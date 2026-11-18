using Flow.Contracts.Monitoring;

namespace Flow.Contracts.Ipc;

/// <summary>
/// Payload carried by the <c>StatusChanged</c> D-Bus signal. The signal is
/// coalesced by the daemon: only the most recent snapshot is delivered after a
/// change is observed, so clients should treat it as a "refresh now" hint and
/// re-query for authoritative data.
/// </summary>
public sealed record StatusChangedSignalDto(
    DateTimeOffset ObservedUtc,
    string Reason,
    MonitoringStatusDto Status);
