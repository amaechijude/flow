using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Flow.Desktop.ViewModels;

public partial class InterfacesPageViewModel : ViewModelBase
{
    private IReadOnlyList<InterfaceRow> _allInterfaces = Array.Empty<InterfaceRow>();

    [ObservableProperty] public partial string SearchText { get; set; } = "";
    [ObservableProperty] public partial string SortColumn { get; set; } = "total";
    [ObservableProperty] public partial bool SortDescending { get; set; } = true;
    [ObservableProperty] public partial InterfaceRow? SelectedInterface { get; set; }
    [ObservableProperty] public partial bool HasNoInterfaces { get; set; } = true;
    [ObservableProperty] public partial string SortSummary { get; set; } = "Sorted by total ↓ · 0 shown";
    [ObservableProperty] public partial string ScopeText { get; set; } = "No data loaded yet.";
    [ObservableProperty] public partial string TotalIngressText { get; set; } = "0 B";
    [ObservableProperty] public partial string TotalEgressText { get; set; } = "0 B";
    [ObservableProperty] public partial string SamplingText { get; set; } = "provider not yet known";
    [ObservableProperty] public partial string CaveatText { get; set; } =
        "Procfs meters interface-level counters and cannot attribute bytes to a process; eBPF TCP hooks attribute per process but do not currently record per-interface identity. Unattributed bytes are always shown separately.";

    public ObservableCollection<InterfaceRow> Interfaces { get; } = new();

    partial void OnSearchTextChanged(string value) => ApplyView();

    [RelayCommand]
    private void SortBy(string? column)
    {
        if (string.IsNullOrWhiteSpace(column)) return;
        if (string.Equals(SortColumn, column, StringComparison.Ordinal)) SortDescending = !SortDescending;
        else { SortColumn = column; SortDescending = true; }
        ApplyView();
    }

    public void Apply(UsageSnapshot snapshot)
    {
        var total = snapshot.Summary.RxBytes + snapshot.Summary.TxBytes + snapshot.Summary.UnattributedRxBytes + snapshot.Summary.UnattributedTxBytes;
        var grandTotal = Math.Max(1, total);
        var previous = SelectedInterface?.Key;

        _allInterfaces = snapshot.Interfaces.Select(i => InterfaceRow.From(i, grandTotal)).ToList();
        TotalIngressText = ByteFormat.Bytes(snapshot.Summary.RxBytes + snapshot.Summary.UnattributedRxBytes);
        TotalEgressText = ByteFormat.Bytes(snapshot.Summary.TxBytes + snapshot.Summary.UnattributedTxBytes);
        SamplingText = snapshot.Status.ProviderName ?? "provider unknown";
        ScopeText = $"{_allInterfaces.Count} interface key(s) in selected range · status provider {snapshot.Status.ProviderName ?? "n/a"}";

        ApplyView();
        SelectedInterface = previous is null ? Interfaces.FirstOrDefault() : Interfaces.FirstOrDefault(i => i.Key == previous) ?? Interfaces.FirstOrDefault();
    }

    private void ApplyView()
    {
        var rows = _allInterfaces.Where(i => RowFilter.Matches(i.SearchBlob, SearchText));
        var ordered = SortColumn switch
        {
            "name" => SortDescending ? rows.OrderByDescending(i => i.Title, StringComparer.OrdinalIgnoreCase) : rows.OrderBy(i => i.Title, StringComparer.OrdinalIgnoreCase),
            "rx" => SortDescending ? rows.OrderByDescending(i => i.RxBytes) : rows.OrderBy(i => i.RxBytes),
            "tx" => SortDescending ? rows.OrderByDescending(i => i.TxBytes) : rows.OrderBy(i => i.TxBytes),
            "unattributed" => SortDescending ? rows.OrderByDescending(i => i.UnattributedTotal) : rows.OrderBy(i => i.UnattributedTotal),
            _ => SortDescending ? rows.OrderByDescending(i => i.TotalBytes) : rows.OrderBy(i => i.TotalBytes),
        };
        Interfaces.Clear();
        foreach (var row in ordered) Interfaces.Add(row);
        HasNoInterfaces = Interfaces.Count == 0;
        SortSummary = $"Sorted by {SortColumn} {(SortDescending ? "↓" : "↑")} · {Interfaces.Count.ToString(CultureInfo.InvariantCulture)} shown";
    }
}
