namespace Flow.Application.Persistence;

public sealed record UsageBucketDelta(
    DateTimeOffset BucketStartUtc,
    string? ApplicationKey,
    string? ProcessStableKey,
    string? InterfaceId,
    long RxBytesDelta,
    long TxBytesDelta,
    bool IsAttributed,
    string? AttributionNote,
    string? MeasurementProvider,
    string? MeasurementHook);
