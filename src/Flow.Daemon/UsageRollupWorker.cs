using Flow.Application.Persistence;

namespace Flow.Daemon;

/// <summary>
/// Periodically rebuilds coarser buckets from persisted 1-minute buckets.
/// Rollup failures are logged and retried on the next tick; they do not stop collection.
/// </summary>
public sealed partial class UsageRollupWorker(
    IUsageRollupService rollups,
    ILogger<UsageRollupWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(5));
        do
        {
            try
            {
                var written = await rollups.RebuildThroughAsync(DateTimeOffset.UtcNow, stoppingToken).ConfigureAwait(false);
                if (written > 0)
                    LogRollupsRebuilt(logger, written);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                LogRollupFailed(logger, ex);
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
    }

    [LoggerMessage(EventId = 3001, Level = LogLevel.Debug, Message = "Rebuilt {Rows} usage rollup rows")]
    private static partial void LogRollupsRebuilt(ILogger logger, int rows);

    [LoggerMessage(EventId = 3002, Level = LogLevel.Warning, Message = "Usage rollup rebuild failed; will retry")]
    private static partial void LogRollupFailed(ILogger logger, Exception exception);
}
