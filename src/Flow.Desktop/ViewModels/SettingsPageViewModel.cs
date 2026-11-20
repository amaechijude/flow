using System;
using CommunityToolkit.Mvvm.ComponentModel;
using Flow.Contracts.Ipc;
using Flow.Desktop.Services;

namespace Flow.Desktop.ViewModels;

/// <summary>
/// Read-only configuration and caveat disclosure. There are no fake toggles:
/// every value shown is either a real environment value, a shipped policy, or
/// the current snapshot's own provenance.
/// </summary>
public partial class SettingsPageViewModel : ViewModelBase
{
    [ObservableProperty] public partial string DatabasePathText { get; set; }
    [ObservableProperty] public partial string ExportDirectoryText { get; set; }
    [ObservableProperty] public partial string DataSourceText { get; set; } = "not queried";
    [ObservableProperty] public partial string ProviderText { get; set; } = "n/a";
    [ObservableProperty] public partial string MeasurementText { get; set; } = "n/a";
    [ObservableProperty] public partial string StateText { get; set; } = "unknown";
    [ObservableProperty] public partial string ContractText { get; set; } = $"{FlowIpc.ServiceName} {FlowIpc.ObjectPath} · contract v{FlowIpc.ContractVersion}";
    [ObservableProperty] public partial string DisplayTimeZoneText { get; set; }
    [ObservableProperty] public partial string GranularityText { get; set; } =
        "≤2h: 1m · ≤2d: 5m · ≤31d: 1h · ≤366d: 1d · older: 1mo. One granularity per query; coarse boundary buckets are included whole.";
    [ObservableProperty] public partial string RetentionText { get; set; } =
        "1m kept 7 days · 5m kept 35 days · 1h kept 400 days · 1d and 1mo kept indefinitely.";
    [ObservableProperty] public partial string CaveatsText { get; set; } =
        "Attribution is per-process only when the active provider emits attributed events; unattributed traffic is shown separately and is not zero. " +
        "ebpf counts TCP and UDP send/recv bytes (QUIC as UDP payload) and correlates an interface where the socket route resolves; unresolved-interface bytes are shown without an interface. " +
        "Identity uses event-time thread-group-leader start time; LRU eviction under high child churn can still over-attribute a small share of bytes, so these figures are not billing-grade. " +
        "All timestamps are stored/exported in UTC and displayed in local time. This prototype is not a packaged release.";

    public SettingsPageViewModel()
    {
        DatabasePathText = AppPaths.DatabasePath();
        var exportDirectory = Environment.GetEnvironmentVariable(AppPaths.ExportDirectoryVariable);
        ExportDirectoryText = string.IsNullOrWhiteSpace(exportDirectory)
            ? "not set (a file picker is used)"
            : exportDirectory;
        var offset = DateTimeOffset.Now.Offset;
        DisplayTimeZoneText = $"UTC{offset.TotalHours:+0.00;-0.00;0.00} (offset {offset})";
    }

    public void Apply(UsageSnapshot snapshot)
    {
        DataSourceText = snapshot.IpcLive
            ? $"IPC live · usage {snapshot.UsageDataSource} · status {snapshot.StatusDataSource}"
            : $"SQLite fallback · usage {snapshot.UsageDataSource} · status unavailable (not fabricated)";
        ProviderText = snapshot.Status.ProviderName ?? "n/a";
        MeasurementText = snapshot.Status.MeasurementDescription ?? "Not reported by daemon.";
        StateText = snapshot.Status.State.ToString();
    }
}
