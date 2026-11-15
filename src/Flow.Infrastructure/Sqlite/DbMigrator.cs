using Microsoft.Data.Sqlite;

namespace Flow.Infrastructure.Sqlite;

public static class DbMigrator
{
    public static void Migrate(SqliteConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        using var tx = connection.BeginTransaction();

        Execute(connection, tx, """
            CREATE TABLE IF NOT EXISTS schema_migrations (
                version INTEGER PRIMARY KEY,
                name TEXT NOT NULL,
                applied_at_utc TEXT NOT NULL
            );
            """);

        Apply(connection, tx, 1, "initial_schema", InitialSchemaSql);
        Apply(connection, tx, 2, "usage_buckets_null_safe_uniqueness", UsageBucketsNullSafeUniquenessSql);

        tx.Commit();
    }

    private static void Apply(SqliteConnection connection, SqliteTransaction tx, int version, string name, string sql)
    {
        using (var check = connection.CreateCommand())
        {
            check.Transaction = tx;
            check.CommandText = "SELECT 1 FROM schema_migrations WHERE version = $v LIMIT 1;";
            check.Parameters.AddWithValue("$v", version);
            if (check.ExecuteScalar() is not null)
                return;
        }

        Execute(connection, tx, sql);

        using var ins = connection.CreateCommand();
        ins.Transaction = tx;
        ins.CommandText = "INSERT INTO schema_migrations(version, name, applied_at_utc) VALUES ($v,$n,$t);";
        ins.Parameters.AddWithValue("$v", version);
        ins.Parameters.AddWithValue("$n", name);
        ins.Parameters.AddWithValue("$t", DateTimeOffset.UtcNow.ToString("O"));
        ins.ExecuteNonQuery();
    }

