namespace Flow.Daemon;

/// <summary>
/// Runtime topology for the daemon. Full is the development default; collector
/// is the privileged headless role used by the packaging plan.
/// </summary>
public enum DaemonMode
{
    Full,
    Collector,
}

public static class DaemonModeParser
{
    public static DaemonMode Parse(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        null or "" or "full" or "development" => DaemonMode.Full,
        "collector" or "headless" => DaemonMode.Collector,
        "session" => throw new InvalidOperationException(
            "Flow:Mode=session is planned but not implemented yet; use 'full' or 'collector'."),
        _ => throw new InvalidOperationException(
            $"Unknown Flow:Mode '{value}'. Expected 'full' or 'collector'."),
    };
}
