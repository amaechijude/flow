using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Flow.Desktop.ViewModels;

public partial class ApplicationsPageViewModel : ViewModelBase
{
    private IReadOnlyList<ApplicationRow> _allApplications = Array.Empty<ApplicationRow>();
    private IReadOnlyList<ProcessRow> _allProcesses = Array.Empty<ProcessRow>();

    [ObservableProperty] public partial string AppSearchText { get; set; } = "";
    [ObservableProperty] public partial string ProcessSearchText { get; set; } = "";
    [ObservableProperty] public partial string FilterMode { get; set; } = "all";
    [ObservableProperty] public partial string AppSortColumn { get; set; } = "total";
    [ObservableProperty] public partial bool AppSortDescending { get; set; } = true;
    [ObservableProperty] public partial string ProcessSortColumn { get; set; } = "total";
    [ObservableProperty] public partial bool ProcessSortDescending { get; set; } = true;
    [ObservableProperty] public partial ApplicationRow? SelectedApplication { get; set; }
    [ObservableProperty] public partial ProcessRow? SelectedProcess { get; set; }
    [ObservableProperty] public partial bool HasNoApplications { get; set; } = true;
    [ObservableProperty] public partial bool HasNoProcesses { get; set; } = true;
    [ObservableProperty] public partial string AppSortSummary { get; set; } = "Sorted by total ↓";
    [ObservableProperty] public partial string ProcessSortSummary { get; set; } = "Sorted by total ↓";
    [ObservableProperty] public partial string AllCountText { get; set; } = "0";
    [ObservableProperty] public partial string AttributedCountText { get; set; } = "0";
    [ObservableProperty] public partial string UnattributedCountText { get; set; } = "0";
    [ObservableProperty] public partial string ScopeText { get; set; } = "No data loaded yet.";
    [ObservableProperty] public partial bool IsAllFilter { get; set; } = true;
    [ObservableProperty] public partial bool IsAttributedFilter { get; set; }
    [ObservableProperty] public partial bool IsUnattributedFilter { get; set; }

    public ObservableCollection<ApplicationRow> Applications { get; } = new();
    public ObservableCollection<ProcessRow> Processes { get; } = new();

    partial void OnAppSearchTextChanged(string value) => ApplyApplications();

    partial void OnProcessSearchTextChanged(string value) => ApplyProcesses();

    [RelayCommand]
    private void SetFilter(string? mode)
    {
        FilterMode = string.IsNullOrWhiteSpace(mode) ? "all" : mode;
        IsAllFilter = FilterMode == "all";
        IsAttributedFilter = FilterMode == "attributed";
        IsUnattributedFilter = FilterMode == "unattributed";
        ApplyApplications();
        ApplyProcesses();
    }

    [RelayCommand]
    private void SortApplications(string? column)
    {
        if (string.IsNullOrWhiteSpace(column)) return;
        if (string.Equals(AppSortColumn, column, StringComparison.Ordinal)) AppSortDescending = !AppSortDescending;
        else { AppSortColumn = column; AppSortDescending = true; }
        ApplyApplications();
    }

    [RelayCommand]
    private void SortProcesses(string? column)
    {
        if (string.IsNullOrWhiteSpace(column)) return;
        if (string.Equals(ProcessSortColumn, column, StringComparison.Ordinal)) ProcessSortDescending = !ProcessSortDescending;
        else { ProcessSortColumn = column; ProcessSortDescending = true; }
        ApplyProcesses();
    }

