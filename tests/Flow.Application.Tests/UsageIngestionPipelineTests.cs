using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Flow.Application.Monitoring;
using Flow.Application.Persistence;
using Flow.Domain.Identity;
using Flow.Domain.Measurement;
using Flow.Domain.Monitoring;
using Microsoft.Extensions.Logging;

namespace Flow.Application.Tests;

public sealed class UsageIngestionPipelineTests
{
    [Fact]
    public async Task AggregatesEventsAndFlushesBatchesWithDiagnostics()
    {
        var t0 = new DateTimeOffset(2026, 11, 15, 12, 0, 10, TimeSpan.Zero);
        var bucket = new DateTimeOffset(2026, 11, 15, 12, 0, 0, TimeSpan.Zero);
        var events = new[]
        {
            CreateEvent(t0, "app.a", "p1", "eth0", 100, 10, true),
            CreateEvent(t0.AddSeconds(5), "app.a", "p1", "eth0", 50, 5, true),
            CreateEvent(t0.AddSeconds(20), null, null, "eth0", 7, 1, false, "unattributed"),
            CreateEvent(t0, "app.a", "p1", "eth0", 0, 0, true), // ignored
        };

        var monitor = new FakeMonitor(events);
        var store = new RecordingStore();
        var logger = new TestLogger<UsageIngestionPipeline>();
        await using var pipeline = new UsageIngestionPipeline(monitor, store, logger, flushInterval: TimeSpan.FromMilliseconds(30));

        await pipeline.StartMonitoringAsync();
        await store.WaitForBatchesAsync(expected: 1, timeout: TimeSpan.FromSeconds(2));
        await pipeline.StopAsync();

        var all = store.AllDeltas();
        Assert.Contains(all, d => d.BucketStartUtc == bucket && d.ApplicationKey == "app.a" && d.ProcessStableKey is not null && d.InterfaceId == "eth0" && d.IsAttributed && d.RxBytesDelta == 150 && d.TxBytesDelta == 15);
        Assert.Contains(all, d => d.BucketStartUtc == bucket && d.ApplicationKey == null && d.ProcessStableKey == null && d.InterfaceId == "eth0" && !d.IsAttributed && d.RxBytesDelta == 7 && d.TxBytesDelta == 1);

        var diag = pipeline.Diagnostics;
        Assert.Equal(4, diag.EventsReceived);
        Assert.Equal(3, diag.EventsAggregated);
        Assert.True(diag.BatchesWritten >= 1);
        Assert.Equal(0, diag.WriteErrors);
        Assert.NotNull(diag.LastEventAtUtc);
        Assert.NotNull(diag.LastFlushAtUtc);
    }

    [Fact]
    public async Task SameLogicalBucketAcrossFlushCyclesIsCoalescedByStoreWrites()
    {
        var t0 = new DateTimeOffset(2026, 11, 15, 12, 5, 1, TimeSpan.Zero);
        var monitor = new ScriptedMonitor();
        var store = new CoalescingRecordingStore();
        var logger = new TestLogger<UsageIngestionPipeline>();
        await using var pipeline = new UsageIngestionPipeline(monitor, store, logger, flushInterval: TimeSpan.FromMilliseconds(40));

        await pipeline.StartMonitoringAsync();
        await monitor.EmitAndWaitReadAsync(CreateEvent(t0, null, null, null, 100, 10, false, "unattributed"));
        await store.WaitForBatchesAsync(1, TimeSpan.FromSeconds(2));

        await monitor.EmitAndWaitReadAsync(CreateEvent(t0.AddSeconds(20), null, null, null, 50, 5, false, "unattributed"));
        await store.WaitForBatchesAsync(2, TimeSpan.FromSeconds(2));

        await pipeline.StopAsync();

        var bucket = new DateTimeOffset(2026, 11, 15, 12, 5, 0, TimeSpan.Zero);
        var totals = store.TotalsFor(bucket, null, null, null, false);
        Assert.Equal(150, totals.rx);
        Assert.Equal(15, totals.tx);
        Assert.True(pipeline.Diagnostics.BatchesWritten >= 2);
        Assert.Equal(0, pipeline.Diagnostics.WriteErrors);
    }

