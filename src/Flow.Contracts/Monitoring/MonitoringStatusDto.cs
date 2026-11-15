using Flow.Domain.Monitoring;

namespace Flow.Contracts.Monitoring;

public sealed record MonitoringStatusDto(
    MonitoringState State,
    DateTimeOffset? LastUpdatedUtc,
    string? ProviderName,
    string? MeasurementDescription,
    string? HealthMessage,
    long? AttributedEvents,
    long? UnattributedEvents,
    DateTimeOffset? LastEventAtUtc = null,
    DateTimeOffset? LastFlushAtUtc = null,
    string? LastError = null,
    long EventsReceived = 0,
    long EventsDropped = 0,
    long EventsAggregated = 0,
    long BatchesWritten = 0,
    long WriteErrors = 0);
