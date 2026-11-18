using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using Flow.Application.Monitoring;
using Flow.Application.Queries;
using Flow.Contracts.Ipc;
using Flow.Contracts.Monitoring;
using Flow.Contracts.Usage;
using Flow.Daemon.Ipc;
using Flow.Desktop.Services;
using Flow.Domain.Monitoring;
using Microsoft.Extensions.Logging.Abstractions;
using Tmds.DBus;

namespace Flow.SystemTests;

/// <summary>
/// End-to-end IPC coverage against a private, real <c>dbus-daemon</c>. The daemon
/// service object, the desktop client and the JSON contract are all exercised as
/// they run in production; only the data sources are fakes.
/// </summary>
[SuppressMessage("Design", "CA1001:Types that own disposable fields should be disposable", Justification = "xUnit's IAsyncLifetime disposes the connection.")]
public sealed class DbusIntegrationTests : IAsyncLifetime
{
    private static readonly DateTimeOffset Start = new(2026, 11, 18, 10, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset End = Start.AddHours(1);

    private readonly FakeUsageQueries _queries = new();
    private Process? _bus;
    private string _address = string.Empty;
    private Connection? _serverConnection;
    private FlowDaemonDbusService? _service;

    public async Task InitializeAsync()
    {
        _bus = await PrivateBus.StartAsync();
        _address = await PrivateBus.ReadAddressAsync(_bus);
        _serverConnection = new Connection(_address);
        await _serverConnection.ConnectAsync();
        await _serverConnection.RegisterServiceAsync(FlowIpc.ServiceName);
        _service = new FlowDaemonDbusService(new FakeStatusService(), _queries);
        await _serverConnection.RegisterObjectAsync(_service);
    }

    public async Task DisposeAsync()
    {
        _service = null;
        if (_serverConnection is not null)
        {
            try { _serverConnection.Dispose(); } catch { }
            _serverConnection = null;
        }
        await PrivateBus.StopAsync(_bus);
        _bus = null;
    }

    [Fact]
    public async Task ContractVersionIsV3()
    {
        Assert.Equal(3, FlowIpc.ContractVersion);

        using var connection = new Connection(_address);
        await connection.ConnectAsync();
        var proxy = connection.CreateProxy<IFlowDaemonDbus>(FlowIpc.ServiceName, FlowIpc.ObjectPath);
        Assert.Equal(FlowIpc.ContractVersion, await proxy.GetContractVersionAsync());
    }

    [Fact]
    public async Task EveryQueryMethodRoundTripsOverDbus()
    {
        using var client = new DbusDaemonClient(_address);

        var status = await client.GetStatusAsync();
        Assert.Equal(MonitoringState.Monitoring, status.State);
        Assert.Equal("test-provider", status.ProviderName);
        Assert.True(client.IsSignalSubscribed);
        Assert.Null(client.LastTransportError);

        var summary = await client.GetSummaryAsync(Start, End);
        Assert.Equal(_queries.Summary, summary);

        var applications = await client.GetTopApplicationsAsync(Start, End, limit: 5);
        Assert.Equal(_queries.Applications, applications);

        var timeSeries = await client.GetUsageTimeSeriesAsync(Start, End, applicationKey: "app:test");
        Assert.Equal(_queries.TimeSeries, timeSeries);

        var interfaces = await client.GetInterfaceUsageAsync(Start, End);
        Assert.Equal(_queries.Interfaces, interfaces);

        var processes = await client.GetTopProcessesAsync(Start, End, limit: 5);
        Assert.Equal(_queries.Processes, processes);

        Assert.NotNull(client.LastCallAtUtc);
        Assert.Null(client.LastTransportError);
    }

    [Fact]
    public async Task StatusChangedSignalReachesDesktopClient()
    {
        using var client = new DbusDaemonClient(_address);
        var received = new TaskCompletionSource<DaemonStatusChangedEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
        client.StatusChanged += (_, args) => received.TrySetResult(args);

        // First call connects the client and installs the signal match rule.
        await client.GetStatusAsync();
        Assert.True(client.IsSignalSubscribed);

        var snapshot = new FakeStatusService().GetStatus();
        var payload = IpcJson.Serialize(new StatusChangedSignalDto(DateTimeOffset.UtcNow, "test", snapshot));
        _service!.PublishStatusChanged(payload);

        var args = await received.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(payload, args.RawJson);
        Assert.NotNull(args.Signal);
        Assert.Equal("test", args.Signal!.Reason);
        Assert.Equal(snapshot, args.Signal.Status);
        Assert.NotNull(client.LastSignalAtUtc);
        Assert.Null(client.LastTransportError);
    }
}

/// <summary>
/// Exercises the hosted IPC service's coalescing publisher: it must emit an
/// <c>initial</c> signal and only emit again when the diagnostics snapshot changes.
/// </summary>
public sealed class DbusStatusSignalPublisherTests : IAsyncLifetime
{
    private Process? _bus;
    private string _address = string.Empty;

    public async Task InitializeAsync()
    {
        _bus = await PrivateBus.StartAsync();
        _address = await PrivateBus.ReadAddressAsync(_bus);
    }

    public async Task DisposeAsync()
    {
        await PrivateBus.StopAsync(_bus);
        _bus = null;
    }

