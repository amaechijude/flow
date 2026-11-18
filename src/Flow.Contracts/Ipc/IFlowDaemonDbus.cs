using System;
using System.Threading.Tasks;
using Tmds.DBus;

namespace Flow.Contracts.Ipc;

[DBusInterface(FlowIpc.InterfaceName)]
public interface IFlowDaemonDbus : IDBusObject
{
    Task<int> GetContractVersionAsync();
    Task<string> GetStatusJsonAsync();
    Task<string> GetUsageSummaryJsonAsync(string startUtcIso, string endUtcIso, string interfaceId);
    Task<string> GetTopApplicationsJsonAsync(string startUtcIso, string endUtcIso, string interfaceId, int limit);
    Task<string> GetUsageTimeSeriesJsonAsync(string startUtcIso, string endUtcIso, string interfaceId, string applicationKey);
    Task<string> GetInterfaceUsageJsonAsync(string startUtcIso, string endUtcIso);
    Task<string> GetTopProcessesJsonAsync(string startUtcIso, string endUtcIso, string interfaceId, int limit);

    /// <summary>
    /// Batched status-change signal. Handlers receive a JSON-serialized
    /// <see cref="StatusChangedSignalDto"/> payload; the daemon coalesces changes and
    /// emits at most one signal per polling interval.
    /// </summary>
    Task<IDisposable> WatchStatusChangedAsync(Action<string> handler, Action<Exception>? onError = null);
}
