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

    public async Task<MonitoringStatusDto> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        var proxy = await GetProxyAsync().ConfigureAwait(false);
        var json = await proxy.GetStatusJsonAsync().ConfigureAwait(false);
        return IpcJson.Deserialize<MonitoringStatusDto>(json) ?? throw new InvalidOperationException("Invalid status response from daemon.");
    }

    public async Task<UsageSummaryDto> GetSummaryAsync(DateTimeOffset startUtc, DateTimeOffset endUtc, string? interfaceId = null, CancellationToken cancellationToken = default)
    {
        var proxy = await GetProxyAsync().ConfigureAwait(false);
        var json = await proxy.GetUsageSummaryJsonAsync(ToIso(startUtc), ToIso(endUtc), interfaceId ?? string.Empty).ConfigureAwait(false);
        return IpcJson.Deserialize<UsageSummaryDto>(json) ?? throw new InvalidOperationException("Invalid summary response from daemon.");
    }

    public async Task<IReadOnlyList<ApplicationUsageDto>> GetTopApplicationsAsync(DateTimeOffset startUtc, DateTimeOffset endUtc, string? interfaceId = null, int limit = 20, CancellationToken cancellationToken = default)
    {
        var proxy = await GetProxyAsync().ConfigureAwait(false);
        var json = await proxy.GetTopApplicationsJsonAsync(ToIso(startUtc), ToIso(endUtc), interfaceId ?? string.Empty, limit).ConfigureAwait(false);
        return IpcJson.Deserialize<IReadOnlyList<ApplicationUsageDto>>(json) ?? Array.Empty<ApplicationUsageDto>();
    }

    public async Task<IReadOnlyList<UsageTimePointDto>> GetUsageTimeSeriesAsync(DateTimeOffset startUtc, DateTimeOffset endUtc, string? interfaceId = null, string? applicationKey = null, CancellationToken cancellationToken = default)
    {
        var proxy = await GetProxyAsync().ConfigureAwait(false);
        var json = await proxy.GetUsageTimeSeriesJsonAsync(ToIso(startUtc), ToIso(endUtc), interfaceId ?? string.Empty, applicationKey ?? string.Empty).ConfigureAwait(false);
        return IpcJson.Deserialize<IReadOnlyList<UsageTimePointDto>>(json) ?? Array.Empty<UsageTimePointDto>();
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
            // Probe contract to fail fast if service is not present/compatible.
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

    private static string ToIso(DateTimeOffset value) => value.ToUniversalTime().ToString("O");

    public void Dispose()
    {
        try { _connection?.Dispose(); } catch { }
        _connection = null;
        _proxy = null;
        _gate.Dispose();
    }
}
