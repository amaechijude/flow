using System;
using System.Threading;
using System.Threading.Tasks;
using Flow.Application.Monitoring;
using Flow.Application.Queries;
using Flow.Contracts.Ipc;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Tmds.DBus;

namespace Flow.Daemon.Ipc;

public sealed partial class DbusIpcHostedService : IHostedService, IAsyncDisposable
{
    private readonly IMonitoringStatusService _status;
    private readonly IUsageQueryService _queries;
    private readonly ILogger<DbusIpcHostedService> _logger;
    private Connection? _connection;

    public DbusIpcHostedService(IMonitoringStatusService status, IUsageQueryService queries, ILogger<DbusIpcHostedService> logger)
    {
        _status = status;
        _queries = queries;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            var address = Address.Session ?? Address.System;
            if (string.IsNullOrWhiteSpace(address))
            {
                LogUnavailable(_logger, "No session or system D-Bus address available.");
                return;
            }

            _connection = new Connection(address);
            await _connection.ConnectAsync().ConfigureAwait(false);
            await _connection.RegisterServiceAsync(FlowIpc.ServiceName).ConfigureAwait(false);
            var svc = new FlowDaemonDbusService(_status, _queries);
            await _connection.RegisterObjectAsync(svc).ConfigureAwait(false);
            LogStarted(_logger, address, FlowIpc.ServiceName, FlowIpc.ObjectPath);
        }
        catch (Exception ex)
        {
            LogFailed(_logger, ex);
            await DisposeAsync().ConfigureAwait(false);
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => DisposeAsync().AsTask();

    public ValueTask DisposeAsync()
    {
        if (_connection is not null)
        {
            try { _connection.Dispose(); } catch { }
            _connection = null;
        }
        return ValueTask.CompletedTask;
    }

    [LoggerMessage(EventId = 2001, Level = LogLevel.Information, Message = "Flow D-Bus IPC started at {Address} service={Service} path={Path}")]
    private static partial void LogStarted(ILogger logger, string address, string service, string path);

    [LoggerMessage(EventId = 2002, Level = LogLevel.Warning, Message = "Flow D-Bus IPC unavailable: {Reason}")]
    private static partial void LogUnavailable(ILogger logger, string reason);

    [LoggerMessage(EventId = 2003, Level = LogLevel.Warning, Message = "Flow D-Bus IPC failed to start")]
    private static partial void LogFailed(ILogger logger, Exception exception);
}
