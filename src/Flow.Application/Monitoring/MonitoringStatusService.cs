using Flow.Contracts.Monitoring;

namespace Flow.Application.Monitoring;

public sealed class MonitoringStatusService : IMonitoringStatusService
{
    private readonly IMonitoringDiagnosticsSource _diagnostics;

    public MonitoringStatusService(IMonitoringDiagnosticsSource diagnostics)
    {
        _diagnostics = diagnostics;
    }

    public MonitoringStatusDto GetStatus()
    {
        var d = _diagnostics.GetDiagnosticsSnapshot();
        var lastUpdated = d.LastFlushAtUtc ?? d.LastEventAtUtc;
        var health = d.LastError is not null ? d.LastError : d.State.ToString();
        return new MonitoringStatusDto(
            d.State,
            lastUpdated,
            d.ProviderName,
            null,
            health,
            d.EventsAggregated,
            null,
            d.LastEventAtUtc,
            d.LastFlushAtUtc,
            d.LastError,
            d.EventsReceived,
            d.EventsDropped,
            d.EventsAggregated,
            d.BatchesWritten,
            d.WriteErrors,
            d.PendingDurableDeltas,
            d.OldestPendingAtUtc,
            d.DurableFlushAttempts,
            d.NextRetryAtUtc,
            d.RetryBackoffSeconds);
    }
}
