using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using Flow.Contracts.Monitoring;
using Flow.Contracts.Usage;
using Flow.Desktop.Controls;
using Flow.Domain.Monitoring;

namespace Flow.Desktop.ViewModels;

public sealed record UsageSnapshot(
    DateTimeOffset StartUtc,
    DateTimeOffset EndUtc,
    string RangeLabel,
    string? InterfaceId,
    UsageSummaryDto Summary,
    IReadOnlyList<ApplicationUsageDto> Applications,
    IReadOnlyList<ProcessUsageDto> Processes,
    IReadOnlyList<InterfaceUsageDto> Interfaces,
    IReadOnlyList<UsageTimePointDto> Series,
    MonitoringStatusDto Status,
    string UsageDataSource,
    string StatusDataSource,
    bool IpcLive,
    bool SignalSubscribed,
    DateTimeOffset? LastSignalAtUtc,
    DateTimeOffset GeneratedAtUtc);

public partial class OverviewPageViewModel : ViewModelBase
{
    private static readonly IBrush LiveBrush = new SolidColorBrush(Color.Parse("#4EDEA3"));
    private static readonly IBrush WarningBrush = new SolidColorBrush(Color.Parse("#F5B44A"));
    private static readonly IBrush ErrorBrush = new SolidColorBrush(Color.Parse("#FF8A93"));

    [ObservableProperty] public partial string SourceBadge { get; set; } = "WAITING";
    [ObservableProperty] public partial string SourceDetail { get; set; } = "No data loaded yet.";
    [ObservableProperty] public partial bool IsFallback { get; set; }
    [ObservableProperty] public partial string WindowText { get; set; } = "—";
    [ObservableProperty] public partial string SyncText { get; set; } = "—";
    [ObservableProperty] public partial IBrush StatusBrush { get; set; } = WarningBrush;
    [ObservableProperty] public partial string StatusText { get; set; } = "Loading…";
    [ObservableProperty] public partial string StatusDetail { get; set; } = "";

    [ObservableProperty] public partial string TotalValue { get; set; } = "0";
    [ObservableProperty] public partial string TotalUnit { get; set; } = "B";
    [ObservableProperty] public partial string RxText { get; set; } = "0 B";
    [ObservableProperty] public partial string TxText { get; set; } = "0 B";
    [ObservableProperty] public partial double RxShare { get; set; }
    [ObservableProperty] public partial double TxShare { get; set; }
    [ObservableProperty] public partial string RxShareText { get; set; } = "RX 0%";
    [ObservableProperty] public partial string TxShareText { get; set; } = "TX 0%";

    [ObservableProperty] public partial string AttributedPercentText { get; set; } = "0.0%";
    [ObservableProperty] public partial string AttributedText { get; set; } = "0 B";
    [ObservableProperty] public partial string UnattributedText { get; set; } = "0 B";
    [ObservableProperty] public partial string AttributionCaption { get; set; } = "No traffic in range.";
    [ObservableProperty] public partial double AttributedShare { get; set; }
    [ObservableProperty] public partial double UnattributedShare { get; set; }

    [ObservableProperty] public partial string InterfaceCountText { get; set; } = "0";
    [ObservableProperty] public partial string InterfaceNames { get; set; } = "No interfaces observed in range.";
    [ObservableProperty] public partial string InterfaceCaption { get; set; } = "Provider mapping may be partial.";

    [ObservableProperty] public partial string PeakValue { get; set; } = "0";
    [ObservableProperty] public partial string PeakUnit { get; set; } = "B";
    [ObservableProperty] public partial string PeakDetail { get; set; } = "No 1-minute buckets.";
    [ObservableProperty] public partial string BucketCountText { get; set; } = "0 buckets";

    [ObservableProperty] public partial bool HasChartData { get; set; }
    [ObservableProperty] public partial string ChartCaption { get; set; } = "";
    [ObservableProperty] public partial bool HasNoApplications { get; set; } = true;
    [ObservableProperty] public partial bool HasNoInterfaces { get; set; } = true;
    [ObservableProperty] public partial bool HasNoBuckets { get; set; } = true;

    public ObservableCollection<ChartPoint> ChartPoints { get; } = new();
    public ObservableCollection<ApplicationRow> TopApplications { get; } = new();
    public ObservableCollection<InterfaceRow> Interfaces { get; } = new();
    public ObservableCollection<TimeSeriesRow> RecentBuckets { get; } = new();

