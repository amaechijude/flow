using Flow.Domain.Identity;
using Flow.Domain.Measurement;

namespace Flow.Application.Monitoring;

/// <summary>
/// Raw/provider-level usage observation before persistence aggregation.
/// Bytes are as reported by the provider at its documented measurement point.
/// </summary>
public sealed record NetworkUsageEvent(
    DateTimeOffset ObservedAtUtc,
    ProcessIdentity? Process,
    ApplicationIdentity? ApplicationHint,
    string? InterfaceName,
    long RxBytesDelta,
    long TxBytesDelta,
    MeasurementPoint MeasurementPoint,
    bool IsAttributed,
    string? AttributionNote = null);
