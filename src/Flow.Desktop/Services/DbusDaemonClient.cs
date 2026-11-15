using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Flow.Contracts.Ipc;
using Flow.Contracts.Monitoring;
using Flow.Contracts.Usage;
using Tmds.DBus;

namespace Flow.Desktop.Services;

/// <summary>
/// Real IPC client to the running Flow daemon over D-Bus. No fabricated data.
/// </summary>
public sealed class DbusDaemonClient : IDaemonStatusClient, IUsageQueryClient, IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private Connection? _connection;
    private IFlowDaemonDbus? _proxy;

    public Task<MonitoringStatusDto> GetStatusAsync(CancellationToken cancellationToken = default)
        => CallAsync(async p => IpcJson.Deserialize<MonitoringStatusDto>(await p.GetStatusJsonAsync().ConfigureAwait(false)) ?? throw new InvalidOperationException("Invalid status response from daemon."));

    public Task<UsageSummaryDto> GetSummaryAsync(DateTimeOffset startUtc, DateTimeOffset endUtc, string? interfaceId = null, CancellationToken cancellationToken = default)
        => CallAsync(async p => IpcJson.Deserialize<UsageSummaryDto>(await p.GetUsageSummaryJsonAsync(ToIso(startUtc), ToIso(endUtc), interfaceId ?? string.Empty).ConfigureAwait(false)) ?? throw new InvalidOperationException("Invalid summary response from daemon."));

    public Task<IReadOnlyList<ApplicationUsageDto>> GetTopApplicationsAsync(DateTimeOffset startUtc, DateTimeOffset endUtc, string? interfaceId = null, int limit = 20, CancellationToken cancellationToken = default)
        => CallAsync(async p => IpcJson.Deserialize<IReadOnlyList<ApplicationUsageDto>>(await p.GetTopApplicationsJsonAsync(ToIso(startUtc), ToIso(endUtc), interfaceId ?? string.Empty, limit).ConfigureAwait(false)) ?? Array.Empty<ApplicationUsageDto>());

    public Task<IReadOnlyList<UsageTimePointDto>> GetUsageTimeSeriesAsync(DateTimeOffset startUtc, DateTimeOffset endUtc, string? interfaceId = null, string? applicationKey = null, CancellationToken cancellationToken = default)
        => CallAsync(async p => IpcJson.Deserialize<IReadOnlyList<UsageTimePointDto>>(await p.GetUsageTimeSeriesJsonAsync(ToIso(startUtc), ToIso(endUtc), interfaceId ?? string.Empty, applicationKey ?? string.Empty).ConfigureAwait(false)) ?? Array.Empty<UsageTimePointDto>());

    public Task<IReadOnlyList<InterfaceUsageDto>> GetInterfaceUsageAsync(DateTimeOffset startUtc, DateTimeOffset endUtc, CancellationToken cancellationToken = default)
        => CallAsync(async p => IpcJson.Deserialize<IReadOnlyList<InterfaceUsageDto>>(await p.GetInterfaceUsageJsonAsync(ToIso(startUtc), ToIso(endUtc)).ConfigureAwait(false)) ?? Array.Empty<InterfaceUsageDto>());

    public Task<IReadOnlyList<ProcessUsageDto>> GetTopProcessesAsync(DateTimeOffset startUtc, DateTimeOffset endUtc, string? interfaceId = null, int limit = 20, CancellationToken cancellationToken = default)
        => CallAsync(async p => IpcJson.Deserialize<IReadOnlyList<ProcessUsageDto>>(await p.GetTopProcessesJsonAsync(ToIso(startUtc), ToIso(endUtc), interfaceId ?? string.Empty, limit).ConfigureAwait(false)) ?? Array.Empty<ProcessUsageDto>());

    private async Task<T> CallAsync<T>(Func<IFlowDaemonDbus, Task<T>> call)
    {
        try
        {
            var proxy = await GetProxyAsync().ConfigureAwait(false);
            return await call(proxy).ConfigureAwait(false);
        }
        catch
        {
            await ResetAsync().ConfigureAwait(false);
            var proxy = await GetProxyAsync().ConfigureAwait(false);
            return await call(proxy).ConfigureAwait(false);
        }
    }

    private async Task<IFlowDaemonDbus> GetProxyAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_proxy is not null && _connection is not null)
                return _proxy;

            var address = Address.Session ?? Address.System;
            if (string.IsNullOrWhiteSpace(address))
                throw new InvalidOperationException("No D-Bus address available.");

            _connection?.Dispose();
            _connection = new Connection(address);
            await _connection.ConnectAsync().ConfigureAwait(false);
            _proxy = _connection.CreateProxy<IFlowDaemonDbus>(FlowIpc.ServiceName, FlowIpc.ObjectPath);
            _ = await _proxy.GetContractVersionAsync().ConfigureAwait(false);
            return _proxy;
        }
        catch
        {
            try { _connection?.Dispose(); } catch { }
            _connection = null;
            _proxy = null;
            throw;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task ResetAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            try { _connection?.Dispose(); } catch { }
            _connection = null;
            _proxy = null;
        }
        finally
        {
            _gate.Release();
        }
    }

    private static string ToIso(DateTimeOffset value) => value.ToUniversalTime().ToString("O");

    public void Dispose()
    {
        try { _connection?.Dispose(); } catch { }
        _connection = null;
        _proxy = null;
        _gate.Dispose();
    }
}
