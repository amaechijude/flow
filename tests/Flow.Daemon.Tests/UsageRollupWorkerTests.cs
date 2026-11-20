using Flow.Application.Persistence;
using Flow.Daemon;
using Microsoft.Extensions.Logging;

namespace Flow.Daemon.Tests;

public sealed class UsageRollupWorkerTests
{
    [Fact]
    public async Task RollupDoesNotAdvancePastOldestPendingDurableDelta()
    {
        var oldest = DateTimeOffset.UtcNow - TimeSpan.FromHours(3);
        var queue = new FakeQueue { Oldest = oldest };
        var rollups = new FakeRollups();
        using var cts = new CancellationTokenSource();
        var worker = new UsageRollupWorker(rollups, new FakeRetention(), new SilentLogger<UsageRollupWorker>(), queue);

        await worker.StartAsync(cts.Token);
        try
        {
            var start = DateTime.UtcNow;
            while (rollups.Through is null)
            {
                if (DateTime.UtcNow - start > TimeSpan.FromSeconds(2))
                    throw new TimeoutException("Rollup worker did not run.");
                await Task.Delay(5);
            }
            Assert.True(rollups.Through <= oldest, "Rollup watermark must stay behind pending durable deltas.");
        }
        finally
        {
            await worker.StopAsync(cts.Token);
        }
    }

    private sealed class FakeRollups : IUsageRollupService
    {
        public DateTimeOffset? Through { get; private set; }
        public Task<int> RebuildThroughAsync(DateTimeOffset throughUtc, CancellationToken cancellationToken = default)
        {
            Through = throughUtc;
            return Task.FromResult(0);
        }
    }

    private sealed class FakeRetention : IUsageRetentionService
    {
        public Task<int> ApplyAsync(DateTimeOffset nowUtc, CancellationToken cancellationToken = default)
            => Task.FromResult(0);
    }

    private sealed class FakeQueue : IDurableUsageQueue
    {
        public DateTimeOffset? Oldest { get; set; }
        public Task EnqueueAsync(IReadOnlyCollection<UsageBucketDelta> deltas, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<int> FlushOnceAsync(CancellationToken cancellationToken = default) => Task.FromResult(0);
        public Task<int> PendingCountAsync(CancellationToken cancellationToken = default) => Task.FromResult(Oldest.HasValue ? 1 : 0);
        public Task<DateTimeOffset?> OldestPendingAtUtcAsync(CancellationToken cancellationToken = default) => Task.FromResult(Oldest);
        public Task RecordFailureAsync(Exception exception, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class SilentLogger<T> : ILogger<T>
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => false;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) { }
    }
}
