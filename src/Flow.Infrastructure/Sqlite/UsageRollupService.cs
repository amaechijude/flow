using System.Globalization;
using Flow.Application.Persistence;
using Flow.Application.Queries;
using Microsoft.Data.Sqlite;

namespace Flow.Infrastructure.Sqlite;

/// <summary>
/// Rebuilds coarser buckets in tiers: 5m from 1m, 1h from 5m, 1d from 1h,
/// and 1mo from 1d. Each tier recomputes only the buckets that can be derived
/// from currently available source rows, and never deletes rollups whose
/// finer source data has already been dropped by retention.
/// </summary>
public sealed class UsageRollupService : IUsageRollupService
{
    private static readonly (string Source, string Target)[] Tiers =
    [
        ("1m", "5m"),
        ("5m", "1h"),
        ("1h", "1d"),
        ("1d", "1mo"),
    ];

    private static readonly TimeSpan RebuildLookback = TimeSpan.FromHours(2);

    private readonly SqliteConnectionFactory _factory;
    private readonly UsageRollupState _state;

    public UsageRollupService(SqliteConnectionFactory factory)
    {
        _factory = factory;
        _state = new UsageRollupState(factory);
    }

    public Task<int> RebuildThroughAsync(DateTimeOffset throughUtc, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var through = throughUtc.ToUniversalTime();
        var last = _state.GetLastRolledMinuteUtc()?.ToUniversalTime();
        var incrementalFrom = last.HasValue && last.Value > DateTimeOffset.MinValue + RebuildLookback
            ? last.Value - RebuildLookback
            : DateTimeOffset.MinValue;

        var written = 0;
        using var conn = _factory.CreateOpenConnection();
        using var tx = conn.BeginTransaction();

        foreach (var (source, target) in Tiers)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var sourceMin = MinBucketStart(conn, tx, source);
            if (sourceMin is null) continue;

            var targetMin = MinBucketStart(conn, tx, target);
            // Backfill when the target is missing history the source still has
            // (fresh DB, a previous rollup failure, or a restored backup).
            // Otherwise recompute only the recent lookback; late-arriving
            // deltas are safe because rows are replaced, not incremented.
            var from = targetMin is null || sourceMin.Value < targetMin.Value
                ? sourceMin.Value
                : incrementalFrom;

            // Delete whole target buckets, and read source rows, from the same
            // aligned boundary. Buckets outside this horizon are never touched,
            // so retention of finer data cannot silently erase older rollups.
            var alignedFrom = AlignDown(from, target);
            if (alignedFrom >= through) continue;

            var rows = ReadRows(conn, tx, source, alignedFrom, through, cancellationToken);
            if (rows.Count == 0) continue;

            DeleteRows(conn, tx, target, alignedFrom);
            var rollups = BuildRollups(rows, target);
            written += WriteRollups(conn, tx, rollups, cancellationToken);
        }

