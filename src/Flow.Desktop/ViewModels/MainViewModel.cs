using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Media;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Flow.Application.Queries;
using Flow.Contracts.Monitoring;
using Flow.Contracts.Usage;
using Flow.Desktop.Services;
using Flow.Domain.Monitoring;

namespace Flow.Desktop.ViewModels;

public partial class MainViewModel : ViewModelBase
{
    private static readonly IBrush LiveBrush = new SolidColorBrush(Color.Parse("#4EDEA3"));
    private static readonly IBrush WarningBrush = new SolidColorBrush(Color.Parse("#F5B44A"));
    private static readonly IBrush ErrorBrush = new SolidColorBrush(Color.Parse("#FF8A93"));
    private static readonly IBrush DimBrush = new SolidColorBrush(Color.Parse("#819096"));

    private readonly IDaemonStatusClient _statusClient;
    private readonly IUsageQueryClient _usageClient;
    private readonly IExportFilePicker? _filePicker;
    private readonly IDaemonChangeNotifier? _notifier;
    private readonly IDataSourceStatus? _dataSourceStatus;
    private readonly IDataSourceStatus? _statusSourceStatus;
    private bool _suppressRefresh;
    private bool _refreshQueued;
    private UsageSnapshot? _lastSnapshot;
    private DateTimeOffset _lastSignalRefresh = DateTimeOffset.MinValue;
    private DateTimeOffset? _lastSignalAtUtc;
    private double _lastStatusRttMs;

    [ObservableProperty] public partial object? CurrentPage { get; set; }
    [ObservableProperty] public partial bool IsOverviewActive { get; set; } = true;
    [ObservableProperty] public partial bool IsApplicationsActive { get; set; }
    [ObservableProperty] public partial bool IsInterfacesActive { get; set; }
    [ObservableProperty] public partial bool IsDiagnosticsActive { get; set; }
    [ObservableProperty] public partial bool IsReportsActive { get; set; }
    [ObservableProperty] public partial bool IsHistoryActive { get; set; }
    [ObservableProperty] public partial bool IsSettingsActive { get; set; }
    [ObservableProperty] public partial bool IsComparisonActive { get; set; }

    [ObservableProperty] public partial string DaemonStateText { get; set; } = "DAEMON STATUS UNKNOWN";
    [ObservableProperty] public partial IBrush DaemonStateBrush { get; set; } = WarningBrush;
    [ObservableProperty] public partial string IpcText { get; set; } = "IPC CHECKING";
    [ObservableProperty] public partial bool IsFallback { get; set; }
    [ObservableProperty] public partial string SourceModeText { get; set; } = "DATA SOURCE CHECKING";
    [ObservableProperty] public partial string LastRefreshText { get; set; } = "Not refreshed yet";
    [ObservableProperty] public partial string ProviderText { get; set; } = "provider n/a";
    [ObservableProperty] public partial string EventsText { get; set; } = "events 0";
    [ObservableProperty] public partial string DropRateText { get; set; } = "drop 0.00%";
    [ObservableProperty] public partial IBrush ProviderStateBrush { get; set; } = DimBrush;
    [ObservableProperty] public partial bool ShowDaemonNotice { get; set; }
    [ObservableProperty] public partial string DaemonNoticeText { get; set; } = "";

    [ObservableProperty] public partial string RangeText { get; set; } = "";
    [ObservableProperty] public partial bool IsBusy { get; set; }
    [ObservableProperty] public partial bool IsCustomRange { get; set; }
    [ObservableProperty] public partial RangeOption? SelectedRange { get; set; }
    [ObservableProperty] public partial InterfaceFilterOption? SelectedInterfaceFilter { get; set; }
    [ObservableProperty] public partial DateTimeOffset? CustomStartDate { get; set; }
    [ObservableProperty] public partial DateTimeOffset? CustomEndDate { get; set; }
    [ObservableProperty] public partial string ExportStatus { get; set; } = "";
    [ObservableProperty] public partial string SignalStatusText { get; set; } = "No D-Bus signal yet";
    [ObservableProperty] public partial string? SelectedComparisonMode { get; set; }

