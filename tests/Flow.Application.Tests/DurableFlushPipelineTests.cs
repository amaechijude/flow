using System.Runtime.CompilerServices;
using Flow.Application.Monitoring;
using Flow.Application.Persistence;
using Flow.Domain.Identity;
using Flow.Domain.Measurement;
using Flow.Domain.Monitoring;
using Microsoft.Extensions.Logging;

namespace Flow.Application.Tests;

public sealed class DurableFlushPipelineTests
{
    private static readonly DateTimeOffset Bucket = new(2026, 11, 20, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task DeltasAreEnqueuedDurablyRatherThanWrittenDirectly()
    {
        var queue = new FakeDurableQueue();
        var directStore = new RecordingStore();
        var monitor = new FakeMonitor([Event(100, 10)]);
        await using var pipeline = CreatePipeline(monitor, directStore, queue, TimeSpan.FromMilliseconds(20));

        await pipeline.StartMonitoringAsync();
        await WaitUntilAsync(() => queue.Totals().rx == 100, TimeSpan.FromSeconds(2));
        await pipeline.StopAsync();

        Assert.True(queue.EnqueueCalls >= 1);
        Assert.Empty(directStore.AllDeltas());
        Assert.Equal(0, pipeline.Diagnostics.PendingDurableDeltas);
        Assert.True(pipeline.Diagnostics.DurableFlushAttempts > 0);
        Assert.Null(pipeline.LastError);
        Assert.Equal(100, queue.Totals().rx);
        Assert.Equal(10, queue.Totals().tx);
    }

    [Fact]
    public async Task FailedDrainRetriesWithBackoffAndRecoversWithoutDoubleCounting()
    {
        var queue = new FakeDurableQueue(failures: 2);
        var monitor = new FakeMonitor([Event(300, 30)]);
        await using var pipeline = CreatePipeline(
            monitor, new RecordingStore(), queue,
            TimeSpan.FromMilliseconds(15),
            TimeSpan.FromMilliseconds(5),
            TimeSpan.FromMilliseconds(20));

        await pipeline.StartMonitoringAsync();
        await WaitUntilAsync(() => queue.FailuresRecorded >= 2, TimeSpan.FromSeconds(2));

        Assert.Equal(MonitoringState.Degraded, pipeline.State);
        Assert.NotNull(pipeline.Diagnostics.NextRetryAtUtc);
        Assert.True(pipeline.Diagnostics.RetryBackoffSeconds > 0);
        Assert.True(pipeline.Diagnostics.WriteErrors >= 2);

        await WaitUntilAsync(
            () => pipeline.State == MonitoringState.Monitoring && queue.Totals().rx == 300,
            TimeSpan.FromSeconds(3));
        await pipeline.StopAsync();

        Assert.Null(pipeline.LastError);
        Assert.Equal(300, queue.Totals().rx); // exactly once despite two failed attempts
        Assert.Equal(30, queue.Totals().tx);
        Assert.Equal(0, pipeline.Diagnostics.PendingDurableDeltas);
    }

    [Fact]
    public async Task PendingDeltasFromAPreviousRunAreDrainedOnStart()
    {
        var queue = new FakeDurableQueue();
        queue.Preload([Delta(500, 50)]);
        var monitor = new FakeMonitor([]);
        await using var pipeline = CreatePipeline(monitor, new RecordingStore(), queue, TimeSpan.FromMilliseconds(20));

        await pipeline.StartMonitoringAsync();
        await WaitUntilAsync(() => queue.Totals().rx == 500, TimeSpan.FromSeconds(2));
        await pipeline.StopAsync();

        Assert.Equal(500, queue.Totals().rx);
        Assert.Equal(50, queue.Totals().tx);
        Assert.Equal(0, queue.PendingCount);
    }

    private static UsageIngestionPipeline CreatePipeline(
        INetworkMonitor monitor,
        IUsageBucketStore store,
        IDurableUsageQueue queue,
        TimeSpan flush,
        TimeSpan? baseDelay = null,
        TimeSpan? maxDelay = null)
        => new(monitor, store, new SilentLogger<UsageIngestionPipeline>(),
            flushInterval: flush, durableQueue: queue,
            durableBaseDelay: baseDelay, durableMaxDelay: maxDelay);

    private static UsageBucketDelta Delta(long rx, long tx) => new(
        Bucket, "app.test", "p1", "eth0", rx, tx, true, null, "test", "hook");

    private static NetworkUsageEvent Event(long rx, long tx)
    {
        var app = new ApplicationIdentity("app.test", "app.test", null, null, null);
        var proc = new ProcessIdentity(42, new DateTimeOffset(2026, 11, 20, 11, 0, 0, TimeSpan.Zero), "p1", 1000);
        var point = new MeasurementPoint("test-provider", "test-hook", "per-process");
        return new NetworkUsageEvent(Bucket.AddSeconds(5), proc, app, "eth0", rx, tx, point, true, null);
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var start = DateTime.UtcNow;
        while (!condition())
        {
            if (DateTime.UtcNow - start > timeout)
                throw new TimeoutException("Condition not met in time.");
            await Task.Delay(5);
        }
    }

    private sealed class FakeMonitor(IReadOnlyList<NetworkUsageEvent> events) : INetworkMonitor
    {
        public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public async IAsyncEnumerable<NetworkUsageEvent> ReadEventsAsync([EnumeratorCancellation] CancellationToken cancellationToken)
        {
            foreach (var ev in events)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return ev;
            }
            try { await Task.Delay(500, cancellationToken); } catch (OperationCanceledException) { }
        }
    }

