using System.Threading.Channels;
using Flow.Application.Persistence;
using Flow.Domain.Identity;
using Flow.Domain.Monitoring;
using Microsoft.Extensions.Logging;

namespace Flow.Application.Monitoring;

public sealed partial class UsageIngestionPipeline : IMonitoringController, IMonitoringDiagnosticsSource, IAsyncDisposable
{
    private readonly INetworkMonitor _monitor;
    private readonly IUsageBucketStore _store;
    private readonly IMonitoringMetadataStore? _metadataStore;
    private readonly ILogger<UsageIngestionPipeline> _logger;
    private readonly MonitoringDiagnostics _diagnostics = new();
    private readonly Channel<NetworkUsageEvent> _channel;
    private readonly TimeSpan _flushInterval;
    private readonly CancellationTokenSource _cts = new();
    private Task? _readerTask;
    private Task? _writerTask;
    private int _startState; // 0 stopped, 1 running
    private int _disposeState; // 0 active, 1 disposed
    private long? _sessionId;

    public UsageIngestionPipeline(
        INetworkMonitor monitor,
        IUsageBucketStore store,
        ILogger<UsageIngestionPipeline> logger,
        IMonitoringMetadataStore? metadataStore = null,
        int channelCapacity = 10_000,
        TimeSpan? flushInterval = null)
    {
        _monitor = monitor;
        _store = store;
        _metadataStore = metadataStore;
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
    public MonitoringDiagnosticsSnapshot GetDiagnosticsSnapshot() => _diagnostics.Snapshot();

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

        if (_diagnostics.State is not MonitoringState.Degraded and not MonitoringState.Unavailable)
            _diagnostics.State = MonitoringState.Paused;
        try { await _monitor.StopAsync(cancellationToken).ConfigureAwait(false); } catch { /* best effort */ }
        if (_metadataStore is not null && _sessionId.HasValue)
        {
            try { await _metadataStore.EndMonitoringSessionAsync(_sessionId.Value, _diagnostics.State, _diagnostics.LastError, cancellationToken).ConfigureAwait(false); } catch { }
            _sessionId = null;
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        await PauseMonitoringAsync(cancellationToken).ConfigureAwait(false);
        _channel.Writer.TryComplete();
        try { if (!_cts.IsCancellationRequested) await _cts.CancelAsync().ConfigureAwait(false); } catch (ObjectDisposedException) { }
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
            _diagnostics.ProviderName ??= _monitor.GetType().Name;
            await _monitor.StartAsync(ct).ConfigureAwait(false);
            _diagnostics.State = MonitoringState.Monitoring;
            if (_metadataStore is not null)
            {
                try { _sessionId = await _metadataStore.StartMonitoringSessionAsync(_diagnostics.ProviderName, null, _diagnostics.State, null, ct).ConfigureAwait(false); } catch { }
            }
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
        var apps = _metadataStore is null ? null : new Dictionary<string, (ApplicationIdentity App, DateTimeOffset Seen)>();
        var procs = _metadataStore is null ? null : new Dictionary<string, (ProcessIdentity Proc, string? AppKey, DateTimeOffset Seen)>();
        var ifaces = _metadataStore is null ? null : new Dictionary<string, DateTimeOffset>(StringComparer.OrdinalIgnoreCase);
        var timer = new PeriodicTimer(_flushInterval);
        try
        {
            var tickTask = timer.WaitForNextTickAsync(ct).AsTask();
            while (true)
            {
                var readTask = _channel.Reader.WaitToReadAsync(ct).AsTask();
                var completed = await Task.WhenAny(readTask, tickTask).ConfigureAwait(false);
                if (completed == tickTask)
                {
                    await tickTask.ConfigureAwait(false);
                    await FlushAsync(buffer, apps, procs, ifaces, ct).ConfigureAwait(false);
                    tickTask = timer.WaitForNextTickAsync(ct).AsTask();
                    continue;
                }

                if (!await readTask.ConfigureAwait(false))
                {
                    DrainAvailable(buffer, apps, procs, ifaces);
                    await FlushAsync(buffer, apps, procs, ifaces, ct).ConfigureAwait(false);
                    break;
                }

                DrainAvailable(buffer, apps, procs, ifaces);
                if (buffer.Count >= 2048)
                    await FlushAsync(buffer, apps, procs, ifaces, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            DrainAvailable(buffer, apps, procs, ifaces);
            try { await FlushAsync(buffer, apps, procs, ifaces, CancellationToken.None).ConfigureAwait(false); } catch { }
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

    private void DrainAvailable(
        Dictionary<BucketKey, BucketAccumulator> buffer,
        Dictionary<string, (ApplicationIdentity App, DateTimeOffset Seen)>? apps,
        Dictionary<string, (ProcessIdentity Proc, string? AppKey, DateTimeOffset Seen)>? procs,
        Dictionary<string, DateTimeOffset>? ifaces)
    {
        while (_channel.Reader.TryRead(out var ev))
        {
            if (apps is not null && ev.ApplicationHint is not null)
                apps[ev.ApplicationHint.ApplicationKey] = (ev.ApplicationHint, ev.ObservedAtUtc);
            if (procs is not null && ev.Process is not null)
                procs[ev.Process.StableKey] = (ev.Process, ev.ApplicationHint?.ApplicationKey, ev.ObservedAtUtc);
            if (ifaces is not null && !string.IsNullOrWhiteSpace(ev.InterfaceName))
                ifaces[NormalizeInterfaceId(ev.InterfaceName)] = ev.ObservedAtUtc;

            if (ev.RxBytesDelta == 0 && ev.TxBytesDelta == 0)
                continue;

            var bucketStart = AlignToMinuteUtc(ev.ObservedAtUtc);
            var ifaceId = string.IsNullOrWhiteSpace(ev.InterfaceName) ? null : NormalizeInterfaceId(ev.InterfaceName);
            var key = new BucketKey(
                bucketStart,
                ev.ApplicationHint?.ApplicationKey,
                ev.Process?.StableKey,
                ifaceId,
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

    private async Task FlushAsync(
        Dictionary<BucketKey, BucketAccumulator> buffer,
        Dictionary<string, (ApplicationIdentity App, DateTimeOffset Seen)>? apps,
        Dictionary<string, (ProcessIdentity Proc, string? AppKey, DateTimeOffset Seen)>? procs,
        Dictionary<string, DateTimeOffset>? ifaces,
        CancellationToken ct)
    {
        if (buffer.Count == 0 && (apps?.Count ?? 0) == 0 && (procs?.Count ?? 0) == 0 && (ifaces?.Count ?? 0) == 0)
            return;

        // Snapshot current state so we can safely retain/retry on failure while allowing new events to accumulate.
        List<(string Key, DateTimeOffset Seen)>? ifaceBatch = null;
        if (_metadataStore is not null && ifaces is not null && ifaces.Count > 0)
            ifaceBatch = ifaces.Select(kv => (kv.Key, kv.Value)).ToList();
        List<(ApplicationIdentity App, DateTimeOffset Seen)>? appBatch = null;
        if (_metadataStore is not null && apps is not null && apps.Count > 0)
            appBatch = apps.Values.ToList();
        List<(ProcessIdentity Proc, string? AppKey, DateTimeOffset Seen)>? procBatch = null;
        if (_metadataStore is not null && procs is not null && procs.Count > 0)
            procBatch = procs.Values.ToList();

        List<UsageBucketDelta>? deltaBatch = null;
        if (buffer.Count > 0)
        {
            deltaBatch = new List<UsageBucketDelta>(buffer.Count);
            foreach (var (key, acc) in buffer)
            {
                deltaBatch.Add(new UsageBucketDelta(
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
        }

        try
        {
            if (_metadataStore is not null)
            {
                if (ifaceBatch is not null)
                {
                    foreach (var kv in ifaceBatch)
                        await _metadataStore.UpsertInterfaceAsync(kv.Key, null, kv.Seen, ct).ConfigureAwait(false);
                }
                if (appBatch is not null)
                {
                    foreach (var kv in appBatch)
                        await _metadataStore.UpsertApplicationAsync(kv.App, kv.Seen, ct).ConfigureAwait(false);
                }
                if (procBatch is not null)
                {
                    foreach (var kv in procBatch)
                        await _metadataStore.UpsertProcessAsync(kv.Proc, kv.AppKey, kv.Seen, ct).ConfigureAwait(false);
                }
            }

            if (deltaBatch is not null && deltaBatch.Count > 0)
                await _store.WriteMinuteDeltasAsync(deltaBatch, ct).ConfigureAwait(false);

            // Commit: remove flushed snapshots (preserve any newer arrivals accumulated during async writes).
            if (ifaceBatch is not null && ifaces is not null)
            {
                foreach (var kv in ifaceBatch)
                {
                    if (ifaces.TryGetValue(kv.Key, out var seen) && seen <= kv.Seen)
                        ifaces.Remove(kv.Key);
                }
            }
            if (appBatch is not null && apps is not null)
            {
                foreach (var kv in appBatch)
                {
                    if (apps.TryGetValue(kv.App.ApplicationKey, out var cur) && cur.Seen <= kv.Seen)
                        apps.Remove(kv.App.ApplicationKey);
                }
            }
            if (procBatch is not null && procs is not null)
            {
                foreach (var kv in procBatch)
                {
                    if (procs.TryGetValue(kv.Proc.StableKey, out var cur) && cur.Seen <= kv.Seen)
                        procs.Remove(kv.Proc.StableKey);
                }
            }
            if (deltaBatch is not null)
            {
                foreach (var d in deltaBatch)
                {
                    var key = new BucketKey(d.BucketStartUtc, d.ApplicationKey, d.ProcessStableKey, d.InterfaceId, d.IsAttributed);
                    if (buffer.TryGetValue(key, out var acc))
                    {
                        acc.Subtract(d.RxBytesDelta, d.TxBytesDelta);
                        if (acc.IsEmpty)
                            buffer.Remove(key);
                    }
                }
                _diagnostics.IncrementBatchesWritten();
            }

            _diagnostics.LastFlushAtUtc = DateTimeOffset.UtcNow;
            if (Volatile.Read(ref _startState) == 1 && _diagnostics.State == MonitoringState.Degraded)
            {
                // A successful flush is the recovery signal for write-path errors.
                _diagnostics.State = MonitoringState.Monitoring;
                _diagnostics.LastError = null;
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested || _cts.IsCancellationRequested)
        {
            // Shutdown/cancellation: retain buffered deltas/metadata; do not mark degraded or count as write error.
        }
        catch (Exception ex)
        {
            _diagnostics.IncrementWriteErrors();
            _diagnostics.State = MonitoringState.Degraded;
            _diagnostics.LastError = ex.Message;
            LogFlushFailed(_logger, ex, deltaBatch?.Count ?? 0);
            // Retain buffered deltas/metadata for retry on next flush cycle (upserts are idempotent/additive-safe).
        }
    }

    private static string NormalizeInterfaceId(string interfaceName) => interfaceName.Trim().ToLowerInvariant();

    private static DateTimeOffset AlignToMinuteUtc(DateTimeOffset valueUtc)
    {
        var utc = valueUtc.ToUniversalTime();
        return new DateTimeOffset(utc.Year, utc.Month, utc.Day, utc.Hour, utc.Minute, 0, TimeSpan.Zero);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposeState, 1) == 1)
            return;
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
        public bool IsEmpty => RxBytes == 0 && TxBytes == 0;

        public void Add(long rx, long tx, string? note, string? provider, string? hook)
        {
            RxBytes = checked(RxBytes + rx);
            TxBytes = checked(TxBytes + tx);
            AttributionNote ??= note;
            MeasurementProvider ??= provider;
            MeasurementHook ??= hook;
        }

        public void Subtract(long rx, long tx)
        {
            RxBytes = checked(RxBytes - rx);
            TxBytes = checked(TxBytes - tx);
        }
    }

    [LoggerMessage(EventId = 1001, Level = LogLevel.Error, Message = "Usage ingestion read loop failed")]
    private static partial void LogReadLoopFailed(ILogger logger, Exception exception);

    [LoggerMessage(EventId = 1002, Level = LogLevel.Error, Message = "Usage ingestion aggregation/flush loop failed")]
    private static partial void LogFlushLoopFailed(ILogger logger, Exception exception);

    [LoggerMessage(EventId = 1003, Level = LogLevel.Error, Message = "Failed to flush {Count} aggregated usage deltas")]
    private static partial void LogFlushFailed(ILogger logger, Exception exception, int count);
}