    public ObservableCollection<RangeOption> Ranges { get; } = new(
    [
        new RangeOption("Last 1 hour", TimeSpan.FromHours(1)),
        new RangeOption("Last 6 hours", TimeSpan.FromHours(6)),
        new RangeOption("Last 12 hours", TimeSpan.FromHours(12)),
        new RangeOption("Last 24 hours", TimeSpan.FromHours(24)),
        new RangeOption("Today", TimeSpan.Zero),
        new RangeOption("Yesterday", TimeSpan.Zero),
        new RangeOption("Last 7 days", TimeSpan.FromDays(7)),
        new RangeOption("Last 30 days", TimeSpan.FromDays(30)),
        new RangeOption("Custom range", TimeSpan.Zero, true),
    ]);

    public ObservableCollection<InterfaceFilterOption> InterfaceFilters { get; } = new([new InterfaceFilterOption("All interfaces", null)]);

    public IReadOnlyList<string> ComparisonModes { get; } = ["Previous period", "Previous day", "Previous week"];

    public OverviewPageViewModel Overview { get; } = new();
    public ApplicationsPageViewModel Applications { get; } = new();
    public InterfacesPageViewModel Interfaces { get; } = new();
    public DiagnosticsPageViewModel Diagnostics { get; } = new();
    public ReportsPageViewModel Reports { get; }
    public HistoryPageViewModel History { get; } = new();
    public SettingsPageViewModel Settings { get; } = new();
    public ComparisonPageViewModel Comparison { get; } = new();

    public MainViewModel(
        IDaemonStatusClient statusClient,
        IUsageQueryClient usageClient,
        IExportFilePicker? filePicker = null,
        IDaemonChangeNotifier? notifier = null)
    {
        _statusClient = statusClient;
        _usageClient = usageClient;
        _filePicker = filePicker;
        _notifier = notifier;
        _dataSourceStatus = usageClient as IDataSourceStatus;
        _statusSourceStatus = statusClient as IDataSourceStatus;
        Reports = new ReportsPageViewModel(
            () => _ = ExportCsvAsync(),
            () => _ = ExportJsonAsync());

        var todayLocal = DateTimeOffset.Now.Date;
        CustomStartDate = new DateTimeOffset(todayLocal, DateTimeOffset.Now.Offset);
        CustomEndDate = new DateTimeOffset(todayLocal, DateTimeOffset.Now.Offset);
        _suppressRefresh = true;
        SelectedRange = Ranges.First(r => r.Label == "Last 24 hours");
        SelectedInterfaceFilter = InterfaceFilters[0];
        SelectedComparisonMode = ComparisonModes[0];
        _suppressRefresh = false;

        CurrentPage = Overview;

        if (_notifier is not null)
            _notifier.StatusChanged += OnDaemonStatusChanged;

        _ = RefreshAsync();
    }

    partial void OnSelectedRangeChanged(RangeOption? value)
    {
        IsCustomRange = value?.IsCustom == true;
        if (!_suppressRefresh && value is not null && !value.IsCustom) _ = RefreshAsync();
    }

    partial void OnSelectedInterfaceFilterChanged(InterfaceFilterOption? value)
    {
        if (!_suppressRefresh && value is not null) _ = RefreshAsync();
    }

    partial void OnSelectedComparisonModeChanged(string? value)
    {
        if (!_suppressRefresh && value is not null) _ = RefreshAsync();
    }

    [RelayCommand]
    private void Navigate(string? id)
    {
        switch (id)
        {
            case "applications":
                CurrentPage = Applications;
                break;
            case "interfaces":
                CurrentPage = Interfaces;
                break;
            case "diagnostics":
                CurrentPage = Diagnostics;
                break;
            case "reports":
                CurrentPage = Reports;
                break;
            case "history":
                CurrentPage = History;
                break;
            case "settings":
                CurrentPage = Settings;
                break;
            case "comparison":
                CurrentPage = Comparison;
                break;
            default:
                CurrentPage = Overview;
                break;
        }

        IsOverviewActive = ReferenceEquals(CurrentPage, Overview);
        IsApplicationsActive = ReferenceEquals(CurrentPage, Applications);
        IsInterfacesActive = ReferenceEquals(CurrentPage, Interfaces);
        IsDiagnosticsActive = ReferenceEquals(CurrentPage, Diagnostics);
        IsReportsActive = ReferenceEquals(CurrentPage, Reports);
        IsHistoryActive = ReferenceEquals(CurrentPage, History);
        IsSettingsActive = ReferenceEquals(CurrentPage, Settings);
        IsComparisonActive = ReferenceEquals(CurrentPage, Comparison);
    }

