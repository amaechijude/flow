using Flow.Contracts.Monitoring;

namespace Flow.Desktop.Services;

public interface IDaemonStatusClient
{
    Task<MonitoringStatusDto> GetStatusAsync(CancellationToken cancellationToken = default);
}
