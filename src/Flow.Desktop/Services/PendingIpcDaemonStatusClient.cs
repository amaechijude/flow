using System.Threading;
using System.Threading.Tasks;
using Flow.Contracts.Monitoring;
using Flow.Domain.Monitoring;

namespace Flow.Desktop.Services;

/// <summary>
/// Truthful placeholder until versioned daemon IPC is implemented (ADR 0002 pending).
/// Does not fabricate live monitoring metrics from the daemon process.
/// </summary>
public sealed class PendingIpcDaemonStatusClient : IDaemonStatusClient
{
    public Task<MonitoringStatusDto> GetStatusAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(new MonitoringStatusDto(
            MonitoringState.Unavailable,
            null,
            null,
            null,
            "Daemon IPC not connected yet (read-only historical data is loaded from the local database).",
            null,
            null,
            null,
            null,
            "Daemon IPC not connected",
            0, 0, 0, 0, 0));
}
