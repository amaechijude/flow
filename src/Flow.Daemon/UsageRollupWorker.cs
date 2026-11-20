using Flow.Application.Persistence;

namespace Flow.Daemon;

/// <summary>
/// Periodically rebuilds coarser buckets from persisted 1-minute buckets.
/// Rollup failures are logged and retried on the next tick; they do not stop collection.
/// </summary>
public sealed partial class UsageRollupWorker(
    IUsageRollupService rollups,
    IUsageRetentionService retention,
    ILogger<UsageRollupWorker> logger,
    IDurableUsageQueue? durableQueue = null) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(5));
        do
        {
            try
            {
                var through = await SafeRollupThroughAsync(stoppingToken).ConfigureAwait(false);
                var written = await rollups.RebuildThroughAsync(through, stoppingToken).ConfigureAwait(false);
                if (written > 0)
                    LogRollupsRebuilt(logger, written);

                var now = DateTimeOffset.UtcNow;
                var deleted = await retention.ApplyAsync(now, stoppingToken).ConfigureAwait(false);
                if (deleted > 0)
                    LogRetentionApplied(logger, deleted);
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

    /// <summary>
    /// Do not advance the rollup watermark past the oldest durable delta that
    /// has not been applied yet. Otherwise a delta applied after a long outage
    /// would fall outside the rebuild lookback and be missing from rollups.
    /// </summary>
    private async Task<DateTimeOffset> SafeRollupThroughAsync(CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        if (durableQueue is null) return now;
        try
        {
            var oldest = await durableQueue.OldestPendingAtUtcAsync(ct).ConfigureAwait(false);
            if (oldest.HasValue && oldest.Value < now)
                return oldest.Value.AddMinutes(-1);
        }
        catch { /* queue diagnostics are best effort */ }
        return now;
    }

    [LoggerMessage(EventId = 3001, Level = LogLevel.Debug, Message = "Rebuilt {Rows} usage rollup rows")]
    private static partial void LogRollupsRebuilt(ILogger logger, int rows);

    [LoggerMessage(EventId = 3002, Level = LogLevel.Warning, Message = "Usage rollup rebuild failed; will retry")]
    private static partial void LogRollupFailed(ILogger logger, Exception exception);

    [LoggerMessage(EventId = 3003, Level = LogLevel.Debug, Message = "Usage retention removed {Rows} expired buckets")]
    private static partial void LogRetentionApplied(ILogger logger, int rows);
}