    [Fact]
    public async Task PublisherCoalescesStatusChangesAndDoesNotRepeat()
    {
        var status = new FakeStatusService();
        var service = new DbusIpcHostedService(status, new FakeUsageQueries(), NullLogger<DbusIpcHostedService>.Instance)
        {
            AddressOverride = _address,
            StatusPollInterval = TimeSpan.FromMilliseconds(100)
        };

        using var client = new DbusDaemonClient(_address);
        var degradedSignals = new List<DaemonStatusChangedEventArgs>();
        var degraded = new TaskCompletionSource<DaemonStatusChangedEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
        client.StatusChanged += (_, args) =>
        {
            if (args.Signal?.Status.State != MonitoringState.Degraded) return;
            lock (degradedSignals)
            {
                degradedSignals.Add(args);
            }
            degraded.TrySetResult(args);
        };

        try
        {
            await service.StartAsync(CancellationToken.None);
            await client.GetStatusAsync();
            Assert.True(client.IsSignalSubscribed);

            status.MarkDegraded();
            var args = await degraded.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal("test degraded", args.Signal!.Status.HealthMessage);
            Assert.True(service.StatusSignalsPublished >= 1);

            // The snapshot is now stable, so a coalescing publisher must stay quiet.
            await Task.Delay(500);
            lock (degradedSignals)
            {
                Assert.Single(degradedSignals);
            }
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
            await service.DisposeAsync();
        }
    }
}

internal static class PrivateBus
{
    public static Task<Process> StartAsync()
    {
        var startInfo = new ProcessStartInfo("dbus-daemon")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        startInfo.ArgumentList.Add("--session");
        startInfo.ArgumentList.Add("--print-address=1");
        startInfo.ArgumentList.Add("--nofork");

        return Task.FromResult(Process.Start(startInfo)
            ?? throw new InvalidOperationException("Failed to start dbus-daemon."));
    }

    public static async Task<string> ReadAddressAsync(Process bus)
    {
        var address = await bus.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10));
        if (string.IsNullOrWhiteSpace(address))
            throw new InvalidOperationException("Private D-Bus daemon did not report an address.");
        return address;
    }

    public static async Task StopAsync(Process? bus)
    {
        if (bus is null) return;
        try { if (!bus.HasExited) bus.Kill(entireProcessTree: true); } catch { }
        try { await bus.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)); } catch { }
        bus.Dispose();
    }
}

internal sealed class FakeStatusService : IMonitoringStatusService
{
    private MonitoringStatusDto _status = new(
        MonitoringState.Monitoring,
        new DateTimeOffset(2026, 11, 18, 10, 0, 1, TimeSpan.Zero),
        "test-provider",
        "test measurement",
        null,
        120,
        7);

    public MonitoringStatusDto GetStatus() => _status;

    public void MarkDegraded() => _status = _status with
    {
        State = MonitoringState.Degraded,
        HealthMessage = "test degraded"
    };
}

internal sealed class FakeUsageQueries : IUsageQueryService
{
    public UsageSummaryDto Summary { get; } = new(Start, Start.AddHours(1), "eth0", 1000, 2000, 10, 20);

    public IReadOnlyList<ApplicationUsageDto> Applications { get; } =
    [
        new("app:test", "Test App", 600, 1200, 4, 8)
    ];

    public IReadOnlyList<UsageTimePointDto> TimeSeries { get; } =
    [
        new(Start, 100, 200),
        new(Start.AddMinutes(5), 300, 400)
    ];

    public IReadOnlyList<InterfaceUsageDto> Interfaces { get; } =
    [
        new("iface:eth0", "eth0", "ethernet", 900, 1800, 10, 20)
    ];

    public IReadOnlyList<ProcessUsageDto> Processes { get; } =
    [
        new("pid:4242:start", "app:test", "Test App", "/usr/bin/test", 500, 1000, 2, 4)
    ];

    private static readonly DateTimeOffset Start = new(2026, 11, 18, 10, 0, 0, TimeSpan.Zero);

    public Task<UsageSummaryDto> GetSummaryAsync(DateTimeOffset startUtc, DateTimeOffset endUtc, string? interfaceId = null, CancellationToken cancellationToken = default)
        => Task.FromResult(Summary);

    public Task<IReadOnlyList<ApplicationUsageDto>> GetTopApplicationsAsync(DateTimeOffset startUtc, DateTimeOffset endUtc, string? interfaceId = null, int limit = 20, CancellationToken cancellationToken = default)
        => Task.FromResult(Applications);

    public Task<IReadOnlyList<UsageTimePointDto>> GetUsageTimeSeriesAsync(DateTimeOffset startUtc, DateTimeOffset endUtc, string? interfaceId = null, string? applicationKey = null, CancellationToken cancellationToken = default)
        => Task.FromResult(TimeSeries);

    public Task<IReadOnlyList<InterfaceUsageDto>> GetInterfaceUsageAsync(DateTimeOffset startUtc, DateTimeOffset endUtc, CancellationToken cancellationToken = default)
        => Task.FromResult(Interfaces);

    public Task<IReadOnlyList<ProcessUsageDto>> GetTopProcessesAsync(DateTimeOffset startUtc, DateTimeOffset endUtc, string? interfaceId = null, int limit = 20, CancellationToken cancellationToken = default)
        => Task.FromResult(Processes);
}
