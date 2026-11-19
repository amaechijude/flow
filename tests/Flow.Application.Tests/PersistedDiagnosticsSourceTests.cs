using Flow.Application.Monitoring;
using Flow.Application.Persistence;
using Flow.Domain.Identity;
using Flow.Domain.Monitoring;

namespace Flow.Application.Tests;

public class PersistedDiagnosticsSourceTests
{
    [Fact]
    public void NoSessionReportsUnavailable()
    {
        var source = new PersistedDiagnosticsSource(new FakeMetadataStore(null));

        var snapshot = source.GetDiagnosticsSnapshot();

        Assert.Equal(MonitoringState.Unavailable, snapshot.State);
        Assert.Null(snapshot.ProviderName);
        Assert.Contains("No monitoring session", snapshot.LastError);
    }

    [Fact]
    public void UsesPersistedSessionWithoutInventingCounters()
    {
        var session = new MonitoringSessionSnapshot(
            7,
            DateTimeOffset.Parse("2026-11-19T10:00:00Z"),
            DateTimeOffset.Parse("2026-11-19T11:00:00Z"),
            "ebpf",
            "v3",
            MonitoringState.Degraded,
            "write delayed");
        var source = new PersistedDiagnosticsSource(new FakeMetadataStore(session));

        var snapshot = source.GetDiagnosticsSnapshot();

        Assert.Equal(MonitoringState.Degraded, snapshot.State);
        Assert.Equal("ebpf", snapshot.ProviderName);
        Assert.Equal("write delayed", snapshot.LastError);
        Assert.Equal(0, snapshot.EventsReceived);
        Assert.Equal(0, snapshot.EventsAggregated);
    }

    private sealed class FakeMetadataStore(MonitoringSessionSnapshot? session) : IMonitoringMetadataStore
    {
        public Task UpsertApplicationAsync(ApplicationIdentity application, DateTimeOffset seenAtUtc, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task UpsertProcessAsync(ProcessIdentity process, string? applicationKey, DateTimeOffset seenAtUtc, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task UpsertInterfaceAsync(string interfaceName, string? interfaceType, DateTimeOffset seenAtUtc, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task<long> StartMonitoringSessionAsync(string? providerName, string? measurementVersion, MonitoringState healthState, string? diagnostics, CancellationToken cancellationToken = default)
            => Task.FromResult(1L);

        public Task EndMonitoringSessionAsync(long sessionId, MonitoringState healthState, string? diagnostics, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public MonitoringSessionSnapshot? GetLatestMonitoringSession() => session;
    }
}