    [RelayCommand]
    public async Task RefreshAsync()
    {
        if (IsBusy)
        {
            _refreshQueued = true;
            return;
        }

        try
        {
            IsBusy = true;
            _refreshQueued = false;

            var statusWatch = Stopwatch.StartNew();
            var status = await _statusClient.GetStatusAsync();
            statusWatch.Stop();
            _lastStatusRttMs = statusWatch.Elapsed.TotalMilliseconds;

            var ipcLive = status.State != MonitoringState.Unavailable;
            var (start, end, rangeLabel) = GetCurrentRange();
            var ifaceId = SelectedInterfaceFilter?.InterfaceId;

            RangeText = $"{rangeLabel} (local): {DisplayTime.Full(start)} – {DisplayTime.Full(end)}" +
                        (ifaceId is null ? "" : $" · interface {ifaceId}");

            // Interface breakdown is intentionally unfiltered so users can see every mapped interface in range.
            var interfaces = await _usageClient.GetInterfaceUsageAsync(start, end);
            var summary = await _usageClient.GetSummaryAsync(start, end, ifaceId);
            var applications = await _usageClient.GetTopApplicationsAsync(start, end, ifaceId, limit: 1000);
            var processes = await _usageClient.GetTopProcessesAsync(start, end, ifaceId, limit: 1000);
            var series = await _usageClient.GetUsageTimeSeriesAsync(start, end, ifaceId);

            UpdateInterfaceFilters(interfaces);

            var snapshot = new UsageSnapshot(
                start,
                end,
                rangeLabel,
                ifaceId,
                summary,
                applications,
                processes,
                interfaces,
                series,
                status,
                _dataSourceStatus?.UsageDataSource ?? "usage query path",
                _statusSourceStatus?.StatusDataSource ?? "status query path",
                ipcLive,
                _notifier?.IsSignalSubscribed ?? false,
                _lastSignalAtUtc ?? _notifier?.LastSignalAtUtc,
                DateTimeOffset.UtcNow);

            _lastSnapshot = snapshot;
            Overview.Apply(snapshot);
            Applications.Apply(snapshot);
            Interfaces.Apply(snapshot);
            Diagnostics.Apply(snapshot);

            var resolution = UsageGranularityPolicy.Choose(start, end);
            var resolutionLabel = resolution == "1m"
                ? "1m"
                : $"{resolution} (falls back to 1m until rollups exist)";
            Reports.Apply(snapshot);
            History.Apply(snapshot, resolutionLabel);
            Settings.Apply(snapshot);
            await ApplyComparisonAsync(snapshot, start, end, resolution, ifaceId);

            ApplyShellStatus(status, ipcLive);
        }
        catch (Exception ex)
        {
            DaemonStateText = "DAEMON OFFLINE";
            DaemonStateBrush = ErrorBrush;
            IpcText = "IPC UNAVAILABLE";
            SourceModeText = "DATA SOURCE FAILED";
            IsFallback = true;
            ShowDaemonNotice = true;
            DaemonNoticeText = $"Query failed: {ex.Message}. Previously loaded values (if any) may be stale; no fabricated data is shown.";
            ExportStatus = $"Refresh failed: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
            LastRefreshText = $"Refreshed {DateTimeOffset.Now:ddd HH:mm:ss} · status RTT {_lastStatusRttMs:0} ms";
            if (_refreshQueued && !IsBusy)
            {
                _refreshQueued = false;
                _ = RefreshAsync();
            }
        }
    }