    [Fact]
    public async Task FlushFailureMarksDegradedAndRecordsWriteError()
    {
        var t0 = new DateTimeOffset(2026, 11, 15, 12, 10, 1, TimeSpan.Zero);
        var monitor = new FakeMonitor(new[] { CreateEvent(t0, "app.a", "p1", "eth0", 10, 1, true) });
        var store = new ThrowingStore();
        var logger = new TestLogger<UsageIngestionPipeline>();
        await using var pipeline = new UsageIngestionPipeline(monitor, store, logger, flushInterval: TimeSpan.FromMilliseconds(30));

        await pipeline.StartMonitoringAsync();
        await WaitUntilAsync(() => pipeline.Diagnostics.WriteErrors > 0, TimeSpan.FromSeconds(2));
        await pipeline.StopAsync();

        Assert.True(pipeline.Diagnostics.WriteErrors >= 1);
        Assert.Equal(MonitoringState.Degraded, pipeline.State);
        Assert.False(string.IsNullOrWhiteSpace(pipeline.LastError));
    }

    [Fact]
    public async Task FailedFlushIsRetriedAndPersistsAfterTransientError()
    {
        var t0 = new DateTimeOffset(2026, 11, 15, 12, 20, 1, TimeSpan.Zero);
        var bucket = new DateTimeOffset(2026, 11, 15, 12, 20, 0, TimeSpan.Zero);
        var monitor = new FakeMonitor(new[] { CreateEvent(t0, "app.a", "p1", "eth0", 10, 1, true) });
        var store = new FailThenSucceedStore(failures: 1);
        var logger = new TestLogger<UsageIngestionPipeline>();
        await using var pipeline = new UsageIngestionPipeline(monitor, store, logger, flushInterval: TimeSpan.FromMilliseconds(30));

        await pipeline.StartMonitoringAsync();
        await store.WaitForSuccessfulBatchesAsync(1, TimeSpan.FromSeconds(2));
        await WaitUntilAsync(() => pipeline.State == MonitoringState.Monitoring && pipeline.Diagnostics.BatchesWritten >= 1, TimeSpan.FromSeconds(2));
        await pipeline.StopAsync();

        Assert.True(pipeline.Diagnostics.WriteErrors >= 1);
        Assert.True(pipeline.Diagnostics.BatchesWritten >= 1);
        Assert.Null(pipeline.LastError); // cleared after successful recovery flush
        Assert.False(string.IsNullOrWhiteSpace(pipeline.Diagnostics.ProviderName));
        var totals = store.TotalsFor(bucket, "app.a", "eth0", true);
        Assert.Equal(10, totals.rx);
        Assert.Equal(1, totals.tx);
    }

    private static NetworkUsageEvent CreateEvent(
        DateTimeOffset observedAtUtc, string? appKey, string? processKey, string? iface,
        long rx, long tx, bool attributed, string? note = null)
    {
        var processStart = new DateTimeOffset(2026, 11, 15, 11, 0, 0, TimeSpan.Zero);
        ApplicationIdentity? app = appKey is null ? null : new ApplicationIdentity(appKey, appKey, null, null, null);
        ProcessIdentity? proc = processKey is null ? null : new ProcessIdentity(42, processStart, processKey, 1000);
        var point = new MeasurementPoint("test-provider", "test-hook", "per-process");
        return new NetworkUsageEvent(observedAtUtc, proc, app, iface, rx, tx, point, attributed, note);
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var start = DateTime.UtcNow;
        while (!condition())
        {
            if (DateTime.UtcNow - start > timeout)
                throw new TimeoutException("Condition not met in time.");
            await Task.Delay(10);
        }
    }

