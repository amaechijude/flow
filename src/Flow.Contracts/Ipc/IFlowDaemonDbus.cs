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
}
