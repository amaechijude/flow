using System.Globalization;
using Flow.Application.Persistence;
using Microsoft.Data.Sqlite;

namespace Flow.Infrastructure.Sqlite;

/// <summary>
/// SQLite-backed durable queue for 1-minute deltas. Applying a batch and
/// deleting its queue rows share one transaction, so interruption between the
/// two is impossible; an interrupted transaction simply leaves rows pending.
/// </summary>
public sealed class SqliteDurableUsageQueue : IDurableUsageQueue
{
    private readonly SqliteConnectionFactory _factory;
    private readonly int _batchSize;

    public SqliteDurableUsageQueue(SqliteConnectionFactory factory, int batchSize = 500)
    {
        _factory = factory;
        _batchSize = Math.Clamp(batchSize, 1, 900);
    }

    public Task EnqueueAsync(IReadOnlyCollection<UsageBucketDelta> deltas, CancellationToken cancellationToken = default)
    {
        if (deltas is null || deltas.Count == 0) return Task.CompletedTask;
        cancellationToken.ThrowIfCancellationRequested();

        using var conn = _factory.CreateOpenConnection();
        using var tx = conn.BeginTransaction();
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO pending_usage_deltas (
                bucket_start_utc, application_key, process_stable_key, interface_id,
                rx_bytes, tx_bytes, is_attributed, attribution_note, measurement_provider, measurement_hook,
                enqueued_at_utc, attempts, last_error
            ) VALUES (
                $start, $app, $proc, $iface, $rx, $tx, $attr, $note, $prov, $hook, $enqueued, 0, NULL
            );
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
        var pEnqueued = cmd.Parameters.Add("$enqueued", SqliteType.Text);
        var enqueued = DateTimeOffset.UtcNow.ToString("O");

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
            pEnqueued.Value = enqueued;
            cmd.ExecuteNonQuery();
        }
        tx.Commit();
        return Task.CompletedTask;
    }

    public Task<int> FlushOnceAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        using var conn = _factory.CreateOpenConnection();
        using var tx = conn.BeginTransaction();
        var rows = ReadBatch(conn, tx, cancellationToken);
        if (rows.Count == 0)
        {
            tx.Commit();
            return Task.FromResult(0);
        }

        // No catch here: on any failure the transaction is rolled back by dispose,
        // all rows remain pending, and the exception propagates so the pipeline
        // can apply backoff and record diagnostics.
        {
            using (var upsert = conn.CreateCommand())
            {
                upsert.Transaction = tx;
                upsert.CommandText = UpsertSql;
                var pStart = upsert.Parameters.Add("$start", SqliteType.Text);
                var pApp = upsert.Parameters.Add("$app", SqliteType.Text);
                var pProc = upsert.Parameters.Add("$proc", SqliteType.Text);
                var pIface = upsert.Parameters.Add("$iface", SqliteType.Text);
                var pRx = upsert.Parameters.Add("$rx", SqliteType.Integer);
                var pTx = upsert.Parameters.Add("$tx", SqliteType.Integer);
                var pAttr = upsert.Parameters.Add("$attr", SqliteType.Integer);
                var pNote = upsert.Parameters.Add("$note", SqliteType.Text);
                var pProv = upsert.Parameters.Add("$prov", SqliteType.Text);
                var pHook = upsert.Parameters.Add("$hook", SqliteType.Text);
                foreach (var row in rows)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    pStart.Value = row.BucketStartUtc;
                    pApp.Value = (object?)row.ApplicationKey ?? DBNull.Value;
                    pProc.Value = (object?)row.ProcessStableKey ?? DBNull.Value;
                    pIface.Value = (object?)row.InterfaceId ?? DBNull.Value;
                    pRx.Value = row.RxBytes;
                    pTx.Value = row.TxBytes;
                    pAttr.Value = row.IsAttributed ? 1 : 0;
                    pNote.Value = (object?)row.AttributionNote ?? DBNull.Value;
                    pProv.Value = (object?)row.MeasurementProvider ?? DBNull.Value;
                    pHook.Value = (object?)row.MeasurementHook ?? DBNull.Value;
                    upsert.ExecuteNonQuery();
                }
            }

            using (var delete = conn.CreateCommand())
            {
                delete.Transaction = tx;
                var parameters = new string[rows.Count];
                for (var i = 0; i < rows.Count; i++)
                {
                    parameters[i] = $"$id{i}";
                    delete.Parameters.AddWithValue(parameters[i], rows[i].Id);
                }
                delete.CommandText = $"DELETE FROM pending_usage_deltas WHERE id IN ({string.Join(',', parameters)});";
                delete.ExecuteNonQuery();
            }

            tx.Commit();
            return Task.FromResult(rows.Count);
        }
    }

    public Task<int> PendingCountAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var conn = _factory.CreateOpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM pending_usage_deltas;";
        return Task.FromResult((int)(long)(cmd.ExecuteScalar() ?? 0L));
    }

    public Task<DateTimeOffset?> OldestPendingAtUtcAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var conn = _factory.CreateOpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT MIN(enqueued_at_utc) FROM pending_usage_deltas;";
        var value = cmd.ExecuteScalar() as string;
        DateTimeOffset? parsed = string.IsNullOrWhiteSpace(value)
            ? null
            : DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
        return Task.FromResult(parsed);
    }

    public Task RecordFailureAsync(Exception exception, CancellationToken cancellationToken = default)
    {
        try
        {
            using var conn = _factory.CreateOpenConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                UPDATE pending_usage_deltas
                SET attempts = attempts + 1, last_error = $error
                WHERE id IN (SELECT id FROM pending_usage_deltas ORDER BY id LIMIT $limit);
                """;
            cmd.Parameters.AddWithValue("$error", exception.Message);
            cmd.Parameters.AddWithValue("$limit", _batchSize);
            cmd.ExecuteNonQuery();
        }
        catch
        {
            // Recording a failure must never replace the original failure.
        }
        return Task.CompletedTask;
    }

    private List<PendingRow> ReadBatch(SqliteConnection conn, SqliteTransaction tx, CancellationToken ct)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            SELECT id, bucket_start_utc, application_key, process_stable_key, interface_id,
                   rx_bytes, tx_bytes, is_attributed, attribution_note, measurement_provider, measurement_hook
            FROM pending_usage_deltas
            ORDER BY id
            LIMIT $limit;
            """;
        cmd.Parameters.AddWithValue("$limit", _batchSize);
        using var reader = cmd.ExecuteReader();
        var rows = new List<PendingRow>();
        while (reader.Read())
        {
            ct.ThrowIfCancellationRequested();
            rows.Add(new PendingRow(
                reader.GetInt64(0),
                reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.GetInt64(5),
                reader.GetInt64(6),
                reader.GetInt32(7) != 0,
                reader.IsDBNull(8) ? null : reader.GetString(8),
                reader.IsDBNull(9) ? null : reader.GetString(9),
                reader.IsDBNull(10) ? null : reader.GetString(10)));
        }
        return rows;
    }

    private static DateTimeOffset AlignToMinuteUtc(DateTimeOffset valueUtc)
    {
        var utc = valueUtc.ToUniversalTime();
        return new DateTimeOffset(utc.Year, utc.Month, utc.Day, utc.Hour, utc.Minute, 0, TimeSpan.Zero);
    }

    private const string UpsertSql = """
        INSERT INTO usage_buckets (
            bucket_start_utc, bucket_seconds, granularity, application_key, process_stable_key, interface_id,
            rx_bytes, tx_bytes, is_attributed, attribution_note, measurement_provider, measurement_hook
        ) VALUES (
            $start, 60, '1m', $app, $proc, $iface, $rx, $tx, $attr, $note, $prov, $hook
        )
        ON CONFLICT(bucket_start_utc, bucket_seconds, granularity, COALESCE(application_key,''), COALESCE(process_stable_key,''), COALESCE(interface_id,''), is_attributed)
        DO UPDATE SET
            rx_bytes = rx_bytes + excluded.rx_bytes,
            tx_bytes = tx_bytes + excluded.tx_bytes,
            attribution_note = COALESCE(excluded.attribution_note, usage_buckets.attribution_note),
            measurement_provider = COALESCE(excluded.measurement_provider, usage_buckets.measurement_provider),
            measurement_hook = COALESCE(excluded.measurement_hook, usage_buckets.measurement_hook);
        """;

    private sealed record PendingRow(
        long Id,
        string BucketStartUtc,
        string? ApplicationKey,
        string? ProcessStableKey,
        string? InterfaceId,
        long RxBytes,
        long TxBytes,
        bool IsAttributed,
        string? AttributionNote,
        string? MeasurementProvider,
        string? MeasurementHook);
}
