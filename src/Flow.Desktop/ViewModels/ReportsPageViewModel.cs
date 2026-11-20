using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Flow.Desktop.ViewModels;

/// <summary>
/// A human-readable report assembled only from the current real snapshot.
/// It performs no measurement of its own and never substitutes estimates.
/// </summary>
public partial class ReportsPageViewModel(
    Action exportCsv,
    Action exportJson) : ViewModelBase
{
    [ObservableProperty] public partial string ReportTitle { get; set; } = "Usage report — no data";
    [ObservableProperty] public partial string GeneratedText { get; set; } = "—";
    [ObservableProperty] public partial string RangeText { get; set; } = "—";
    [ObservableProperty] public partial string ScopeText { get; set; } = "—";
    [ObservableProperty] public partial string SourceText { get; set; } = "—";
    [ObservableProperty] public partial string StateText { get; set; } = "—";
    [ObservableProperty] public partial string TotalText { get; set; } = "0 B";
    [ObservableProperty] public partial string RxText { get; set; } = "0 B";
    [ObservableProperty] public partial string TxText { get; set; } = "0 B";
    [ObservableProperty] public partial string AttributedText { get; set; } = "0 B";
    [ObservableProperty] public partial string UnattributedText { get; set; } = "0 B";
    [ObservableProperty] public partial string AttributedPercentText { get; set; } = "0.0%";
    [ObservableProperty] public partial string InterfaceCountText { get; set; } = "0";
    [ObservableProperty] public partial string CaveatsText { get; set; } = "No report loaded yet.";
    [ObservableProperty] public partial string ExportStatusText { get; set; } = "Use Export CSV / JSON in the toolbar to save this exact range.";
    [ObservableProperty] public partial bool HasApplications { get; set; }
    [ObservableProperty] public partial bool HasProcesses { get; set; }
    [ObservableProperty] public partial bool HasInterfaces { get; set; }

    public ObservableCollection<ApplicationRow> Applications { get; } = new();
    public ObservableCollection<ProcessRow> Processes { get; } = new();
    public ObservableCollection<InterfaceRow> Interfaces { get; } = new();

    [RelayCommand]
    private void ExportCsv() => exportCsv();

    [RelayCommand]
    private void ExportJson() => exportJson();

    public void Apply(UsageSnapshot snapshot)
    {
        var s = snapshot.Summary;
        var totalRx = s.RxBytes + s.UnattributedRxBytes;
        var totalTx = s.TxBytes + s.UnattributedTxBytes;
        var total = totalRx + totalTx;
        var attributed = s.RxBytes + s.TxBytes;
        var unattributed = s.UnattributedRxBytes + s.UnattributedTxBytes;

        ReportTitle = $"Usage report · {snapshot.RangeLabel}";
        GeneratedText = $"Generated {DisplayTime.FullWithSeconds(snapshot.GeneratedAtUtc)} local ({snapshot.GeneratedAtUtc:u})";
        RangeText = DisplayTime.Range(snapshot.StartUtc, snapshot.EndUtc) +
                    (snapshot.InterfaceId is null ? "" : $" · interface {snapshot.InterfaceId}");
        ScopeText = $"{snapshot.Applications.Count} application key(s) · {snapshot.Processes.Count} process key(s) · {snapshot.Series.Count} persisted bucket(s)";
        SourceText = snapshot.IpcLive
            ? $"Live daemon IPC ({snapshot.UsageDataSource}); status via {snapshot.StatusDataSource}."
            : $"SQLite fallback ({snapshot.UsageDataSource}); live daemon status unavailable, not fabricated.";
        StateText = snapshot.Status.State.ToString();

        TotalText = ByteFormat.Bytes(total);
        RxText = ByteFormat.Bytes(totalRx);
        TxText = ByteFormat.Bytes(totalTx);
        AttributedText = ByteFormat.Bytes(attributed);
        UnattributedText = ByteFormat.Bytes(unattributed);
        AttributedPercentText = ByteFormat.Percent(attributed, total);
        InterfaceCountText = snapshot.Interfaces.Count.ToString(CultureInfo.InvariantCulture);

        var grandTotal = Math.Max(1, total);
        Applications.Clear();
        foreach (var a in snapshot.Applications.Take(15))
            Applications.Add(ApplicationRow.From(a, grandTotal));
        HasApplications = Applications.Count > 0;

        Processes.Clear();
        foreach (var p in snapshot.Processes.Take(15))
            Processes.Add(ProcessRow.From(p, grandTotal));
        HasProcesses = Processes.Count > 0;

        Interfaces.Clear();
        foreach (var i in snapshot.Interfaces)
            Interfaces.Add(InterfaceRow.From(i, grandTotal));
        HasInterfaces = Interfaces.Count > 0;

        CaveatsText = total == 0
            ? "No traffic was persisted for this range. Zero means unmeasured here, not proof that no traffic occurred."
            : $"{ByteFormat.Percent(attributed, total)} of bytes carry a process/application key; {ByteFormat.Percent(unattributed, total)} are real unattributed traffic and must not be treated as zero. " +
              "Attribution depends on the active provider (ebpf: TCP+UDP, QUIC counted as UDP payload; procfs: interface-level totals only). " +
              "Process identity uses the thread-group-leader start time; systemd/LRU high-churn can still mis-attribute a small share of bytes. " +
              "Coarse rollups include whole boundary buckets, so long-range totals may include bytes slightly outside the exact window.";
    }
}