    public void Apply(UsageSnapshot snapshot)
    {
        var s = snapshot.Summary;
        var totalRx = s.RxBytes + s.UnattributedRxBytes;
        var totalTx = s.TxBytes + s.UnattributedTxBytes;
        var total = totalRx + totalTx;

        var (totalValue, totalUnit) = ByteFormat.Split(total);
        TotalValue = totalValue;
        TotalUnit = totalUnit;
        RxText = ByteFormat.Bytes(totalRx);
        TxText = ByteFormat.Bytes(totalTx);
        RxShare = ByteFormat.Share(totalRx, total);
        TxShare = ByteFormat.Share(totalTx, total);
        RxShareText = $"RX {ByteFormat.Percent(totalRx, total)}";
        TxShareText = $"TX {ByteFormat.Percent(totalTx, total)}";

        var attributed = s.RxBytes + s.TxBytes;
        var unattributed = s.UnattributedRxBytes + s.UnattributedTxBytes;
        AttributedPercentText = ByteFormat.Percent(attributed, total);
        AttributedText = ByteFormat.Bytes(attributed);
        UnattributedText = ByteFormat.Bytes(unattributed);
        AttributedShare = ByteFormat.Share(attributed, total);
        UnattributedShare = ByteFormat.Share(unattributed, total);
        AttributionCaption = total == 0
            ? "No traffic persisted for this range yet."
            : $"{ByteFormat.Percent(attributed, total)} of bytes carry a process/application key; {ByteFormat.Percent(unattributed, total)} are unattributed.";

        InterfaceCountText = snapshot.Interfaces.Count.ToString(CultureInfo.InvariantCulture);
        InterfaceNames = snapshot.Interfaces.Count == 0
            ? "No interfaces observed in range."
            : string.Join("  ·  ", snapshot.Interfaces.Take(4).Select(i => i.InterfaceName ?? i.InterfaceKey));
        InterfaceCaption = snapshot.Interfaces.Count == 0
            ? "Interface breakdown appears when the provider records interface identity."
            : $"{snapshot.Interfaces.Count} interface key(s) observed. Mapping can be partial for TCP eBPF events.";

        var series = snapshot.Series;
        var peak = series.Count == 0 ? null : series.MaxBy(p => p.RxBytes + p.TxBytes);
        if (peak is null)
        {
            PeakValue = "0";
            PeakUnit = "B";
            PeakDetail = "No 1-minute buckets in range.";
        }
        else
        {
            var (pv, pu) = ByteFormat.Split(peak.RxBytes + peak.TxBytes);
            PeakValue = pv;
            PeakUnit = pu;
            PeakDetail = $"{peak.BucketStartUtc.ToLocalTime():MM-dd HH:mm} · bucket total";
        }
        BucketCountText = $"{series.Count} bucket{(series.Count == 1 ? "" : "s")}";

        ChartPoints.Clear();
        foreach (var p in series)
            ChartPoints.Add(new ChartPoint(p.BucketStartUtc, p.RxBytes, p.TxBytes));
        HasChartData = ChartPoints.Count > 0;
        ChartCaption = series.Count == 0
            ? "No persisted 1-minute buckets for this range."
            : $"{series.Count} real 1-minute buckets · {series[0].BucketStartUtc:u} – {series[^1].BucketStartUtc:u}";

        var grandTotal = Math.Max(1, total);
        TopApplications.Clear();
        foreach (var a in snapshot.Applications.Take(8))
            TopApplications.Add(ApplicationRow.From(a, grandTotal));
        HasNoApplications = TopApplications.Count == 0;

        Interfaces.Clear();
        foreach (var i in snapshot.Interfaces)
            Interfaces.Add(InterfaceRow.From(i, grandTotal));
        HasNoInterfaces = Interfaces.Count == 0;

        RecentBuckets.Clear();
        foreach (var p in series.TakeLast(8).Reverse())
            RecentBuckets.Add(TimeSeriesRow.From(p));
        HasNoBuckets = RecentBuckets.Count == 0;

        WindowText = $"{snapshot.StartUtc.ToLocalTime():yyyy-MM-dd HH:mm} → {snapshot.EndUtc.ToLocalTime():yyyy-MM-dd HH:mm} local";
        SyncText = snapshot.IpcLive
            ? (snapshot.LastSignalAtUtc is null ? "IPC LIVE (no signal yet)" : $"SIGNAL {snapshot.LastSignalAtUtc.Value.ToLocalTime():HH:mm:ss}")
            : "SQLITE SNAPSHOT";
        IsFallback = !snapshot.IpcLive;
        SourceBadge = snapshot.IpcLive ? "IPC LIVE MODE" : "SQLITE FALLBACK MODE";
        SourceDetail = snapshot.IpcLive
            ? $"Queried via daemon IPC ({snapshot.UsageDataSource}); live status from daemon."
            : $"Live daemon IPC unreachable. Reading persisted data via {snapshot.UsageDataSource}; status is reported unavailable rather than fabricated.";

        StatusText = snapshot.Status.State switch
        {
            MonitoringState.Monitoring => "Monitoring",
            MonitoringState.Degraded => "Degraded",
            MonitoringState.Paused => "Paused",
            MonitoringState.Initializing => "Initializing",
            _ => "Daemon unavailable",
        };
        StatusDetail = $"{snapshot.Status.ProviderName ?? "provider n/a"} · {snapshot.Status.HealthMessage ?? snapshot.Status.LastError ?? "no health detail"}";
        StatusBrush = snapshot.Status.State switch
        {
            MonitoringState.Monitoring => LiveBrush,
            MonitoringState.Degraded or MonitoringState.Paused or MonitoringState.Initializing => WarningBrush,
            _ => ErrorBrush,
        };
    }
}
