using Flow.Domain.Monitoring;

namespace Flow.Application.Monitoring;

public sealed class MonitoringDiagnostics
{
    private long _eventsReceived;
    private long _eventsDropped;
    private long _eventsAggregated;
    private long _batchesWritten;
    private long _writeErrors;
    private long _durableFlushAttempts;

    public MonitoringState State { get; internal set; } = MonitoringState.Initializing;
    public DateTimeOffset? LastEventAtUtc { get; internal set; }
    public DateTimeOffset? LastFlushAtUtc { get; internal set; }
    public string? LastError { get; internal set; }
    public string? ProviderName { get; internal set; }

    // Durable flush diagnostics. Values are real queue state, never estimates.
    public long PendingDurableDeltas { get; internal set; }
    public DateTimeOffset? OldestPendingAtUtc { get; internal set; }
    public DateTimeOffset? NextRetryAtUtc { get; internal set; }
    public double RetryBackoffSeconds { get; internal set; }

    public long EventsReceived => Interlocked.Read(ref _eventsReceived);
    public long EventsDropped => Interlocked.Read(ref _eventsDropped);
    public long EventsAggregated => Interlocked.Read(ref _eventsAggregated);
    public long BatchesWritten => Interlocked.Read(ref _batchesWritten);
    public long WriteErrors => Interlocked.Read(ref _writeErrors);
    public long DurableFlushAttempts => Interlocked.Read(ref _durableFlushAttempts);

    internal void IncrementReceived() => Interlocked.Increment(ref _eventsReceived);
    internal void AddDropped(long count) => Interlocked.Add(ref _eventsDropped, count);
    internal void AddAggregated(long count) => Interlocked.Add(ref _eventsAggregated, count);
    internal void IncrementBatchesWritten() => Interlocked.Increment(ref _batchesWritten);
    internal void IncrementWriteErrors() => Interlocked.Increment(ref _writeErrors);
    internal void IncrementDurableFlushAttempts() => Interlocked.Increment(ref _durableFlushAttempts);

    public MonitoringDiagnosticsSnapshot Snapshot() => new(
        State,
        LastEventAtUtc,
        LastFlushAtUtc,
        LastError,
        ProviderName,
        EventsReceived,
        EventsDropped,
        EventsAggregated,
        BatchesWritten,
        WriteErrors,
        PendingDurableDeltas,
        OldestPendingAtUtc,
        DurableFlushAttempts,
        NextRetryAtUtc,
        RetryBackoffSeconds);
}

public sealed record MonitoringDiagnosticsSnapshot(
    MonitoringState State,
    DateTimeOffset? LastEventAtUtc,
    DateTimeOffset? LastFlushAtUtc,
    string? LastError,
    string? ProviderName,
    long EventsReceived,
    long EventsDropped,
    long EventsAggregated,
    long BatchesWritten,
    long WriteErrors,
    long PendingDurableDeltas = 0,
    DateTimeOffset? OldestPendingAtUtc = null,
    long DurableFlushAttempts = 0,
    DateTimeOffset? NextRetryAtUtc = null,
    double RetryBackoffSeconds = 0);
