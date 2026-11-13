using Flow.Domain.Monitoring;

namespace Flow.Contracts.Monitoring;

public sealed record MonitoringStatusDto(
    MonitoringState State,
    DateTimeOffset? LastUpdatedUtc,
    string? ProviderName,
    string? MeasurementDescription,
    string? HealthMessage,
    long? AttributedEvents,
    long? UnattributedEvents);
