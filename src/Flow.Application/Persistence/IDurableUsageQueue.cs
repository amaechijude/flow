namespace Flow.Application.Persistence;

/// <summary>
/// Durable hand-off for 1-minute deltas. Enqueue happens before the in-memory
/// buffer is released; applying deltas and deleting enqueued rows happens in
/// one transaction, so a failed or interrupted flush can be retried without
/// double-counting.
/// </summary>
public interface IDurableUsageQueue
{
    /// <summary>Persists deltas to the durable queue.</summary>
    Task EnqueueAsync(IReadOnlyCollection<UsageBucketDelta> deltas, CancellationToken cancellationToken = default);

    /// <summary>
    /// Applies up to one batch of pending deltas and removes those rows atomically.
    /// Returns the number of pending rows applied.
    /// </summary>
    Task<int> FlushOnceAsync(CancellationToken cancellationToken = default);

    /// <summary>Number of pending rows (used for honest diagnostics).</summary>
    Task<int> PendingCountAsync(CancellationToken cancellationToken = default);

    /// <summary>Oldest enqueue time still pending, if any.</summary>
    Task<DateTimeOffset?> OldestPendingAtUtcAsync(CancellationToken cancellationToken = default);

    /// <summary>Records a failed attempt on the pending rows (best effort, never deletes rows).</summary>
    Task RecordFailureAsync(Exception exception, CancellationToken cancellationToken = default);
}
