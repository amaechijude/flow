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
public sealed class HybridDaemonStatusClient : IDaemonStatusClient, IDataSourceStatus
{
    private readonly DbusDaemonClient _dbus;

    public HybridDaemonStatusClient(DbusDaemonClient dbus) => _dbus = dbus;

    public string UsageDataSource => "not queried";

    public string StatusDataSource { get; private set; } = "unknown";

    public async Task<MonitoringStatusDto> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var status = await _dbus.GetStatusAsync(cancellationToken).ConfigureAwait(false);
            StatusDataSource = "daemon IPC (D-Bus)";
            return status;
        }
        catch (Exception ex)
        {
            StatusDataSource = "unavailable (D-Bus unreachable)";
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
public sealed class HybridUsageQueryClient : IUsageQueryClient, IDataSourceStatus
{
    private readonly DbusDaemonClient _dbus;
    private readonly IUsageQueryClient _local;

    public HybridUsageQueryClient(DbusDaemonClient dbus, IUsageQueryClient local)
    {
        _dbus = dbus;
        _local = local;
    }

    public string StatusDataSource => "not queried";

    public string UsageDataSource { get; private set; } = "unknown";

    public async Task<UsageSummaryDto> GetSummaryAsync(DateTimeOffset startUtc, DateTimeOffset endUtc, string? interfaceId = null, CancellationToken cancellationToken = default)
    {
        try { var r = await _dbus.GetSummaryAsync(startUtc, endUtc, interfaceId, cancellationToken).ConfigureAwait(false); UsageDataSource = "daemon IPC (D-Bus)"; return r; }
        catch { var r = await _local.GetSummaryAsync(startUtc, endUtc, interfaceId, cancellationToken).ConfigureAwait(false); UsageDataSource = "local SQLite fallback"; return r; }
    }

    public async Task<IReadOnlyList<ApplicationUsageDto>> GetTopApplicationsAsync(DateTimeOffset startUtc, DateTimeOffset endUtc, string? interfaceId = null, int limit = 20, CancellationToken cancellationToken = default)
    {
        try { var r = await _dbus.GetTopApplicationsAsync(startUtc, endUtc, interfaceId, limit, cancellationToken).ConfigureAwait(false); UsageDataSource = "daemon IPC (D-Bus)"; return r; }
        catch { var r = await _local.GetTopApplicationsAsync(startUtc, endUtc, interfaceId, limit, cancellationToken).ConfigureAwait(false); UsageDataSource = "local SQLite fallback"; return r; }
    }

    public async Task<IReadOnlyList<UsageTimePointDto>> GetUsageTimeSeriesAsync(DateTimeOffset startUtc, DateTimeOffset endUtc, string? interfaceId = null, string? applicationKey = null, CancellationToken cancellationToken = default)
    {
        try { var r = await _dbus.GetUsageTimeSeriesAsync(startUtc, endUtc, interfaceId, applicationKey, cancellationToken).ConfigureAwait(false); UsageDataSource = "daemon IPC (D-Bus)"; return r; }
        catch { var r = await _local.GetUsageTimeSeriesAsync(startUtc, endUtc, interfaceId, applicationKey, cancellationToken).ConfigureAwait(false); UsageDataSource = "local SQLite fallback"; return r; }
    }

    public async Task<IReadOnlyList<InterfaceUsageDto>> GetInterfaceUsageAsync(DateTimeOffset startUtc, DateTimeOffset endUtc, CancellationToken cancellationToken = default)
    {
        try { var r = await _dbus.GetInterfaceUsageAsync(startUtc, endUtc, cancellationToken).ConfigureAwait(false); UsageDataSource = "daemon IPC (D-Bus)"; return r; }
        catch { var r = await _local.GetInterfaceUsageAsync(startUtc, endUtc, cancellationToken).ConfigureAwait(false); UsageDataSource = "local SQLite fallback"; return r; }
    }

    public async Task<IReadOnlyList<ProcessUsageDto>> GetTopProcessesAsync(DateTimeOffset startUtc, DateTimeOffset endUtc, string? interfaceId = null, int limit = 20, CancellationToken cancellationToken = default)
    {
        try { var r = await _dbus.GetTopProcessesAsync(startUtc, endUtc, interfaceId, limit, cancellationToken).ConfigureAwait(false); UsageDataSource = "daemon IPC (D-Bus)"; return r; }
        catch { var r = await _local.GetTopProcessesAsync(startUtc, endUtc, interfaceId, limit, cancellationToken).ConfigureAwait(false); UsageDataSource = "local SQLite fallback"; return r; }
    }
}
