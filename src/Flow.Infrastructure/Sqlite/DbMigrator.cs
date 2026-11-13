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
}
