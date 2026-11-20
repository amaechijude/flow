using Flow.Contracts.Ipc;
using Flow.Contracts.Monitoring;
using Flow.Contracts.Usage;
using Flow.Domain.Monitoring;

namespace Flow.Infrastructure.Tests;

public sealed class IpcContractSerializationTests
{
    [Fact]
    public void MonitoringStatusRoundTripsAsJson()
    {
        var dto = new MonitoringStatusDto(
            MonitoringState.Monitoring,
            new DateTimeOffset(2026, 11, 15, 4, 0, 0, TimeSpan.Zero),
            "ProcNetDevMonitor",
            "desc",
            "Monitoring",
            10,
            2,
            new DateTimeOffset(2026, 11, 15, 3, 59, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 11, 15, 4, 0, 0, TimeSpan.Zero),
            null,
            20, 1, 10, 5, 0,
            7,
            new DateTimeOffset(2026, 11, 15, 3, 58, 0, TimeSpan.Zero),
            3,
            new DateTimeOffset(2026, 11, 15, 4, 0, 2, TimeSpan.Zero),
            2.5);

        var json = IpcJson.Serialize(dto);
        var back = IpcJson.Deserialize<MonitoringStatusDto>(json);
        Assert.NotNull(back);
        Assert.Equal(dto.State, back.State);
        Assert.Equal(dto.ProviderName, back.ProviderName);
        Assert.Equal(dto.EventsAggregated, back.EventsAggregated);
        Assert.Equal(dto.BatchesWritten, back.BatchesWritten);
        Assert.Equal(7, back.PendingDurableDeltas);
        Assert.Equal(dto.OldestPendingAtUtc, back.OldestPendingAtUtc);
        Assert.Equal(3, back.DurableFlushAttempts);
        Assert.Equal(dto.NextRetryAtUtc, back.NextRetryAtUtc);
        Assert.Equal(2.5, back.RetryBackoffSeconds);
    }

    [Fact]
    public void OlderStatusJsonWithoutDurableFieldsStillDeserializes()
    {
        const string oldJson = """
            {"State":"Monitoring","LastUpdatedUtc":"2026-11-15T04:00:00+00:00","ProviderName":"p","MeasurementDescription":null,"HealthMessage":null,"AttributedEvents":null,"UnattributedEvents":null,"LastEventAtUtc":null,"LastFlushAtUtc":null,"LastError":null,"EventsReceived":0,"EventsDropped":0,"EventsAggregated":0,"BatchesWritten":0,"WriteErrors":0}
            """;
        var back = IpcJson.Deserialize<MonitoringStatusDto>(oldJson);
        Assert.NotNull(back);
        Assert.Equal(0, back.PendingDurableDeltas);
        Assert.Null(back.OldestPendingAtUtc);
        Assert.Equal(0, back.DurableFlushAttempts);
    }

    [Fact]
    public void StatusChangedSignalRoundTripsAsJson()
    {
        var status = new MonitoringStatusDto(MonitoringState.Degraded, null, "ProcNetDevMonitor", "desc", "test", 1, 2);
        var dto = new StatusChangedSignalDto(new DateTimeOffset(2026, 11, 18, 9, 30, 0, TimeSpan.Zero), "changed", status);

        var json = IpcJson.Serialize(dto);
        var back = IpcJson.Deserialize<StatusChangedSignalDto>(json);

        Assert.NotNull(back);
        Assert.Equal(dto.ObservedUtc, back.ObservedUtc);
        Assert.Equal(dto.Reason, back.Reason);
        Assert.Equal(status, back.Status);
        Assert.Equal(3, FlowIpc.ContractVersion);
    }

    [Fact]
    public void UsageDtosRoundTripAsJson()
    {
        var summary = new UsageSummaryDto(new DateTimeOffset(2026,11,15,0,0,0,TimeSpan.Zero), new DateTimeOffset(2026,11,15,1,0,0,TimeSpan.Zero), null, 1,2,3,4);
        var apps = new[] { new ApplicationUsageDto("app", "App", 1,2,3,4) };
        var ifaces = new[] { new InterfaceUsageDto("eth0", "eth0", "ethernet", 1,2,3,4) };
        var procs = new[] { new ProcessUsageDto("p1", "app", "App", "/bin/app", 1,2,3,4) };
        var series = new[] { new UsageTimePointDto(new DateTimeOffset(2026,11,15,0,0,0,TimeSpan.Zero), 5,6) };

        Assert.NotNull(IpcJson.Deserialize<UsageSummaryDto>(IpcJson.Serialize(summary)));
        Assert.NotNull(IpcJson.Deserialize<ApplicationUsageDto[]>(IpcJson.Serialize(apps)));
        Assert.NotNull(IpcJson.Deserialize<InterfaceUsageDto[]>(IpcJson.Serialize(ifaces)));
        Assert.NotNull(IpcJson.Deserialize<ProcessUsageDto[]>(IpcJson.Serialize(procs)));
        Assert.NotNull(IpcJson.Deserialize<UsageTimePointDto[]>(IpcJson.Serialize(series)));
    }
}
