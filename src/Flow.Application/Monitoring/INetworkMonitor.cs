namespace Flow.Application.Monitoring;

public interface INetworkMonitor
{
    Task StartAsync(CancellationToken cancellationToken);
    Task StopAsync(CancellationToken cancellationToken);
    IAsyncEnumerable<NetworkUsageEvent> ReadEventsAsync(CancellationToken cancellationToken);
}
