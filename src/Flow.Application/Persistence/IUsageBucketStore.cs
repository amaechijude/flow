namespace Flow.Application.Persistence;

public interface IUsageBucketStore
{
    /// <summary>
    /// Applies aggregated deltas idempotently into 1-minute buckets.
    /// Implementations should batch writes transactionally where possible.
    /// </summary>
    Task WriteMinuteDeltasAsync(IReadOnlyCollection<UsageBucketDelta> deltas, CancellationToken cancellationToken = default);
}
