using Flow.Domain.Identity;
using Flow.Domain.Measurement;

namespace Flow.Domain.Model;

/// <summary>
/// A normalized usage observation produced by a monitoring provider.
/// Provider must document MeasurementPoint semantics.
/// </summary>
public sealed record NetworkUsageSample(
    DateTimeOffset ObservedAtUtc,
    ProcessIdentity? Process,
    ApplicationIdentity? Application,
    string? InterfaceName,
    ByteCounters Counters,
    MeasurementPoint MeasurementPoint,
    bool IsAttributed,
    string? AttributionNote = null);
