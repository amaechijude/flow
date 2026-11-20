using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using Flow.Application.Queries;

namespace Flow.Desktop.ViewModels;

public sealed record ComparisonMetricRow(string Label, string Current, string Previous, string Delta, string Change);
public sealed record ComparisonRowView(
    string Title,
    string Key,
    string Current,
    string Previous,
    string Delta,
    string Change,
    bool IsDeltaAvailable,
    string Presence);

public partial class ComparisonPageViewModel : ViewModelBase
{
    [ObservableProperty] public partial string CurrentRangeText { get; set; } = "—";
    [ObservableProperty] public partial string PreviousRangeText { get; set; } = "—";
    [ObservableProperty] public partial string CurrentPrecisionText { get; set; } = "—";
    [ObservableProperty] public partial string PreviousPrecisionText { get; set; } = "—";
    [ObservableProperty] public partial string VerdictText { get; set; } = "No comparison loaded yet.";
    [ObservableProperty] public partial bool IsExact { get; set; }
    [ObservableProperty] public partial bool HasComparison { get; set; }
    [ObservableProperty] public partial string StatusText { get; set; } = "Compare a real window with the preceding one.";

    public ObservableCollection<ComparisonMetricRow> Metrics { get; } = new();
    public ObservableCollection<ComparisonRowView> Applications { get; } = new();
    public ObservableCollection<ComparisonRowView> Processes { get; } = new();
    public ObservableCollection<ComparisonRowView> Interfaces { get; } = new();

    public void Apply(UsageComparisonResult result, string modeLabel)
    {
        CurrentRangeText = FormatRange(result.Current);
        PreviousRangeText = FormatRange(result.Previous);
        CurrentPrecisionText = Precision(result.Current);
        PreviousPrecisionText = Precision(result.Previous);
        VerdictText = result.Current.Granularity == "1m"
            ? result.Verdict
            : result.Verdict + " Long ranges fall back to 1m until rollups exist; check the resolution shown on History before treating this as final.";
        IsExact = result.IsExactComparison;
        HasComparison = true;
        StatusText = $"{modeLabel}: current vs previous window, both from real persisted data.";

        Metrics.Clear();
        foreach (var metric in result.Metrics)
        {
            Metrics.Add(new ComparisonMetricRow(
                metric.Label,
                ByteFormat.Bytes(metric.Current),
                ByteFormat.Bytes(metric.Previous),
                Signed(metric.Delta),
                metric.ChangePercent is null
                    ? "n/a (previous 0)"
                    : $"{(metric.ChangePercent.Value >= 0 ? "+" : "")}{metric.ChangePercent.Value.ToString("0.0", CultureInfo.InvariantCulture)}%"));
        }

        Applications.Clear();
        foreach (var row in result.Applications)
            Applications.Add(ToView(row));
        Processes.Clear();
        foreach (var row in result.Processes)
            Processes.Add(ToView(row));
        Interfaces.Clear();
        foreach (var row in result.Interfaces)
            Interfaces.Add(ToView(row));
    }

    public void SetUnavailable(string reason)
    {
        HasComparison = false;
        StatusText = $"Comparison unavailable: {reason}. No values were substituted.";
        VerdictText = "No comparison available.";
        Metrics.Clear();
        Applications.Clear();
        Processes.Clear();
        Interfaces.Clear();
    }

    private static ComparisonRowView ToView(ComparisonRow row) => new(
        row.Title,
        row.Key,
        row.CurrentMissing ? "not present" : ByteFormat.Bytes(row.Current),
        row.PreviousMissing ? "not present" : ByteFormat.Bytes(row.Previous),
        row.CurrentMissing || row.PreviousMissing ? "n/a" : Signed(row.Delta),
        row.CurrentMissing || row.PreviousMissing || row.Previous == 0
            ? "n/a"
            : $"{(row.Delta >= 0 ? "+" : "")}{(100.0 * row.Delta / row.Previous).ToString("0.0", CultureInfo.InvariantCulture)}%",
        !row.CurrentMissing && !row.PreviousMissing,
        Presence(row));

    private static string Presence(ComparisonRow row)
        => row.CurrentMissing && row.PreviousMissing ? "n/a"
            : row.CurrentMissing ? "previous only"
            : row.PreviousMissing ? "current only"
            : "both";

    private static string Signed(long value)
        => (value >= 0 ? "+" : "-") + ByteFormat.Bytes(Math.Abs(value));

    private static string FormatRange(WindowPrecision precision)
        => $"{precision.RequestedStartUtc.ToLocalTime():yyyy-MM-dd HH:mm} → {precision.RequestedEndUtc.ToLocalTime():yyyy-MM-dd HH:mm} local";

    private static string Precision(WindowPrecision precision)
        => precision.IsBucketAligned
            ? $"exact {precision.Granularity} buckets"
            : $"approximate ({precision.Granularity} buckets; padding detailed in the verdict)";
}