    private async Task ApplyComparisonAsync(
        UsageSnapshot snapshot,
        DateTimeOffset start,
        DateTimeOffset end,
        string granularity,
        string? ifaceId)
    {
        try
        {
            var mode = SelectedComparisonMode ?? ComparisonModes[0];
            var (previousStart, previousEnd) = mode switch
            {
                "Previous day" => (start - TimeSpan.FromDays(1), start),
                "Previous week" => (start - TimeSpan.FromDays(7), start),
                _ => (start - (end - start), start),
            };

            var summary = await _usageClient.GetSummaryAsync(previousStart, previousEnd, ifaceId);
            var applications = await _usageClient.GetTopApplicationsAsync(previousStart, previousEnd, ifaceId, limit: 1000);
            var processes = await _usageClient.GetTopProcessesAsync(previousStart, previousEnd, ifaceId, limit: 1000);
            var interfaces = await _usageClient.GetInterfaceUsageAsync(previousStart, previousEnd);

            var currentWindow = new UsageComparisonWindow(
                snapshot.StartUtc, snapshot.EndUtc, granularity,
                snapshot.Summary, snapshot.Applications, snapshot.Processes, snapshot.Interfaces);
            var previousWindow = new UsageComparisonWindow(
                previousStart, previousEnd, granularity,
                summary, applications, processes, interfaces);

            Comparison.Apply(UsageComparison.Compare(currentWindow, previousWindow), mode);
        }
        catch (Exception ex)
        {
            // A failed comparison must not discard the already-loaded main view.
            Comparison.SetUnavailable(ex.Message);
        }
    }

    private void ApplyShellStatus(MonitoringStatusDto status, bool ipcLive)
    {
        switch (status.State)
        {
            case MonitoringState.Monitoring:
                DaemonStateText = "DAEMON ACTIVE";
                DaemonStateBrush = LiveBrush;
                break;
            case MonitoringState.Degraded:
                DaemonStateText = "DAEMON DEGRADED";
                DaemonStateBrush = WarningBrush;
                break;
            case MonitoringState.Paused:
                DaemonStateText = "DAEMON PAUSED";
                DaemonStateBrush = WarningBrush;
                break;
            case MonitoringState.Initializing:
                DaemonStateText = "DAEMON STARTING";
                DaemonStateBrush = WarningBrush;
                break;
            default:
                DaemonStateText = "DAEMON OFFLINE";
                DaemonStateBrush = ErrorBrush;
                break;
        }

        IpcText = ipcLive ? $"IPC LIVE · {_lastStatusRttMs:0.0} ms" : "IPC UNAVAILABLE";
        ProviderText = status.ProviderName ?? "provider n/a";
        EventsText = $"events {status.EventsReceived:N0}";
        var dropShare = status.EventsReceived <= 0 ? 0 : (double)status.EventsDropped / status.EventsReceived;
        DropRateText = $"drop {dropShare * 100:0.00}%";
        ProviderStateBrush = status.State == MonitoringState.Monitoring ? LiveBrush : WarningBrush;

        IsFallback = !ipcLive;
        SourceModeText = ipcLive ? "IPC LIVE MODE" : "SQLITE FALLBACK MODE";
        ShowDaemonNotice = !ipcLive || status.State is MonitoringState.Degraded or MonitoringState.Paused;
        DaemonNoticeText = !ipcLive
            ? "Live daemon status is unavailable over D-Bus. Historical usage is read from the local SQLite database (same persisted data); live counters are not fabricated."
            : status.State == MonitoringState.Degraded
                ? $"Daemon reports degraded health: {status.LastError ?? status.HealthMessage ?? "no detail"}. Buffered writes may be retried; values can lag."
                : status.State == MonitoringState.Paused
                    ? "Monitoring is paused; displayed buckets reflect the last persisted state."
                    : "";

        SignalStatusText = _notifier?.IsSignalSubscribed == true
            ? (LastSignalAtText())
            : "D-Bus status signal not subscribed";
    }

    private string LastSignalAtText()
        => _lastSignalAtUtc is null ? "Signal subscribed · none received yet" : $"Signal {_lastSignalAtUtc.Value.ToLocalTime():ddd HH:mm:ss}";

    private void OnDaemonStatusChanged(object? sender, DaemonStatusChangedEventArgs e)
    {
        Dispatcher.UIThread.Post(() =>
        {
            _lastSignalAtUtc = e.ReceivedUtc;
            SignalStatusText = $"Status signal {e.ReceivedUtc.ToLocalTime():ddd HH:mm:ss} ({e.Signal?.Reason ?? "changed"})";

            // Coalesce bursts: the daemon already batches status changes, and we
            // additionally avoid refreshing more than once per second.
            if (DateTimeOffset.UtcNow - _lastSignalRefresh < TimeSpan.FromSeconds(1)) return;
            _lastSignalRefresh = DateTimeOffset.UtcNow;
            _ = RefreshAsync();
        });
    }

    [RelayCommand]
    private Task ExportCsvAsync() => ExportAsync("csv");

