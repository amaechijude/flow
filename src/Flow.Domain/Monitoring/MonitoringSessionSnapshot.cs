namespace Flow.Domain.Monitoring;

/// <summary>
/// Latest persisted monitoring session, used by the session daemon to report
/// status without running the privileged collector in-process.
/// </summary>
public sealed record MonitoringSessionSnapshot(
    long Id,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset? EndedAtUtc,
    string? ProviderName,
    string? MeasurementVersion,
    MonitoringState HealthState,
    string? Diagnostics);
