namespace Flow.Daemon;

/// <summary>
/// Default storage locations. Full/development mode stays per-user; collector
/// and session modes share the packaged system database.
/// </summary>
public static class FlowPaths
{
    public const string SystemDatabasePath = "/var/lib/flow/flow.db";

    public static string DefaultDatabasePath(DaemonMode mode)
    {
        if (mode == DaemonMode.Full)
        {
            var baseDir = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (string.IsNullOrWhiteSpace(baseDir))
                baseDir = AppContext.BaseDirectory;
            return Path.Combine(baseDir, "flow", "flow.db");
        }

        return SystemDatabasePath;
    }
}
