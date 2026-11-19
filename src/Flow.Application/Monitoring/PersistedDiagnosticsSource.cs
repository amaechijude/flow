using Flow.Application.Persistence;
using Flow.Domain.Monitoring;

namespace Flow.Application.Monitoring;

/// <summary>
/// Supplies status from the latest persisted monitoring session for the
/// unprivileged session daemon. Live pipeline counters are intentionally not
/// fabricated: the collector owns them, and this source reports zero counters.
/// </summary>
public sealed class PersistedDiagnosticsSource(IMonitoringMetadataStore metadata) : IMonitoringDiagnosticsSource
{
    public MonitoringDiagnosticsSnapshot GetDiagnosticsSnapshot()
    {
        var session = metadata.GetLatestMonitoringSession();
        if (session is null)
        {
            return new MonitoringDiagnosticsSnapshot(
                MonitoringState.Unavailable,
                null,
                null,
                "No monitoring session recorded yet",
                null,
                0, 0, 0, 0, 0);
        }

        var message = string.IsNullOrWhiteSpace(session.Diagnostics)
            ? "Status from persisted monitoring session; live counters unavailable"
            : session.Diagnostics;

        return new MonitoringDiagnosticsSnapshot(
            session.HealthState,
            session.EndedAtUtc ?? session.StartedAtUtc,
            null,
            message,
            session.ProviderName,
            0, 0, 0, 0, 0);
    }
}
