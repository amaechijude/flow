using System.Globalization;
using Flow.Application.Persistence;
using Microsoft.Data.Sqlite;

namespace Flow.Infrastructure.Sqlite;

/// <summary>
/// Rebuilds 5m/1h/1d/1mo buckets from 1m buckets. Rollup rows are recomputed
/// from source rows and replaced, so repeated runs never double-count.
/// </summary>
public sealed class UsageRollupService : IUsageRollupService
{
    private static readonly string[] RollupGranularities = ["5m", "1h", "1d", "1mo"];

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

        // Recompute a recent window even when the watermark has reached 'through':
        // 1-minute deltas for the current/previous minute can arrive late. The
        // delete+recompute cycle makes this idempotent.
        var anchor = last.HasValue && last.Value < through ? last.Value : through;
        var from = last.HasValue ? SubtractLookback(anchor) : DateTimeOffset.MinValue;

        var rows = ReadMinuteRows(from, through, cancellationToken);
        var rollups = BuildRollups(rows);

        using var conn = _factory.CreateOpenConnection();
        using var tx = conn.BeginTransaction();

        using (var delete = conn.CreateCommand())
        {
            delete.Transaction = tx;
            delete.CommandText = """
                DELETE FROM usage_buckets
                WHERE granularity <> '1m' AND bucket_start_utc >= $from;
                """;
            delete.Parameters.AddWithValue("$from", FloorToMonth(from).ToString("O"));
            delete.ExecuteNonQuery();
        }

        var written = 0;
        if (rollups.Count > 0)
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

            foreach (var rollup in rollups)
            {
                cancellationToken.ThrowIfCancellationRequested();
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
        }

        tx.Commit();
        _state.SetLastRolledMinuteUtc(through);
        return Task.FromResult(written);
    }

    private List<MinuteRow> ReadMinuteRows(DateTimeOffset from, DateTimeOffset through, CancellationToken ct)
    {
        using var conn = _factory.CreateOpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT bucket_start_utc, application_key, process_stable_key, interface_id,
                   rx_bytes, tx_bytes, is_attributed, attribution_note, measurement_provider, measurement_hook
            FROM usage_buckets
            WHERE granularity = '1m' AND bucket_start_utc >= $from AND bucket_start_utc < $through;
            """;
        cmd.Parameters.AddWithValue("$from", from.ToString("O"));
        cmd.Parameters.AddWithValue("$through", through.ToString("O"));
        using var reader = cmd.ExecuteReader();
        var rows = new List<MinuteRow>();
        while (reader.Read())
        {
            ct.ThrowIfCancellationRequested();
            rows.Add(new MinuteRow(
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

    private static List<KeyValuePair<RollupKey, RollupValue>> BuildRollups(List<MinuteRow> rows)
    {
        var result = new Dictionary<RollupKey, RollupValue>();
        foreach (var row in rows)
        {
            foreach (var granularity in RollupGranularities)
            {
                var (start, seconds) = Align(row.StartUtc, granularity);
                var key = new RollupKey(start, seconds, granularity, row.ApplicationKey, row.ProcessStableKey, row.InterfaceId, row.IsAttributed);
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
        }
        return result.OrderBy(kv => kv.Key.Granularity).ThenBy(kv => kv.Key.StartUtc).ToList();
    }

    private static string? MaxNullable(string? a, string? b)
    {
        if (a is null) return b;
        if (b is null) return a;
        return string.CompareOrdinal(a, b) >= 0 ? a : b;
    }

    private static (DateTimeOffset Start, int Seconds) Align(DateTimeOffset valueUtc, string granularity)
    {
        var utc = valueUtc.ToUniversalTime();
        return granularity switch
        {
            "5m" => (new DateTimeOffset(utc.Year, utc.Month, utc.Day, utc.Hour, utc.Minute - (utc.Minute % 5), 0, TimeSpan.Zero), 300),
            "1h" => (new DateTimeOffset(utc.Year, utc.Month, utc.Day, utc.Hour, 0, 0, TimeSpan.Zero), 3600),
            "1d" => (new DateTimeOffset(utc.Year, utc.Month, utc.Day, 0, 0, 0, TimeSpan.Zero), 86400),
            "1mo" => (new DateTimeOffset(utc.Year, utc.Month, 1, 0, 0, 0, TimeSpan.Zero), DateTime.DaysInMonth(utc.Year, utc.Month) * 86400),
            _ => throw new ArgumentOutOfRangeException(nameof(granularity), granularity, "Unsupported rollup granularity"),
        };
    }

    private static DateTimeOffset SubtractLookback(DateTimeOffset value)
        => value > DateTimeOffset.MinValue + TimeSpan.FromHours(2)
            ? value - TimeSpan.FromHours(2)
            : DateTimeOffset.MinValue;

    private static DateTimeOffset FloorToMonth(DateTimeOffset valueUtc)
    {
        var utc = valueUtc.ToUniversalTime();
        return new DateTimeOffset(utc.Year, utc.Month, 1, 0, 0, 0, TimeSpan.Zero);
    }

    private sealed record MinuteRow(
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
