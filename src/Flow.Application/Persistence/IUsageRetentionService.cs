namespace Flow.Application.Persistence;

/// <summary>
/// Deletes fine-grained buckets that are no longer needed after rollups exist.
/// </summary>
public interface IUsageRetentionService
{
    /// <summary>Applies the configured retention policy; returns deleted row count.</summary>
    Task<int> ApplyAsync(DateTimeOffset nowUtc, CancellationToken cancellationToken = default);
}
