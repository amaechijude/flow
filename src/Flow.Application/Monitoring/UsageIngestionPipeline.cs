using System.Threading.Channels;
using Flow.Application.Persistence;
using Flow.Domain.Identity;
using Flow.Domain.Monitoring;
using Microsoft.Extensions.Logging;

namespace Flow.Application.Monitoring;

public sealed partial class UsageIngestionPipeline : IMonitoringController, IAsyncDisposable
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
            while (true)
            {
                var readTask = _channel.Reader.WaitToReadAsync(ct).AsTask();
                var tickTask = timer.WaitForNextTickAsync(ct).AsTask();
                var completed = await Task.WhenAny(readTask, tickTask).ConfigureAwait(false);
                if (completed == tickTask)
                {
                    await tickTask.ConfigureAwait(false);
                    await FlushAsync(buffer, apps, procs, ifaces, ct).ConfigureAwait(false);
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

        try
        {
            if (_metadataStore is not null)
            {
                if (ifaces is not null && ifaces.Count > 0)
                {
                    foreach (var kv in ifaces)
                        await _metadataStore.UpsertInterfaceAsync(kv.Key, null, kv.Value, ct).ConfigureAwait(false);
                    ifaces.Clear();
                }
                if (apps is not null && apps.Count > 0)
                {
                    foreach (var kv in apps.Values)
                        await _metadataStore.UpsertApplicationAsync(kv.App, kv.Seen, ct).ConfigureAwait(false);
                    apps.Clear();
                }
                if (procs is not null && procs.Count > 0)
                {
                    foreach (var kv in procs.Values)
                        await _metadataStore.UpsertProcessAsync(kv.Proc, kv.AppKey, kv.Seen, ct).ConfigureAwait(false);
                    procs.Clear();
                }
            }

            if (buffer.Count > 0)
            {
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
                await _store.WriteMinuteDeltasAsync(batch, ct).ConfigureAwait(false);
                _diagnostics.IncrementBatchesWritten();
            }

            _diagnostics.LastFlushAtUtc = DateTimeOffset.UtcNow;
        }
        catch (Exception ex)
        {
            _diagnostics.IncrementWriteErrors();
            _diagnostics.State = MonitoringState.Degraded;
            _diagnostics.LastError = ex.Message;
            LogFlushFailed(_logger, ex, buffer.Count);
            buffer.Clear();
            apps?.Clear();
            procs?.Clear();
            ifaces?.Clear();
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
