using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Flow.Application.Queries;
using Flow.Contracts.Usage;

namespace Flow.Desktop.Services;

public interface IUsageQueryClient
{
    Task<UsageSummaryDto> GetSummaryAsync(DateTimeOffset startUtc, DateTimeOffset endUtc, string? interfaceId = null, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ApplicationUsageDto>> GetTopApplicationsAsync(DateTimeOffset startUtc, DateTimeOffset endUtc, string? interfaceId = null, int limit = 20, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<UsageTimePointDto>> GetUsageTimeSeriesAsync(DateTimeOffset startUtc, DateTimeOffset endUtc, string? interfaceId = null, string? applicationKey = null, CancellationToken cancellationToken = default);
}

/// <summary>
/// Reads historical usage directly from the local SQLite database (same data owned/persisted by the daemon).
/// This is not a mock; it provides real persisted history when available.
/// </summary>
public sealed class LocalUsageQueryClient : IUsageQueryClient
{
    private readonly IUsageQueryService _queries;

    public LocalUsageQueryClient(IUsageQueryService queries)
    {
        _queries = queries;
    }

    public Task<UsageSummaryDto> GetSummaryAsync(DateTimeOffset startUtc, DateTimeOffset endUtc, string? interfaceId = null, CancellationToken cancellationToken = default)
        => _queries.GetSummaryAsync(startUtc, endUtc, interfaceId, cancellationToken);

    public Task<IReadOnlyList<ApplicationUsageDto>> GetTopApplicationsAsync(DateTimeOffset startUtc, DateTimeOffset endUtc, string? interfaceId = null, int limit = 20, CancellationToken cancellationToken = default)
        => _queries.GetTopApplicationsAsync(startUtc, endUtc, interfaceId, limit, cancellationToken);

    public Task<IReadOnlyList<UsageTimePointDto>> GetUsageTimeSeriesAsync(DateTimeOffset startUtc, DateTimeOffset endUtc, string? interfaceId = null, string? applicationKey = null, CancellationToken cancellationToken = default)
        => _queries.GetUsageTimeSeriesAsync(startUtc, endUtc, interfaceId, applicationKey, cancellationToken);
}
