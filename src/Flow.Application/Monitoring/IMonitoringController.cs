using Flow.Domain.Monitoring;

namespace Flow.Application.Monitoring;

public interface IMonitoringController
{
    MonitoringState State { get; }
    string? LastError { get; }
    DateTimeOffset? LastEventAtUtc { get; }
    Task StartMonitoringAsync(CancellationToken cancellationToken = default);
    Task PauseMonitoringAsync(CancellationToken cancellationToken = default);
}
