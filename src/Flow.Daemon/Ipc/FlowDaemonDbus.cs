using System;
using System.Threading.Tasks;
using Flow.Application.Monitoring;
using Flow.Application.Queries;
using Flow.Contracts.Ipc;
using Tmds.DBus;

namespace Flow.Daemon.Ipc;

public sealed class FlowDaemonDbusService : IFlowDaemonDbus
{
    public static readonly ObjectPath Path = new(FlowIpc.ObjectPath);
    private readonly IMonitoringStatusService _status;
    private readonly IUsageQueryService _queries;

    public FlowDaemonDbusService(IMonitoringStatusService status, IUsageQueryService queries)
    {
        _status = status;
        _queries = queries;
    }

    public ObjectPath ObjectPath => Path;

    public Task<int> GetContractVersionAsync() => Task.FromResult(FlowIpc.ContractVersion);

    public Task<string> GetStatusJsonAsync()
        => Task.FromResult(IpcJson.Serialize(_status.GetStatus()));

    public async Task<string> GetUsageSummaryJsonAsync(string startUtcIso, string endUtcIso, string interfaceId)
    {
        var (start, end, iface) = ParseRange(startUtcIso, endUtcIso, interfaceId);
        var result = await _queries.GetSummaryAsync(start, end, iface).ConfigureAwait(false);
        return IpcJson.Serialize(result);
    }

    public async Task<string> GetTopApplicationsJsonAsync(string startUtcIso, string endUtcIso, string interfaceId, int limit)
    {
        var (start, end, iface) = ParseRange(startUtcIso, endUtcIso, interfaceId);
        var result = await _queries.GetTopApplicationsAsync(start, end, iface, limit <= 0 ? 20 : limit).ConfigureAwait(false);
        return IpcJson.Serialize(result);
    }

    public async Task<string> GetUsageTimeSeriesJsonAsync(string startUtcIso, string endUtcIso, string interfaceId, string applicationKey)
    {
        var (start, end, iface) = ParseRange(startUtcIso, endUtcIso, interfaceId);
        var app = string.IsNullOrWhiteSpace(applicationKey) ? null : applicationKey;
        var result = await _queries.GetUsageTimeSeriesAsync(start, end, iface, app).ConfigureAwait(false);
        return IpcJson.Serialize(result);
    }

    public async Task<string> GetInterfaceUsageJsonAsync(string startUtcIso, string endUtcIso)
    {
        var (start, end, _) = ParseRange(startUtcIso, endUtcIso, string.Empty);
        var result = await _queries.GetInterfaceUsageAsync(start, end).ConfigureAwait(false);
        return IpcJson.Serialize(result);
    }

    public async Task<string> GetTopProcessesJsonAsync(string startUtcIso, string endUtcIso, string interfaceId, int limit)
    {
        var (start, end, iface) = ParseRange(startUtcIso, endUtcIso, interfaceId);
        var result = await _queries.GetTopProcessesAsync(start, end, iface, limit <= 0 ? 20 : limit).ConfigureAwait(false);
        return IpcJson.Serialize(result);
    }

    private static (DateTimeOffset start, DateTimeOffset end, string? iface) ParseRange(string startUtcIso, string endUtcIso, string interfaceId)
    {
        var start = DateTimeOffset.Parse(startUtcIso, null, System.Globalization.DateTimeStyles.RoundtripKind).ToUniversalTime();
        var end = DateTimeOffset.Parse(endUtcIso, null, System.Globalization.DateTimeStyles.RoundtripKind).ToUniversalTime();
        var iface = string.IsNullOrWhiteSpace(interfaceId) ? null : interfaceId;
        return (start, end, iface);
    }
}
