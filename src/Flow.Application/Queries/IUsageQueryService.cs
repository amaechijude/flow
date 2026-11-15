using Flow.Contracts.Usage;

namespace Flow.Application.Queries;

public interface IUsageQueryService
{
    Task<UsageSummaryDto> GetSummaryAsync(DateTimeOffset startUtc, DateTimeOffset endUtc, string? interfaceId = null, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ApplicationUsageDto>> GetTopApplicationsAsync(DateTimeOffset startUtc, DateTimeOffset endUtc, string? interfaceId = null, int limit = 20, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<UsageTimePointDto>> GetUsageTimeSeriesAsync(DateTimeOffset startUtc, DateTimeOffset endUtc, string? interfaceId = null, string? applicationKey = null, CancellationToken cancellationToken = default);
}
