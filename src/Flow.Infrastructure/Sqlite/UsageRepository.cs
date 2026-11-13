using Flow.Application.Persistence;
using Flow.Contracts.Usage;
using Microsoft.Data.Sqlite;

namespace Flow.Infrastructure.Sqlite;

public sealed class UsageRepository : IUsageBucketStore
{
    private readonly SqliteConnectionFactory _factory;

    public UsageRepository(SqliteConnectionFactory factory)
    {
        _factory = factory;
    }

    public Task<UsageSummaryDto> GetSummaryAsync(DateTimeOffset startUtc, DateTimeOffset endUtc, string? interfaceId = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var conn = _factory.CreateOpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT
              COALESCE(SUM(CASE WHEN is_attributed = 1 THEN rx_bytes ELSE 0 END), 0) AS rx_attr,
              COALESCE(SUM(CASE WHEN is_attributed = 1 THEN tx_bytes ELSE 0 END), 0) AS tx_attr,
              COALESCE(SUM(CASE WHEN is_attributed = 0 THEN rx_bytes ELSE 0 END), 0) AS rx_unattr,
              COALESCE(SUM(CASE WHEN is_attributed = 0 THEN tx_bytes ELSE 0 END), 0) AS tx_unattr
            FROM usage_buckets
            WHERE bucket_start_utc >= $start AND bucket_start_utc < $end
              AND ($iface IS NULL OR interface_id = $iface);
            """;
        cmd.Parameters.AddWithValue("$start", startUtc.ToString("O"));
        cmd.Parameters.AddWithValue("$end", endUtc.ToString("O"));
        cmd.Parameters.AddWithValue("$iface", (object?)interfaceId ?? DBNull.Value);

        using var reader = cmd.ExecuteReader();
        long rx = 0, tx = 0, rxu = 0, txu = 0;
        if (reader.Read())
        {
            rx = reader.IsDBNull(0) ? 0 : reader.GetInt64(0);
            tx = reader.IsDBNull(1) ? 0 : reader.GetInt64(1);
            rxu = reader.IsDBNull(2) ? 0 : reader.GetInt64(2);
            txu = reader.IsDBNull(3) ? 0 : reader.GetInt64(3);
        }

        return Task.FromResult(new UsageSummaryDto(startUtc, endUtc, interfaceId, rx, tx, rxu, txu));
    }

    public Task UpsertMinuteBucketAsync(
        DateTimeOffset bucketStartUtc,
        string? applicationKey,
        string? processStableKey,
        string? interfaceId,
        long rxBytesDelta,
        long txBytesDelta,
        bool isAttributed,
        string? attributionNote,
        string? measurementProvider,
        string? measurementHook,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (rxBytesDelta == 0 && txBytesDelta == 0) return Task.CompletedTask;

        using var conn = _factory.CreateOpenConnection();
        using var tx = conn.BeginTransaction();
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO usage_buckets (
                bucket_start_utc, bucket_seconds, granularity, application_key, process_stable_key, interface_id,
                rx_bytes, tx_bytes, is_attributed, attribution_note, measurement_provider, measurement_hook
            ) VALUES (
                $start, 60, '1m', $app, $proc, $iface, $rx, $tx, $attr, $note, $prov, $hook
            )
            ON CONFLICT(bucket_start_utc, bucket_seconds, granularity, application_key, process_stable_key, interface_id, is_attributed)
            DO UPDATE SET
                rx_bytes = rx_bytes + excluded.rx_bytes,
                tx_bytes = tx_bytes + excluded.tx_bytes,
                attribution_note = COALESCE(excluded.attribution_note, usage_buckets.attribution_note),
                measurement_provider = COALESCE(excluded.measurement_provider, usage_buckets.measurement_provider),
                measurement_hook = COALESCE(excluded.measurement_hook, usage_buckets.measurement_hook);
            """;
        cmd.Parameters.AddWithValue("$start", AlignToMinuteUtc(bucketStartUtc).ToString("O"));
        cmd.Parameters.AddWithValue("$app", (object?)applicationKey ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$proc", (object?)processStableKey ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$iface", (object?)interfaceId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$rx", rxBytesDelta);
        cmd.Parameters.AddWithValue("$tx", txBytesDelta);
        cmd.Parameters.AddWithValue("$attr", isAttributed ? 1 : 0);
        cmd.Parameters.AddWithValue("$note", (object?)attributionNote ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$prov", (object?)measurementProvider ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$hook", (object?)measurementHook ?? DBNull.Value);
        cmd.ExecuteNonQuery();
        tx.Commit();
        return Task.CompletedTask;
    }

    public Task WriteMinuteDeltasAsync(IReadOnlyCollection<UsageBucketDelta> deltas, CancellationToken cancellationToken = default)
    {
        if (deltas is null || deltas.Count == 0) return Task.CompletedTask;
        cancellationToken.ThrowIfCancellationRequested();

        using var conn = _factory.CreateOpenConnection();
        using var tx = conn.BeginTransaction();
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO usage_buckets (
                bucket_start_utc, bucket_seconds, granularity, application_key, process_stable_key, interface_id,
                rx_bytes, tx_bytes, is_attributed, attribution_note, measurement_provider, measurement_hook
            ) VALUES (
                $start, 60, '1m', $app, $proc, $iface, $rx, $tx, $attr, $note, $prov, $hook
            )
            ON CONFLICT(bucket_start_utc, bucket_seconds, granularity, application_key, process_stable_key, interface_id, is_attributed)
            DO UPDATE SET
                rx_bytes = rx_bytes + excluded.rx_bytes,
                tx_bytes = tx_bytes + excluded.tx_bytes,
                attribution_note = COALESCE(excluded.attribution_note, usage_buckets.attribution_note),
                measurement_provider = COALESCE(excluded.measurement_provider, usage_buckets.measurement_provider),
                measurement_hook = COALESCE(excluded.measurement_hook, usage_buckets.measurement_hook);
            """;
        var pStart = cmd.Parameters.Add("$start", SqliteType.Text);
        var pApp = cmd.Parameters.Add("$app", SqliteType.Text);
        var pProc = cmd.Parameters.Add("$proc", SqliteType.Text);
        var pIface = cmd.Parameters.Add("$iface", SqliteType.Text);
        var pRx = cmd.Parameters.Add("$rx", SqliteType.Integer);
        var pTx = cmd.Parameters.Add("$tx", SqliteType.Integer);
        var pAttr = cmd.Parameters.Add("$attr", SqliteType.Integer);
        var pNote = cmd.Parameters.Add("$note", SqliteType.Text);
        var pProv = cmd.Parameters.Add("$prov", SqliteType.Text);
        var pHook = cmd.Parameters.Add("$hook", SqliteType.Text);

        foreach (var d in deltas)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (d.RxBytesDelta == 0 && d.TxBytesDelta == 0) continue;
            pStart.Value = AlignToMinuteUtc(d.BucketStartUtc).ToString("O");
            pApp.Value = (object?)d.ApplicationKey ?? DBNull.Value;
            pProc.Value = (object?)d.ProcessStableKey ?? DBNull.Value;
            pIface.Value = (object?)d.InterfaceId ?? DBNull.Value;
            pRx.Value = d.RxBytesDelta;
            pTx.Value = d.TxBytesDelta;
            pAttr.Value = d.IsAttributed ? 1 : 0;
            pNote.Value = (object?)d.AttributionNote ?? DBNull.Value;
            pProv.Value = (object?)d.MeasurementProvider ?? DBNull.Value;
            pHook.Value = (object?)d.MeasurementHook ?? DBNull.Value;
            cmd.ExecuteNonQuery();
        }

        tx.Commit();
        return Task.CompletedTask;
    }

    public static DateTimeOffset AlignToMinuteUtc(DateTimeOffset valueUtc)
    {
        var utc = valueUtc.ToUniversalTime();
        return new DateTimeOffset(utc.Year, utc.Month, utc.Day, utc.Hour, utc.Minute, 0, TimeSpan.Zero);
    }
}