    private sealed class FakeMonitor : INetworkMonitor
    {
        private readonly IReadOnlyList<NetworkUsageEvent> _events;
        public FakeMonitor(IReadOnlyList<NetworkUsageEvent> events) => _events = events;
        public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public async IAsyncEnumerable<NetworkUsageEvent> ReadEventsAsync([EnumeratorCancellation] CancellationToken cancellationToken)
        {
            foreach (var ev in _events)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return ev;
            }
            // Keep stream open briefly so periodic flush can run before completion in most cases.
            try { await Task.Delay(200, cancellationToken).ConfigureAwait(false); } catch (OperationCanceledException) { }
        }
    }

    private sealed class ScriptedMonitor : INetworkMonitor
    {
        private readonly Channel<NetworkUsageEvent> _channel = Channel.CreateUnbounded<NetworkUsageEvent>();
        public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task StopAsync(CancellationToken cancellationToken) { _channel.Writer.TryComplete(); return Task.CompletedTask; }
        public async Task EmitAndWaitReadAsync(NetworkUsageEvent ev)
        {
            await _channel.Writer.WriteAsync(ev).ConfigureAwait(false);
            // Give reader loop time to observe and aggregate before caller relies on next timed flush.
            await Task.Delay(20).ConfigureAwait(false);
        }
        public async IAsyncEnumerable<NetworkUsageEvent> ReadEventsAsync([EnumeratorCancellation] CancellationToken cancellationToken)
        {
            while (await _channel.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
            {
                while (_channel.Reader.TryRead(out var ev))
                    yield return ev;
            }
        }
    }

    private class RecordingStore : IUsageBucketStore
    {
        private readonly List<IReadOnlyCollection<UsageBucketDelta>> _batches = new();
        private readonly object _gate = new();
        public virtual Task WriteMinuteDeltasAsync(IReadOnlyCollection<UsageBucketDelta> deltas, CancellationToken cancellationToken = default)
        {
            lock (_gate) _batches.Add(deltas.ToArray());
            return Task.CompletedTask;
        }
        public IReadOnlyList<UsageBucketDelta> AllDeltas() { lock (_gate) return _batches.SelectMany(b => b).ToList(); }
        public async Task WaitForBatchesAsync(int expected, TimeSpan timeout)
        {
            var start = DateTime.UtcNow;
            while (true)
            {
                lock (_gate) if (_batches.Count >= expected) return;
                if (DateTime.UtcNow - start > timeout) throw new TimeoutException($"Expected >= {expected} batches.");
                await Task.Delay(10).ConfigureAwait(false);
            }
        }
    }

    private sealed class CoalescingRecordingStore : RecordingStore
    {
        private readonly Dictionary<(DateTimeOffset bucket,string? app,string? proc,string? iface,bool attr),(long rx,long tx)> _totals = new();
        private readonly object _gate = new();
        public override Task WriteMinuteDeltasAsync(IReadOnlyCollection<UsageBucketDelta> deltas, CancellationToken cancellationToken = default)
        {
            base.WriteMinuteDeltasAsync(deltas, cancellationToken);
            lock (_gate)
            {
                foreach (var d in deltas)
                {
                    var key = (d.BucketStartUtc, d.ApplicationKey, d.ProcessStableKey, d.InterfaceId, d.IsAttributed);
                    if (!_totals.TryGetValue(key, out var cur)) cur = (0, 0);
                    _totals[key] = (cur.rx + d.RxBytesDelta, cur.tx + d.TxBytesDelta);
                }
            }
            return Task.CompletedTask;
        }
        public (long rx, long tx) TotalsFor(DateTimeOffset bucket, string? app, string? proc, string? iface, bool attr)
        {
            lock (_gate) return _totals.TryGetValue((bucket, app, proc, iface, attr), out var v) ? v : (0, 0);
        }
    }

    private sealed class ThrowingStore : IUsageBucketStore
    {
        public Task WriteMinuteDeltasAsync(IReadOnlyCollection<UsageBucketDelta> deltas, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("boom");
    }

    private sealed class FailThenSucceedStore : IUsageBucketStore
    {
        private int _remainingFailures;
        private int _successes;
        private readonly Dictionary<(DateTimeOffset bucket,string? app,string? iface,bool attr),(long rx,long tx,string? proc)> _totals = new();
        private readonly object _gate = new();
        public FailThenSucceedStore(int failures) => _remainingFailures = failures;
        public Task WriteMinuteDeltasAsync(IReadOnlyCollection<UsageBucketDelta> deltas, CancellationToken cancellationToken = default)
        {
            lock (_gate)
            {
                if (_remainingFailures > 0)
                {
                    _remainingFailures--;
                    throw new InvalidOperationException("transient");
                }
                foreach (var d in deltas)
                {
                    var key = (d.BucketStartUtc, d.ApplicationKey, d.InterfaceId, d.IsAttributed);
                    if (!_totals.TryGetValue(key, out var cur)) cur = (0, 0, d.ProcessStableKey);
                    _totals[key] = (cur.rx + d.RxBytesDelta, cur.tx + d.TxBytesDelta, cur.proc ?? d.ProcessStableKey);
                }
                _successes++;
            }
            return Task.CompletedTask;
        }
        public (long rx, long tx) TotalsFor(DateTimeOffset bucket, string? app, string? iface, bool attr)
        {
            lock (_gate) return _totals.TryGetValue((bucket, app, iface, attr), out var v) ? (v.rx, v.tx) : (0, 0);
        }
        public async Task WaitForSuccessfulBatchesAsync(int expected, TimeSpan timeout)
        {
            var start = DateTime.UtcNow;
            while (true)
            {
                lock (_gate) if (_successes >= expected) return;
                if (DateTime.UtcNow - start > timeout) throw new TimeoutException("Expected successful batch.");
                await Task.Delay(10).ConfigureAwait(false);
            }
        }
    }

    private sealed class TestLogger<T> : ILogger<T>
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => false;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) { }
    }
}
