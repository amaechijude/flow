namespace Flow.Domain.Monitoring;

public enum MonitoringState
{
    Initializing = 0,
    Monitoring = 1,
    Paused = 2,
    Degraded = 3,
    Unavailable = 4
}