        tx.Commit();
        _state.SetLastRolledMinuteUtc(through);
        return Task.FromResult(written);
    }

    private static DateTimeOffset? MinBucketStart(SqliteConnection conn, SqliteTransaction tx, string granularity)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "SELECT MIN(bucket_start_utc) FROM usage_buckets WHERE granularity = $g;";
        cmd.Parameters.AddWithValue("$g", granularity);
        var value = cmd.ExecuteScalar() as string;
        return string.IsNullOrWhiteSpace(value)
            ? null
            : DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
    }

    private static List<BucketRow> ReadRows(
        SqliteConnection conn, SqliteTransaction tx, string granularity,
        DateTimeOffset from, DateTimeOffset through, CancellationToken ct)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            SELECT bucket_start_utc, application_key, process_stable_key, interface_id,
                   rx_bytes, tx_bytes, is_attributed, attribution_note, measurement_provider, measurement_hook
            FROM usage_buckets
            WHERE granularity = $g AND bucket_start_utc >= $from AND bucket_start_utc < $through;
            """;
        cmd.Parameters.AddWithValue("$g", granularity);
        cmd.Parameters.AddWithValue("$from", from.ToString("O"));
        cmd.Parameters.AddWithValue("$through", through.ToString("O"));
        using var reader = cmd.ExecuteReader();
        var rows = new List<BucketRow>();
        while (reader.Read())
        {
            ct.ThrowIfCancellationRequested();
            rows.Add(new BucketRow(
                DateTimeOffset.Parse(reader.GetString(0), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
                reader.IsDBNull(1) ? null : reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.GetInt64(4),
                reader.GetInt64(5),
                reader.GetInt32(6) != 0,
                reader.IsDBNull(7) ? null : reader.GetString(7),
                reader.IsDBNull(8) ? null : reader.GetString(8),
                reader.IsDBNull(9) ? null : reader.GetString(9)));
        }
        return rows;
    }

    private static void DeleteRows(SqliteConnection conn, SqliteTransaction tx, string granularity, DateTimeOffset from)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            DELETE FROM usage_buckets
            WHERE granularity = $g AND bucket_start_utc >= $from;
            """;
        cmd.Parameters.AddWithValue("$g", granularity);
        cmd.Parameters.AddWithValue("$from", from.ToString("O"));
        cmd.ExecuteNonQuery();
    }

    private static List<KeyValuePair<RollupKey, RollupValue>> BuildRollups(List<BucketRow> rows, string target)
    {
        var result = new Dictionary<RollupKey, RollupValue>();
        foreach (var row in rows)
        {
            var (start, seconds) = Align(row.StartUtc, target);
            var key = new RollupKey(start, seconds, target, row.ApplicationKey, row.ProcessStableKey, row.InterfaceId, row.IsAttributed);
            if (result.TryGetValue(key, out var existing))
            {
                result[key] = existing with
                {
                    RxBytes = existing.RxBytes + row.RxBytes,
                    TxBytes = existing.TxBytes + row.TxBytes,
                    AttributionNote = MaxNullable(existing.AttributionNote, row.AttributionNote),
                    MeasurementProvider = MaxNullable(existing.MeasurementProvider, row.MeasurementProvider),
                    MeasurementHook = MaxNullable(existing.MeasurementHook, row.MeasurementHook),
                };
            }
            else
            {
                result[key] = new RollupValue(row.RxBytes, row.TxBytes, row.AttributionNote, row.MeasurementProvider, row.MeasurementHook);
            }
        }
        return result.OrderBy(kv => kv.Key.StartUtc).ToList();
    }

    private static int WriteRollups(
        SqliteConnection conn, SqliteTransaction tx,
        List<KeyValuePair<RollupKey, RollupValue>> rollups, CancellationToken ct)
    {
        using var insert = conn.CreateCommand();
        insert.Transaction = tx;
        insert.CommandText = """
            INSERT INTO usage_buckets (
                bucket_start_utc, bucket_seconds, granularity, application_key, process_stable_key, interface_id,
                rx_bytes, tx_bytes, is_attributed, attribution_note, measurement_provider, measurement_hook
            ) VALUES (
                $start, $seconds, $granularity, $app, $proc, $iface, $rx, $tx, $attr, $note, $prov, $hook
            )
            ON CONFLICT(bucket_start_utc, bucket_seconds, granularity, COALESCE(application_key,''), COALESCE(process_stable_key,''), COALESCE(interface_id,''), is_attributed)
            DO UPDATE SET
                rx_bytes = excluded.rx_bytes,
                tx_bytes = excluded.tx_bytes,
                attribution_note = COALESCE(excluded.attribution_note, usage_buckets.attribution_note),
                measurement_provider = COALESCE(excluded.measurement_provider, usage_buckets.measurement_provider),
                measurement_hook = COALESCE(excluded.measurement_hook, usage_buckets.measurement_hook);
            """;
        var pStart = insert.Parameters.Add("$start", SqliteType.Text);
        var pSeconds = insert.Parameters.Add("$seconds", SqliteType.Integer);
        var pGranularity = insert.Parameters.Add("$granularity", SqliteType.Text);
        var pApp = insert.Parameters.Add("$app", SqliteType.Text);
        var pProc = insert.Parameters.Add("$proc", SqliteType.Text);
        var pIface = insert.Parameters.Add("$iface", SqliteType.Text);
        var pRx = insert.Parameters.Add("$rx", SqliteType.Integer);
        var pTx = insert.Parameters.Add("$tx", SqliteType.Integer);
        var pAttr = insert.Parameters.Add("$attr", SqliteType.Integer);
        var pNote = insert.Parameters.Add("$note", SqliteType.Text);
        var pProv = insert.Parameters.Add("$prov", SqliteType.Text);
        var pHook = insert.Parameters.Add("$hook", SqliteType.Text);

        var written = 0;
        foreach (var rollup in rollups)
        {
            ct.ThrowIfCancellationRequested();
            pStart.Value = rollup.Key.StartUtc.ToString("O");
            pSeconds.Value = rollup.Key.Seconds;
            pGranularity.Value = rollup.Key.Granularity;
            pApp.Value = (object?)rollup.Key.ApplicationKey ?? DBNull.Value;
            pProc.Value = (object?)rollup.Key.ProcessStableKey ?? DBNull.Value;
            pIface.Value = (object?)rollup.Key.InterfaceId ?? DBNull.Value;
            pRx.Value = rollup.Value.RxBytes;
            pTx.Value = rollup.Value.TxBytes;
            pAttr.Value = rollup.Key.IsAttributed ? 1 : 0;
            pNote.Value = (object?)rollup.Value.AttributionNote ?? DBNull.Value;
            pProv.Value = (object?)rollup.Value.MeasurementProvider ?? DBNull.Value;
            pHook.Value = (object?)rollup.Value.MeasurementHook ?? DBNull.Value;
            insert.ExecuteNonQuery();
            written++;
        }
        return written;
    }

    private static string? MaxNullable(string? a, string? b)
    {
        if (a is null) return b;
        if (b is null) return a;
        return string.CompareOrdinal(a, b) >= 0 ? a : b;
    }

    private static (DateTimeOffset Start, int Seconds) Align(DateTimeOffset valueUtc, string granularity)
    {
        var start = UsageGranularityPolicy.AlignDown(valueUtc, granularity);
        return (start, (int)UsagePrecision.BucketDuration(start, granularity).TotalSeconds);
    }

    private static DateTimeOffset AlignDown(DateTimeOffset valueUtc, string granularity)
        => UsageGranularityPolicy.AlignDown(valueUtc, granularity);

    private sealed record BucketRow(
        DateTimeOffset StartUtc,
        string? ApplicationKey,
        string? ProcessStableKey,
        string? InterfaceId,
        long RxBytes,
        long TxBytes,
        bool IsAttributed,
        string? AttributionNote,
        string? MeasurementProvider,
        string? MeasurementHook);

    private sealed record RollupKey(
        DateTimeOffset StartUtc,
        int Seconds,
        string Granularity,
        string? ApplicationKey,
        string? ProcessStableKey,
        string? InterfaceId,
        bool IsAttributed);

    private sealed record RollupValue(
        long RxBytes,
        long TxBytes,
        string? AttributionNote,
        string? MeasurementProvider,
        string? MeasurementHook);
}
