using System.Runtime.CompilerServices;
using Flow.Domain.Measurement;

namespace Flow.Application.Monitoring;

public sealed class NullNetworkMonitor : INetworkMonitor
{
    public static readonly MeasurementPoint MeasurementPoint = new("null", "none", "No monitoring provider configured.");

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public async IAsyncEnumerable<NetworkUsageEvent> ReadEventsAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        // No events; keep awaiting until canceled.
        try
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            yield break;
        }
        yield break;
    }
}