    public void Apply(UsageSnapshot snapshot)
    {
        var total = snapshot.Summary.RxBytes + snapshot.Summary.TxBytes + snapshot.Summary.UnattributedRxBytes + snapshot.Summary.UnattributedTxBytes;
        var grandTotal = Math.Max(1, total);

        var previousApp = SelectedApplication?.Key;
        var previousProcess = SelectedProcess?.Key;

        _allApplications = snapshot.Applications.Select(a => ApplicationRow.From(a, grandTotal)).ToList();
        _allProcesses = snapshot.Processes.Select(p => ProcessRow.From(p, grandTotal)).ToList();

        AllCountText = _allApplications.Count.ToString(CultureInfo.InvariantCulture);
        AttributedCountText = _allApplications.Count(a => a.AttributedTotal > 0).ToString(CultureInfo.InvariantCulture);
        UnattributedCountText = _allApplications.Count(a => a.UnattributedTotal > 0).ToString(CultureInfo.InvariantCulture);
        ScopeText = $"{snapshot.Applications.Count} application keys · {snapshot.Processes.Count} process keys (queries capped at the largest requested page size).";

        ApplyApplications();
        ApplyProcesses();

        SelectedApplication = previousApp is null ? null : Applications.FirstOrDefault(a => a.Key == previousApp) ?? Applications.FirstOrDefault();
        SelectedProcess = previousProcess is null ? null : Processes.FirstOrDefault(p => p.Key == previousProcess) ?? Processes.FirstOrDefault();
    }

    private void ApplyApplications()
    {
        var filtered = Filter(_allApplications, a => a.AttributedTotal, a => a.UnattributedTotal, a => a.SearchBlob, AppSearchText);
        var sorted = Sort(filtered, AppSortColumn, AppSortDescending,
            name: a => a.Title,
            rx: a => a.RxBytes,
            tx: a => a.TxBytes,
            unattributed: a => a.UnattributedTotal,
            total: a => a.TotalBytes);
        Replace(Applications, sorted);
        HasNoApplications = Applications.Count == 0;
        AppSortSummary = $"Sorted by {AppSortColumn} {(AppSortDescending ? "↓" : "↑")} · {Applications.Count} shown";
    }

    private void ApplyProcesses()
    {
        var filtered = Filter(_allProcesses, p => p.AttributedTotal, p => p.UnattributedTotal, p => p.SearchBlob, ProcessSearchText);
        var sorted = Sort(filtered, ProcessSortColumn, ProcessSortDescending,
            name: p => p.Title,
            rx: p => p.RxBytes,
            tx: p => p.TxBytes,
            unattributed: p => p.UnattributedTotal,
            total: p => p.TotalBytes);
        Replace(Processes, sorted);
        HasNoProcesses = Processes.Count == 0;
        ProcessSortSummary = $"Sorted by {ProcessSortColumn} {(ProcessSortDescending ? "↓" : "↑")} · {Processes.Count} shown";
    }

    private IEnumerable<T> Filter<T>(IEnumerable<T> rows, Func<T, long> attributed, Func<T, long> unattributed, Func<T, string> blob, string? search)
    {
        var mode = FilterMode;
        var filtered = mode switch
        {
            "attributed" => rows.Where(r => attributed(r) > 0),
            "unattributed" => rows.Where(r => unattributed(r) > 0),
            _ => rows,
        };
        return filtered.Where(r => RowFilter.Matches(blob(r), search));
    }

    private static IEnumerable<T> Sort<T>(IEnumerable<T> rows, string column, bool descending,
        Func<T, string> name, Func<T, long> rx, Func<T, long> tx, Func<T, long> unattributed, Func<T, long> total)
    {
        IOrderedEnumerable<T> ordered = column switch
        {
            "name" => descending ? rows.OrderByDescending(name, StringComparer.OrdinalIgnoreCase) : rows.OrderBy(name, StringComparer.OrdinalIgnoreCase),
            "rx" => descending ? rows.OrderByDescending(rx) : rows.OrderBy(rx),
            "tx" => descending ? rows.OrderByDescending(tx) : rows.OrderBy(tx),
            "unattributed" => descending ? rows.OrderByDescending(unattributed) : rows.OrderBy(unattributed),
            _ => descending ? rows.OrderByDescending(total) : rows.OrderBy(total),
        };
        return ordered.ThenBy(name, StringComparer.OrdinalIgnoreCase);
    }

    private static void Replace<T>(ObservableCollection<T> target, IEnumerable<T> items)
    {
        target.Clear();
        foreach (var item in items) target.Add(item);
    }
}