    private sealed class RecordingStore : IUsageBucketStore
    {
        private readonly List<IReadOnlyCollection<UsageBucketDelta>> _batches = new();
        private readonly object _gate = new();
        public Task WriteMinuteDeltasAsync(IReadOnlyCollection<UsageBucketDelta> deltas, CancellationToken cancellationToken = default)
        {
            lock (_gate) _batches.Add(deltas.ToArray());
            return Task.CompletedTask;
        }
        public IReadOnlyList<UsageBucketDelta> AllDeltas() { lock (_gate) return _batches.SelectMany(b => b).ToList(); }
    }

    private sealed class FakeDurableQueue(int failures = 0) : IDurableUsageQueue
    {
        private readonly object _gate = new();
        private readonly List<UsageBucketDelta> _pending = new();
        private readonly List<UsageBucketDelta> _applied = new();
        private int _remainingFailures = failures;

        public int EnqueueCalls { get; private set; }
        public int FailuresRecorded { get; private set; }
        public int PendingCount { get { lock (_gate) return _pending.Count; } }

        public void Preload(IEnumerable<UsageBucketDelta> deltas)
        {
            lock (_gate) _pending.AddRange(deltas);
        }

        public Task EnqueueAsync(IReadOnlyCollection<UsageBucketDelta> deltas, CancellationToken cancellationToken = default)
        {
            lock (_gate)
            {
                EnqueueCalls++;
                _pending.AddRange(deltas);
            }
            return Task.CompletedTask;
        }

        public Task<int> FlushOnceAsync(CancellationToken cancellationToken = default)
        {
            lock (_gate)
            {
                if (_remainingFailures > 0)
                {
                    _remainingFailures--;
                    throw new InvalidOperationException("transient durable failure");
                }
                var batch = _pending.Take(500).ToList();
                _pending.RemoveRange(0, batch.Count);
                _applied.AddRange(batch);
                return Task.FromResult(batch.Count);
            }
        }

        public Task<int> PendingCountAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(PendingCount);

        public Task<DateTimeOffset?> OldestPendingAtUtcAsync(CancellationToken cancellationToken = default)
        {
            lock (_gate) return Task.FromResult<DateTimeOffset?>(_pending.Count == 0 ? null : DateTimeOffset.UtcNow);
        }

        public Task RecordFailureAsync(Exception exception, CancellationToken cancellationToken = default)
        {
            lock (_gate) FailuresRecorded++;
            return Task.CompletedTask;
        }

        public (long rx, long tx) Totals()
        {
            lock (_gate) return (_applied.Sum(d => d.RxBytesDelta), _applied.Sum(d => d.TxBytesDelta));
        }
    }

    private sealed class SilentLogger<T> : ILogger<T>
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => false;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) { }
    }
}