    [RelayCommand]
    private Task ExportJsonAsync() => ExportAsync("json");

    private async Task ExportAsync(string format)
    {
        if (_lastSnapshot is null)
        {
            ExportStatus = "Nothing to export yet. Refresh while data is available first.";
            return;
        }

        try
        {
            var now = DateTimeOffset.UtcNow;
            var extension = format == "json" ? "json" : "csv";
            var content = format == "json" ? UsageExport.ToJson(_lastSnapshot) : UsageExport.ToCsv(_lastSnapshot);
            var suggested = UsageExport.SuggestedFileName(extension, now);

            string? path = null;
            var directory = Environment.GetEnvironmentVariable("Flow__ExportDirectory");
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
                path = Path.Combine(directory, suggested);
            }
            else if (_filePicker is not null)
            {
                path = await _filePicker.PickSaveFileAsync(suggested, extension, format == "json" ? "JSON document" : "CSV document");
            }

            if (string.IsNullOrWhiteSpace(path))
            {
                ExportStatus = "Export cancelled (no destination selected).";
                return;
            }

            await File.WriteAllTextAsync(path, content);
            ExportStatus = $"Exported {format.ToUpperInvariant()} → {path}";
        }
        catch (Exception ex)
        {
            ExportStatus = $"Export failed: {ex.Message}";
        }
    }

    private (DateTimeOffset start, DateTimeOffset end, string label) GetCurrentRange()
    {
        if (SelectedRange?.IsCustom == true)
        {
            var sLocal = (CustomStartDate ?? DateTimeOffset.Now).Date;
            var eLocal = (CustomEndDate ?? CustomStartDate ?? DateTimeOffset.Now).Date;
            if (eLocal < sLocal) (sLocal, eLocal) = (eLocal, sLocal);
            var localOffset = DateTimeOffset.Now.Offset;
            return (new DateTimeOffset(sLocal, localOffset).ToUniversalTime(), new DateTimeOffset(eLocal.AddDays(1), localOffset).ToUniversalTime(), "Custom range");
        }
        if (string.Equals(SelectedRange?.Label, "Today", StringComparison.Ordinal))
        {
            var localOffset = DateTimeOffset.Now.Offset;
            var todayLocal = DateTimeOffset.Now.Date;
            return (new DateTimeOffset(todayLocal, localOffset).ToUniversalTime(), new DateTimeOffset(todayLocal.AddDays(1), localOffset).ToUniversalTime(), "Today");
        }
        if (string.Equals(SelectedRange?.Label, "Yesterday", StringComparison.Ordinal))
        {
            var localOffset = DateTimeOffset.Now.Offset;
            var todayLocal = DateTimeOffset.Now.Date;
            return (new DateTimeOffset(todayLocal.AddDays(-1), localOffset).ToUniversalTime(), new DateTimeOffset(todayLocal, localOffset).ToUniversalTime(), "Yesterday");
        }
        var end = DateTimeOffset.UtcNow;
        var duration = SelectedRange?.Duration ?? TimeSpan.FromHours(24);
        return (end - duration, end, SelectedRange?.Label ?? "Range");
    }

    private void UpdateInterfaceFilters(IReadOnlyList<InterfaceUsageDto> interfaces)
    {
        var desired = new List<InterfaceFilterOption> { new("All interfaces", null) };
        desired.AddRange(interfaces.Select(i => new InterfaceFilterOption(
            string.IsNullOrWhiteSpace(i.InterfaceName) ? i.InterfaceKey : $"{i.InterfaceName} ({i.InterfaceKey})",
            i.InterfaceKey)));

        var currentId = SelectedInterfaceFilter?.InterfaceId;
        var same = InterfaceFilters.Count == desired.Count && InterfaceFilters.Zip(desired).All(z => z.First.InterfaceId == z.Second.InterfaceId && z.First.Label == z.Second.Label);
        if (same) return;

        _suppressRefresh = true;
        try
        {
            InterfaceFilters.Clear();
            foreach (var option in desired) InterfaceFilters.Add(option);
            SelectedInterfaceFilter = InterfaceFilters.FirstOrDefault(o => o.InterfaceId == currentId) ?? InterfaceFilters[0];
        }
        finally
        {
            _suppressRefresh = false;
        }
    }
}
