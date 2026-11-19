namespace Flow.Daemon;

/// <summary>
/// Runtime topology for the daemon. Full is the development default; collector
/// is the privileged headless role used by the packaging plan.
/// </summary>
public enum DaemonMode
{
    Full,
    Collector,
    Session,
}

public static class DaemonModeParser
{
    public static DaemonMode Parse(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        null or "" or "full" or "development" => DaemonMode.Full,
        "collector" or "headless" => DaemonMode.Collector,
        "session" or "ui" => DaemonMode.Session,
        _ => throw new InvalidOperationException(
            $"Unknown Flow:Mode '{value}'. Expected 'full', 'collector', or 'session'."),
    };
}
