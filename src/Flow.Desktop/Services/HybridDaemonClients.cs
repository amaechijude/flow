using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Flow.Contracts.Monitoring;
using Flow.Contracts.Usage;
using Flow.Domain.Monitoring;

namespace Flow.Desktop.Services;

/// <summary>
/// Prefers real daemon IPC for live status; reports unavailable when daemon is not reachable.
/// Does not fabricate metrics.
/// </summary>
public sealed class HybridDaemonStatusClient : IDaemonStatusClient
{
    private readonly DbusDaemonClient _dbus;

    public HybridDaemonStatusClient(DbusDaemonClient dbus) => _dbus = dbus;

    public async Task<MonitoringStatusDto> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            return await _dbus.GetStatusAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return new MonitoringStatusDto(
                MonitoringState.Unavailable,
                null,
                null,
                null,
                $"Daemon not reachable via D-Bus: {ex.Message}",
                null,
                null,
                null,
                null,
                ex.Message,
                0, 0, 0, 0, 0);
        }
    }
}

/// <summary>
/// Prefers real daemon IPC query API; falls back to direct local DB reads (same persisted data) if IPC is unavailable.
/// </summary>
public sealed class HybridUsageQueryClient : IUsageQueryClient
{
    private readonly DbusDaemonClient _dbus;
    private readonly IUsageQueryClient _local;

    public HybridUsageQueryClient(DbusDaemonClient dbus, IUsageQueryClient local)
    {
        _dbus = dbus;
        _local = local;
    }

    public async Task<UsageSummaryDto> GetSummaryAsync(DateTimeOffset startUtc, DateTimeOffset endUtc, string? interfaceId = null, CancellationToken cancellationToken = default)
    {
        try { return await _dbus.GetSummaryAsync(startUtc, endUtc, interfaceId, cancellationToken).ConfigureAwait(false); }
        catch { return await _local.GetSummaryAsync(startUtc, endUtc, interfaceId, cancellationToken).ConfigureAwait(false); }
    }

    public async Task<IReadOnlyList<ApplicationUsageDto>> GetTopApplicationsAsync(DateTimeOffset startUtc, DateTimeOffset endUtc, string? interfaceId = null, int limit = 20, CancellationToken cancellationToken = default)
    {
        try { return await _dbus.GetTopApplicationsAsync(startUtc, endUtc, interfaceId, limit, cancellationToken).ConfigureAwait(false); }
        catch { return await _local.GetTopApplicationsAsync(startUtc, endUtc, interfaceId, limit, cancellationToken).ConfigureAwait(false); }
    }

    public async Task<IReadOnlyList<UsageTimePointDto>> GetUsageTimeSeriesAsync(DateTimeOffset startUtc, DateTimeOffset endUtc, string? interfaceId = null, string? applicationKey = null, CancellationToken cancellationToken = default)
    {
        try { return await _dbus.GetUsageTimeSeriesAsync(startUtc, endUtc, interfaceId, applicationKey, cancellationToken).ConfigureAwait(false); }
        catch { return await _local.GetUsageTimeSeriesAsync(startUtc, endUtc, interfaceId, applicationKey, cancellationToken).ConfigureAwait(false); }
    }
}