    private static void Execute(SqliteConnection connection, SqliteTransaction tx, string sql)
    {
        using var cmd = connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private const string InitialSchemaSql = """
        CREATE TABLE IF NOT EXISTS applications (
            application_key TEXT PRIMARY KEY,
            display_name TEXT NOT NULL,
            desktop_file_id TEXT NULL,
            executable_path TEXT NULL,
            icon_ref TEXT NULL,
            first_seen_utc TEXT NOT NULL,
            last_seen_utc TEXT NOT NULL
        );

        CREATE TABLE IF NOT EXISTS processes (
            process_stable_key TEXT PRIMARY KEY,
            pid INTEGER NOT NULL,
            start_time_utc TEXT NOT NULL,
            executable_path TEXT NULL,
            uid INTEGER NULL,
            cgroup_id TEXT NULL,
            network_namespace_id TEXT NULL,
            application_key TEXT NULL,
            first_seen_utc TEXT NOT NULL,
            last_seen_utc TEXT NOT NULL,
            FOREIGN KEY(application_key) REFERENCES applications(application_key)
        );

        CREATE TABLE IF NOT EXISTS network_interfaces (
            interface_id TEXT PRIMARY KEY,
            interface_name TEXT NOT NULL,
            interface_type TEXT NULL,
            first_seen_utc TEXT NOT NULL,
            last_seen_utc TEXT NOT NULL
        );

        CREATE TABLE IF NOT EXISTS usage_buckets (
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            bucket_start_utc TEXT NOT NULL,
            bucket_seconds INTEGER NOT NULL,
            granularity TEXT NOT NULL,
            application_key TEXT NULL,
            process_stable_key TEXT NULL,
            interface_id TEXT NULL,
            rx_bytes INTEGER NOT NULL DEFAULT 0,
            tx_bytes INTEGER NOT NULL DEFAULT 0,
            is_attributed INTEGER NOT NULL DEFAULT 0,
            attribution_note TEXT NULL,
            measurement_provider TEXT NULL,
            measurement_hook TEXT NULL,
            UNIQUE(bucket_start_utc, bucket_seconds, granularity, application_key, process_stable_key, interface_id, is_attributed)
        );

        CREATE TABLE IF NOT EXISTS interface_usage_buckets (
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            bucket_start_utc TEXT NOT NULL,
            bucket_seconds INTEGER NOT NULL,
            granularity TEXT NOT NULL,
            interface_id TEXT NOT NULL,
            rx_bytes INTEGER NOT NULL DEFAULT 0,
            tx_bytes INTEGER NOT NULL DEFAULT 0,
            measurement_provider TEXT NULL,
            measurement_hook TEXT NULL,
            UNIQUE(bucket_start_utc, bucket_seconds, granularity, interface_id)
        );

        CREATE TABLE IF NOT EXISTS monitoring_sessions (
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            started_at_utc TEXT NOT NULL,
            ended_at_utc TEXT NULL,
            provider_name TEXT NULL,
            measurement_version TEXT NULL,
            health_state TEXT NOT NULL,
            diagnostics TEXT NULL
        );

        CREATE TABLE IF NOT EXISTS user_settings (
            key TEXT PRIMARY KEY,
            value TEXT NOT NULL,
            updated_at_utc TEXT NOT NULL
        );

        CREATE INDEX IF NOT EXISTS idx_usage_buckets_time ON usage_buckets(bucket_start_utc, granularity);
        CREATE INDEX IF NOT EXISTS idx_usage_buckets_app_time ON usage_buckets(application_key, bucket_start_utc);
        CREATE INDEX IF NOT EXISTS idx_usage_buckets_proc_time ON usage_buckets(process_stable_key, bucket_start_utc);
        CREATE INDEX IF NOT EXISTS idx_usage_buckets_iface_time ON usage_buckets(interface_id, bucket_start_utc);
        CREATE INDEX IF NOT EXISTS idx_iface_usage_time ON interface_usage_buckets(interface_id, bucket_start_utc, granularity);
        CREATE INDEX IF NOT EXISTS idx_processes_app ON processes(application_key);
        """;

    // SQLite UNIQUE treats NULLs as distinct, so the v1 table-level UNIQUE could not prevent duplicates when
    // app/process/interface dimensions were NULL (unattributed or missing attribution). v2 consolidates existing rows
    // by logical bucket key and enforces a NULL-safe unique index used by ON CONFLICT upserts.
    private const string UsageBucketsNullSafeUniquenessSql = """
        CREATE TEMP TABLE usage_buckets_dedup AS
        SELECT
            MIN(id) AS id,
            bucket_start_utc,
            bucket_seconds,
            granularity,
            application_key,
            process_stable_key,
            interface_id,
            SUM(rx_bytes) AS rx_bytes,
            SUM(tx_bytes) AS tx_bytes,
            is_attributed,
            MAX(attribution_note) AS attribution_note,
            MAX(measurement_provider) AS measurement_provider,
            MAX(measurement_hook) AS measurement_hook
        FROM usage_buckets
        GROUP BY
            bucket_start_utc,
            bucket_seconds,
            granularity,
            COALESCE(application_key, ''),
            COALESCE(process_stable_key, ''),
            COALESCE(interface_id, ''),
            is_attributed;

        DELETE FROM usage_buckets;

        INSERT INTO usage_buckets (
            id, bucket_start_utc, bucket_seconds, granularity, application_key, process_stable_key, interface_id,
            rx_bytes, tx_bytes, is_attributed, attribution_note, measurement_provider, measurement_hook
        )
        SELECT
            id, bucket_start_utc, bucket_seconds, granularity, application_key, process_stable_key, interface_id,
            rx_bytes, tx_bytes, is_attributed, attribution_note, measurement_provider, measurement_hook
        FROM usage_buckets_dedup;

        DROP TABLE usage_buckets_dedup;

        CREATE UNIQUE INDEX IF NOT EXISTS ux_usage_buckets_logical
        ON usage_buckets (
            bucket_start_utc,
            bucket_seconds,
            granularity,
            COALESCE(application_key, ''),
            COALESCE(process_stable_key, ''),
            COALESCE(interface_id, ''),
            is_attributed
        );
        """;
}
