using Flow.Contracts.Monitoring;

namespace Flow.Application.Monitoring;

public interface IMonitoringStatusService
{
    MonitoringStatusDto GetStatus();
}
