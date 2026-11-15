namespace Flow.Application.Monitoring;

public interface IMonitoringDiagnosticsSource
{
    MonitoringDiagnosticsSnapshot GetDiagnosticsSnapshot();
}
