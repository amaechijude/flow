namespace Flow.Application.Persistence;

/// <summary>
/// Rebuilds coarser usage buckets from persisted 1-minute buckets. Rebuilds are
/// idempotent: affected rollup rows are recomputed, not incremented again.
/// </summary>
public interface IUsageRollupService
{
    /// <summary>Rebuilds rollups up to (but not including) <paramref name="throughUtc"/>.</summary>
    /// <returns>Number of rollup rows written.</returns>
    Task<int> RebuildThroughAsync(DateTimeOffset throughUtc, CancellationToken cancellationToken = default);
}
