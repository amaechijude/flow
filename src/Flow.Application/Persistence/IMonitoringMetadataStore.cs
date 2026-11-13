using Flow.Domain.Identity;
using Flow.Domain.Monitoring;

namespace Flow.Application.Persistence;

public interface IMonitoringMetadataStore
{
    Task UpsertApplicationAsync(ApplicationIdentity application, DateTimeOffset seenAtUtc, CancellationToken cancellationToken = default);
    Task UpsertProcessAsync(ProcessIdentity process, string? applicationKey, DateTimeOffset seenAtUtc, CancellationToken cancellationToken = default);
    Task UpsertInterfaceAsync(string interfaceName, string? interfaceType, DateTimeOffset seenAtUtc, CancellationToken cancellationToken = default);
    Task<long> StartMonitoringSessionAsync(string? providerName, string? measurementVersion, MonitoringState healthState, string? diagnostics, CancellationToken cancellationToken = default);
    Task EndMonitoringSessionAsync(long sessionId, MonitoringState healthState, string? diagnostics, CancellationToken cancellationToken = default);
}
