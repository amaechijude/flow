using Flow.Application.Persistence;
using Flow.Application.Queries;
using Flow.Contracts.Usage;
using Flow.Domain.Identity;
using Flow.Domain.Monitoring;
using Microsoft.Data.Sqlite;

namespace Flow.Infrastructure.Sqlite;

public sealed class UsageRepository : IUsageBucketStore, IMonitoringMetadataStore, IUsageQueryService
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

    public Task<IReadOnlyList<ApplicationUsageDto>> GetTopApplicationsAsync(
        DateTimeOffset startUtc, DateTimeOffset endUtc, string? interfaceId = null, int limit = 20, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var conn = _factory.CreateOpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT
              COALESCE(b.application_key, '(unattributed)') AS app_key,
              a.display_name,
              COALESCE(SUM(CASE WHEN b.is_attributed = 1 THEN b.rx_bytes ELSE 0 END), 0) AS rx_attr,
              COALESCE(SUM(CASE WHEN b.is_attributed = 1 THEN b.tx_bytes ELSE 0 END), 0) AS tx_attr,
              COALESCE(SUM(CASE WHEN b.is_attributed = 0 THEN b.rx_bytes ELSE 0 END), 0) AS rx_unattr,
              COALESCE(SUM(CASE WHEN b.is_attributed = 0 THEN b.tx_bytes ELSE 0 END), 0) AS tx_unattr,
              COALESCE(SUM(b.rx_bytes + b.tx_bytes), 0) AS total
            FROM usage_buckets b
            LEFT JOIN applications a ON a.application_key = b.application_key
            WHERE b.bucket_start_utc >= $start AND b.bucket_start_utc < $end
              AND ($iface IS NULL OR b.interface_id = $iface)
            GROUP BY COALESCE(b.application_key, '(unattributed)'), a.display_name
            ORDER BY total DESC
            LIMIT $limit;
            """;
        cmd.Parameters.AddWithValue("$start", startUtc.ToString("O"));
        cmd.Parameters.AddWithValue("$end", endUtc.ToString("O"));
        cmd.Parameters.AddWithValue("$iface", (object?)interfaceId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$limit", Math.Max(1, limit));
        using var reader = cmd.ExecuteReader();
        var list = new List<ApplicationUsageDto>();
        while (reader.Read())
        {
            list.Add(new ApplicationUsageDto(
                reader.GetString(0),
                reader.IsDBNull(1) ? null : reader.GetString(1),
                reader.IsDBNull(2) ? 0 : reader.GetInt64(2),
                reader.IsDBNull(3) ? 0 : reader.GetInt64(3),
                reader.IsDBNull(4) ? 0 : reader.GetInt64(4),
                reader.IsDBNull(5) ? 0 : reader.GetInt64(5)));
        }
        return Task.FromResult<IReadOnlyList<ApplicationUsageDto>>(list);
    }

    public Task<IReadOnlyList<UsageTimePointDto>> GetUsageTimeSeriesAsync(
        DateTimeOffset startUtc, DateTimeOffset endUtc, string? interfaceId = null, string? applicationKey = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var conn = _factory.CreateOpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT bucket_start_utc,
                   COALESCE(SUM(rx_bytes), 0) AS rx,
                   COALESCE(SUM(tx_bytes), 0) AS tx
            FROM usage_buckets
            WHERE bucket_start_utc >= $start AND bucket_start_utc < $end
              AND ($iface IS NULL OR interface_id = $iface)
              AND ($app IS NULL OR application_key = $app)
            GROUP BY bucket_start_utc
            ORDER BY bucket_start_utc ASC;
            """;
        cmd.Parameters.AddWithValue("$start", startUtc.ToString("O"));
        cmd.Parameters.AddWithValue("$end", endUtc.ToString("O"));
        cmd.Parameters.AddWithValue("$iface", (object?)interfaceId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$app", (object?)applicationKey ?? DBNull.Value);
        using var reader = cmd.ExecuteReader();
        var list = new List<UsageTimePointDto>();
        while (reader.Read())
        {
            var ts = DateTimeOffset.Parse(reader.GetString(0), null, System.Globalization.DateTimeStyles.RoundtripKind);
            list.Add(new UsageTimePointDto(
                ts,
                reader.IsDBNull(1) ? 0 : reader.GetInt64(1),
                reader.IsDBNull(2) ? 0 : reader.GetInt64(2)));
        }
        return Task.FromResult<IReadOnlyList<UsageTimePointDto>>(list);
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
            ON CONFLICT(bucket_start_utc, bucket_seconds, granularity, COALESCE(application_key,''), COALESCE(process_stable_key,''), COALESCE(interface_id,''), is_attributed)
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
            ON CONFLICT(bucket_start_utc, bucket_seconds, granularity, COALESCE(application_key,''), COALESCE(process_stable_key,''), COALESCE(interface_id,''), is_attributed)
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

    public Task UpsertApplicationAsync(ApplicationIdentity application, DateTimeOffset seenAtUtc, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(application);
        cancellationToken.ThrowIfCancellationRequested();
        using var conn = _factory.CreateOpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO applications (application_key, display_name, desktop_file_id, executable_path, icon_ref, first_seen_utc, last_seen_utc)
            VALUES ($key, $name, $desktop, $exe, $icon, $seen, $seen)
            ON CONFLICT(application_key) DO UPDATE SET
                display_name = CASE WHEN excluded.display_name <> '' THEN excluded.display_name ELSE applications.display_name END,
                desktop_file_id = COALESCE(excluded.desktop_file_id, applications.desktop_file_id),
                executable_path = COALESCE(excluded.executable_path, applications.executable_path),
                icon_ref = COALESCE(excluded.icon_ref, applications.icon_ref),
                last_seen_utc = CASE WHEN excluded.last_seen_utc > applications.last_seen_utc THEN excluded.last_seen_utc ELSE applications.last_seen_utc END;
            """;
        cmd.Parameters.AddWithValue("$key", application.ApplicationKey);
        cmd.Parameters.AddWithValue("$name", application.DisplayName);
        cmd.Parameters.AddWithValue("$desktop", (object?)application.DesktopFileId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$exe", (object?)application.ExecutablePath ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$icon", (object?)application.IconRef ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$seen", seenAtUtc.ToUniversalTime().ToString("O"));
        cmd.ExecuteNonQuery();
        return Task.CompletedTask;
    }

    public Task UpsertProcessAsync(ProcessIdentity process, string? applicationKey, DateTimeOffset seenAtUtc, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(process);
        cancellationToken.ThrowIfCancellationRequested();
        using var conn = _factory.CreateOpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO processes (process_stable_key, pid, start_time_utc, executable_path, uid, cgroup_id, network_namespace_id, application_key, first_seen_utc, last_seen_utc)
            VALUES ($key, $pid, $start, $exe, $uid, $cgroup, $netns, $app, $seen, $seen)
            ON CONFLICT(process_stable_key) DO UPDATE SET
                executable_path = COALESCE(excluded.executable_path, processes.executable_path),
                uid = COALESCE(excluded.uid, processes.uid),
                cgroup_id = COALESCE(excluded.cgroup_id, processes.cgroup_id),
                network_namespace_id = COALESCE(excluded.network_namespace_id, processes.network_namespace_id),
                application_key = COALESCE(excluded.application_key, processes.application_key),
                last_seen_utc = CASE WHEN excluded.last_seen_utc > processes.last_seen_utc THEN excluded.last_seen_utc ELSE processes.last_seen_utc END;
            """;
        cmd.Parameters.AddWithValue("$key", process.StableKey);
        cmd.Parameters.AddWithValue("$pid", process.Pid);
        cmd.Parameters.AddWithValue("$start", process.StartTimeUtc.ToUniversalTime().ToString("O"));
        cmd.Parameters.AddWithValue("$exe", (object?)process.ExecutablePath ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$uid", process.Uid.HasValue ? (object)(long)process.Uid.Value : DBNull.Value);
        cmd.Parameters.AddWithValue("$cgroup", (object?)process.CgroupId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$netns", (object?)process.NetworkNamespaceId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$app", (object?)applicationKey ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$seen", seenAtUtc.ToUniversalTime().ToString("O"));
        cmd.ExecuteNonQuery();
        return Task.CompletedTask;
    }

    public Task UpsertInterfaceAsync(string interfaceName, string? interfaceType, DateTimeOffset seenAtUtc, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(interfaceName);
        cancellationToken.ThrowIfCancellationRequested();
        var id = NormalizeInterfaceId(interfaceName);
        using var conn = _factory.CreateOpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO network_interfaces (interface_id, interface_name, interface_type, first_seen_utc, last_seen_utc)
            VALUES ($id, $name, $type, $seen, $seen)
            ON CONFLICT(interface_id) DO UPDATE SET
                interface_name = excluded.interface_name,
                interface_type = COALESCE(excluded.interface_type, network_interfaces.interface_type),
                last_seen_utc = CASE WHEN excluded.last_seen_utc > network_interfaces.last_seen_utc THEN excluded.last_seen_utc ELSE network_interfaces.last_seen_utc END;
            """;
        cmd.Parameters.AddWithValue("$id", id);
        cmd.Parameters.AddWithValue("$name", interfaceName);
        cmd.Parameters.AddWithValue("$type", (object?)interfaceType ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$seen", seenAtUtc.ToUniversalTime().ToString("O"));
        cmd.ExecuteNonQuery();
        return Task.CompletedTask;
    }

    public Task<long> StartMonitoringSessionAsync(string? providerName, string? measurementVersion, MonitoringState healthState, string? diagnostics, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var conn = _factory.CreateOpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO monitoring_sessions (started_at_utc, provider_name, measurement_version, health_state, diagnostics)
            VALUES ($start, $prov, $ver, $health, $diag);
            SELECT last_insert_rowid();
            """;
        cmd.Parameters.AddWithValue("$start", DateTimeOffset.UtcNow.ToString("O"));
        cmd.Parameters.AddWithValue("$prov", (object?)providerName ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$ver", (object?)measurementVersion ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$health", healthState.ToString());
        cmd.Parameters.AddWithValue("$diag", (object?)diagnostics ?? DBNull.Value);
        var id = (long)(cmd.ExecuteScalar() ?? 0L);
        return Task.FromResult(id);
    }

    public Task EndMonitoringSessionAsync(long sessionId, MonitoringState healthState, string? diagnostics, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var conn = _factory.CreateOpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE monitoring_sessions
            SET ended_at_utc = $end, health_state = $health, diagnostics = COALESCE($diag, diagnostics)
            WHERE id = $id;
            """;
        cmd.Parameters.AddWithValue("$end", DateTimeOffset.UtcNow.ToString("O"));
        cmd.Parameters.AddWithValue("$health", healthState.ToString());
        cmd.Parameters.AddWithValue("$diag", (object?)diagnostics ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$id", sessionId);
        cmd.ExecuteNonQuery();
        return Task.CompletedTask;
    }

    public static string NormalizeInterfaceId(string interfaceName) => interfaceName.Trim().ToLowerInvariant();

    public static DateTimeOffset AlignToMinuteUtc(DateTimeOffset valueUtc)
    {
        var utc = valueUtc.ToUniversalTime();
        return new DateTimeOffset(utc.Year, utc.Month, utc.Day, utc.Hour, utc.Minute, 0, TimeSpan.Zero);
    }
}
