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
    private FlowDaemonDbusService? _service;
    private CancellationTokenSource? _signalCts;
    private Task? _signalLoop;

    public DbusIpcHostedService(IMonitoringStatusService status, IUsageQueryService queries, ILogger<DbusIpcHostedService> logger)
    {
        _status = status;
        _queries = queries;
        _logger = logger;
    }

    /// <summary>Interval used to poll diagnostics for coalesced status changes.</summary>
    internal TimeSpan StatusPollInterval { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Optional D-Bus address used instead of the ambient session/system bus.
    /// Integration tests use this to target a private <c>dbus-daemon</c>.
    /// </summary>
    internal string? AddressOverride { get; set; }

    /// <summary>Total number of status signals published (test/diagnostic visibility).</summary>
    public long StatusSignalsPublished => Interlocked.Read(ref _signalsPublished);
    private long _signalsPublished;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            var address = AddressOverride ?? Address.Session ?? Address.System;
            if (string.IsNullOrWhiteSpace(address))
            {
                LogUnavailable(_logger, "No session or system D-Bus address available.");
                return;
            }

            _connection = new Connection(address);
            await _connection.ConnectAsync().ConfigureAwait(false);
            await _connection.RegisterServiceAsync(FlowIpc.ServiceName).ConfigureAwait(false);
            _service = new FlowDaemonDbusService(_status, _queries);
            await _connection.RegisterObjectAsync(_service).ConfigureAwait(false);
            LogStarted(_logger, address, FlowIpc.ServiceName, FlowIpc.ObjectPath);

            _signalCts = new CancellationTokenSource();
            _signalLoop = PublishStatusChangesAsync(_signalCts.Token);
        }
        catch (Exception ex)
        {
            LogFailed(_logger, ex);
            await DisposeAsync().ConfigureAwait(false);
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken) => await DisposeAsync().ConfigureAwait(false);

    public async ValueTask DisposeAsync()
    {
        if (_signalCts is not null)
        {
            try { _signalCts.Cancel(); } catch { }
        }
        if (_signalLoop is not null)
        {
            try { await _signalLoop.ConfigureAwait(false); } catch (OperationCanceledException) { } catch (Exception ex) { LogSignalLoopFailed(_logger, ex); }
            _signalLoop = null;
        }
        if (_signalCts is not null)
        {
            _signalCts.Dispose();
            _signalCts = null;
        }
        _service = null;
        if (_connection is not null)
        {
            try { _connection.Dispose(); } catch { }
            _connection = null;
        }
    }

    /// <summary>
    /// Polls the real diagnostics snapshot and publishes a single coalesced signal
    /// whenever the snapshot changes. No synthetic status values are invented.
    /// </summary>
    private async Task PublishStatusChangesAsync(CancellationToken cancellationToken)
    {
        string? lastJson = null;
        try
        {
            using var timer = new PeriodicTimer(StatusPollInterval);
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                var service = _service;
                if (service is null) return;

                var snapshot = _status.GetStatus();
                var json = IpcJson.Serialize(snapshot);
                if (string.Equals(json, lastJson, StringComparison.Ordinal)) continue;

                var reason = lastJson is null ? "initial" : "changed";
                var payload = IpcJson.Serialize(new StatusChangedSignalDto(DateTimeOffset.UtcNow, reason, snapshot));
                service.PublishStatusChanged(payload);
                Interlocked.Increment(ref _signalsPublished);
                lastJson = json;
                LogSignalPublished(_logger, reason, snapshot.State);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // shutdown
        }
    }

    [LoggerMessage(EventId = 2001, Level = LogLevel.Information, Message = "Flow D-Bus IPC started at {Address} service={Service} path={Path}")]
    private static partial void LogStarted(ILogger logger, string address, string service, string path);

    [LoggerMessage(EventId = 2002, Level = LogLevel.Warning, Message = "Flow D-Bus IPC unavailable: {Reason}")]
    private static partial void LogUnavailable(ILogger logger, string reason);

    [LoggerMessage(EventId = 2003, Level = LogLevel.Warning, Message = "Flow D-Bus IPC failed to start")]
    private static partial void LogFailed(ILogger logger, Exception exception);

    [LoggerMessage(EventId = 2004, Level = LogLevel.Debug, Message = "Flow D-Bus status signal published ({Reason}, state={State})")]
    private static partial void LogSignalPublished(ILogger logger, string reason, Flow.Domain.Monitoring.MonitoringState state);

    [LoggerMessage(EventId = 2005, Level = LogLevel.Warning, Message = "Flow D-Bus status signal loop failed")]
    private static partial void LogSignalLoopFailed(ILogger logger, Exception exception);
}
