using System.Threading.Channels;
using Flow.Application.Persistence;
using Flow.Domain.Monitoring;
using Microsoft.Extensions.Logging;

namespace Flow.Application.Monitoring;

public sealed partial class UsageIngestionPipeline : IMonitoringController, IAsyncDisposable
{
    private readonly INetworkMonitor _monitor;
    private readonly IUsageBucketStore _store;
    private readonly ILogger<UsageIngestionPipeline> _logger;
    private readonly MonitoringDiagnostics _diagnostics = new();
    private readonly Channel<NetworkUsageEvent> _channel;
    private readonly TimeSpan _flushInterval;
    private readonly CancellationTokenSource _cts = new();
    private Task? _readerTask;
    private Task? _writerTask;
    private int _startState; // 0 stopped, 1 running

    public UsageIngestionPipeline(
        INetworkMonitor monitor,
        IUsageBucketStore store,
        ILogger<UsageIngestionPipeline> logger,
        int channelCapacity = 10_000,
        TimeSpan? flushInterval = null)
    {
        _monitor = monitor;
        _store = store;
        _logger = logger;
        _flushInterval = flushInterval ?? TimeSpan.FromSeconds(5);
        _channel = Channel.CreateBounded<NetworkUsageEvent>(new BoundedChannelOptions(channelCapacity)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.DropOldest
        });
    }

    public MonitoringState State => _diagnostics.State;
    public string? LastError => _diagnostics.LastError;
    public DateTimeOffset? LastEventAtUtc => _diagnostics.LastEventAtUtc;
    public MonitoringDiagnosticsSnapshot Diagnostics => _diagnostics.Snapshot();

    public Task StartMonitoringAsync(CancellationToken cancellationToken = default)
    {
        if (Interlocked.CompareExchange(ref _startState, 1, 0) != 0)
            return Task.CompletedTask;

        _diagnostics.State = MonitoringState.Initializing;
        _diagnostics.LastError = null;
        _readerTask = Task.Run(() => ReadLoopAsync(_cts.Token), CancellationToken.None);
        _writerTask = Task.Run(() => AggregateAndFlushLoopAsync(_cts.Token), CancellationToken.None);
        return Task.CompletedTask;
    }

    public async Task PauseMonitoringAsync(CancellationToken cancellationToken = default)
    {
        if (Interlocked.CompareExchange(ref _startState, 0, 1) != 1)
            return;

        _diagnostics.State = MonitoringState.Paused;
        try { await _monitor.StopAsync(cancellationToken).ConfigureAwait(false); } catch { /* best effort */ }
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        await PauseMonitoringAsync(cancellationToken).ConfigureAwait(false);
        _channel.Writer.TryComplete();
        await _cts.CancelAsync().ConfigureAwait(false);
        var tasks = new[] { _readerTask, _writerTask }.Where(t => t is not null)!.Cast<Task>().ToArray();
        if (tasks.Length > 0)
        {
            try { await Task.WhenAll(tasks).WaitAsync(cancellationToken).ConfigureAwait(false); } catch { /* shutting down */ }
        }
    }

    private async Task ReadLoopAsync(CancellationToken ct)
    {
        try
        {
            await _monitor.StartAsync(ct).ConfigureAwait(false);
            _diagnostics.State = MonitoringState.Monitoring;
            await foreach (var ev in _monitor.ReadEventsAsync(ct).ConfigureAwait(false))
            {
                ct.ThrowIfCancellationRequested();
                _diagnostics.IncrementReceived();
                _diagnostics.LastEventAtUtc = ev.ObservedAtUtc;
                if (!_channel.Writer.TryWrite(ev))
                {
                    _diagnostics.AddDropped(1);
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // normal shutdown
        }
        catch (Exception ex)
        {
            _diagnostics.State = MonitoringState.Degraded;
            _diagnostics.LastError = ex.Message;
            LogReadLoopFailed(_logger, ex);
        }
        finally
        {
            _channel.Writer.TryComplete();
        }
    }

    private async Task AggregateAndFlushLoopAsync(CancellationToken ct)
    {
        var buffer = new Dictionary<BucketKey, BucketAccumulator>(capacity: 1024);
        var timer = new PeriodicTimer(_flushInterval);
        try
        {
            while (true)
            {
                var readTask = _channel.Reader.WaitToReadAsync(ct).AsTask();
                var tickTask = timer.WaitForNextTickAsync(ct).AsTask();
                var completed = await Task.WhenAny(readTask, tickTask).ConfigureAwait(false);
                if (completed == tickTask)
                {
                    await tickTask.ConfigureAwait(false);
                    await FlushAsync(buffer, ct).ConfigureAwait(false);
                    continue;
                }

                if (!await readTask.ConfigureAwait(false))
                {
                    // channel completed
                    DrainAvailable(buffer);
                    await FlushAsync(buffer, ct).ConfigureAwait(false);
                    break;
                }

                DrainAvailable(buffer);
                if (buffer.Count >= 2048)
                    await FlushAsync(buffer, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            DrainAvailable(buffer);
            try { await FlushAsync(buffer, CancellationToken.None).ConfigureAwait(false); } catch { }
        }
        catch (Exception ex)
        {
            _diagnostics.State = MonitoringState.Degraded;
            _diagnostics.LastError = ex.Message;
            LogFlushLoopFailed(_logger, ex);
        }
        finally
        {
            timer.Dispose();
        }
    }

    private void DrainAvailable(Dictionary<BucketKey, BucketAccumulator> buffer)
    {
        while (_channel.Reader.TryRead(out var ev))
        {
            if (ev.RxBytesDelta == 0 && ev.TxBytesDelta == 0)
                continue;
            var bucketStart = AlignToMinuteUtc(ev.ObservedAtUtc);
            var key = new BucketKey(
                bucketStart,
                ev.ApplicationHint?.ApplicationKey,
                ev.Process?.StableKey,
                ev.InterfaceName,
                ev.IsAttributed);
            if (!buffer.TryGetValue(key, out var acc))
            {
                acc = new BucketAccumulator(ev.AttributionNote, ev.MeasurementPoint.Provider, ev.MeasurementPoint.Hook);
                buffer[key] = acc;
            }
            acc.Add(ev.RxBytesDelta, ev.TxBytesDelta, ev.AttributionNote, ev.MeasurementPoint.Provider, ev.MeasurementPoint.Hook);
            _diagnostics.AddAggregated(1);
        }
    }

    private async Task FlushAsync(Dictionary<BucketKey, BucketAccumulator> buffer, CancellationToken ct)
    {
        if (buffer.Count == 0) return;
        var batch = new List<UsageBucketDelta>(buffer.Count);
        foreach (var (key, acc) in buffer)
        {
            batch.Add(new UsageBucketDelta(
                key.BucketStartUtc,
                key.ApplicationKey,
                key.ProcessStableKey,
                key.InterfaceId,
                acc.RxBytes,
                acc.TxBytes,
                key.IsAttributed,
                acc.AttributionNote,
                acc.MeasurementProvider,
                acc.MeasurementHook));
        }
        buffer.Clear();
        try
        {
            await _store.WriteMinuteDeltasAsync(batch, ct).ConfigureAwait(false);
            _diagnostics.LastFlushAtUtc = DateTimeOffset.UtcNow;
            _diagnostics.IncrementBatchesWritten();
            if (_diagnostics.State == MonitoringState.Degraded && _diagnostics.LastError is not null)
            {
                // keep degraded state until explicitly recovered by future logic; last error preserved
            }
        }
        catch (Exception ex)
        {
            _diagnostics.IncrementWriteErrors();
            _diagnostics.State = MonitoringState.Degraded;
            _diagnostics.LastError = ex.Message;
            LogFlushFailed(_logger, ex, batch.Count);
            // Note: dropped on write failure in this basic implementation; future: retry/backoff queue.
        }
    }

    private static DateTimeOffset AlignToMinuteUtc(DateTimeOffset valueUtc)
    {
        var utc = valueUtc.ToUniversalTime();
        return new DateTimeOffset(utc.Year, utc.Month, utc.Day, utc.Hour, utc.Minute, 0, TimeSpan.Zero);
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        _cts.Dispose();
    }

    private readonly record struct BucketKey(
        DateTimeOffset BucketStartUtc,
        string? ApplicationKey,
        string? ProcessStableKey,
        string? InterfaceId,
        bool IsAttributed);

    private sealed class BucketAccumulator
    {
        public BucketAccumulator(string? note, string? provider, string? hook)
        {
            AttributionNote = note;
            MeasurementProvider = provider;
            MeasurementHook = hook;
        }
        public long RxBytes { get; private set; }
        public long TxBytes { get; private set; }
        public string? AttributionNote { get; private set; }
        public string? MeasurementProvider { get; private set; }
        public string? MeasurementHook { get; private set; }

        public void Add(long rx, long tx, string? note, string? provider, string? hook)
        {
            RxBytes = checked(RxBytes + rx);
            TxBytes = checked(TxBytes + tx);
            AttributionNote ??= note;
            MeasurementProvider ??= provider;
            MeasurementHook ??= hook;
        }
    }

    [LoggerMessage(EventId = 1001, Level = LogLevel.Error, Message = "Usage ingestion read loop failed")]
    private static partial void LogReadLoopFailed(ILogger logger, Exception exception);

    [LoggerMessage(EventId = 1002, Level = LogLevel.Error, Message = "Usage ingestion aggregation/flush loop failed")]
    private static partial void LogFlushLoopFailed(ILogger logger, Exception exception);

    [LoggerMessage(EventId = 1003, Level = LogLevel.Error, Message = "Failed to flush {Count} aggregated usage deltas")]
    private static partial void LogFlushFailed(ILogger logger, Exception exception, int count);
}
