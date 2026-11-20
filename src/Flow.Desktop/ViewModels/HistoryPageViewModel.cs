using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using Flow.Application.Queries;

namespace Flow.Desktop.ViewModels;

/// <summary>A local-calendar-day rollup of real persisted buckets.</summary>
public sealed record HistoryDailyRow(string Day, string Rx, string Tx, string Total, int Buckets);

public partial class HistoryPageViewModel : ViewModelBase
{
    [ObservableProperty] public partial string SummaryText { get; set; } = "No history loaded yet.";
    [ObservableProperty] public partial string ResolutionText { get; set; } = "resolution n/a";
    [ObservableProperty] public partial string WindowText { get; set; } = "—";
    [ObservableProperty] public partial bool HasDays { get; set; }
    [ObservableProperty] public partial bool HasBuckets { get; set; }

    public ObservableCollection<HistoryDailyRow> Days { get; } = new();
    public ObservableCollection<TimeSeriesRow> Buckets { get; } = new();

    public void Apply(UsageSnapshot snapshot, string granularity)
    {
        var localOffset = DateTimeOffset.Now.Offset;
        var days = UsageHistory.GroupByLocalDay(snapshot.Series, localOffset);

        Days.Clear();
        foreach (var day in days)
        {
            Days.Add(new HistoryDailyRow(
                day.LocalDay.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                ByteFormat.Bytes(day.RxBytes),
                ByteFormat.Bytes(day.TxBytes),
                ByteFormat.Bytes(day.RxBytes + day.TxBytes),
                day.Buckets));
        }
        HasDays = Days.Count > 0;

        Buckets.Clear();
        foreach (var point in snapshot.Series)
            Buckets.Add(TimeSeriesRow.From(point));
        HasBuckets = Buckets.Count > 0;

        var totalRx = snapshot.Series.Sum(p => p.RxBytes);
        var totalTx = snapshot.Series.Sum(p => p.TxBytes);
        SummaryText = snapshot.Series.Count == 0
            ? "No persisted buckets in this range."
            : $"{snapshot.Series.Count} real buckets · {ByteFormat.Bytes(totalRx)} RX / {ByteFormat.Bytes(totalTx)} TX across {days.Count} local day(s).";
        ResolutionText = $"query resolution: {granularity}";
        WindowText = $"{snapshot.StartUtc.ToLocalTime():yyyy-MM-dd HH:mm} → {snapshot.EndUtc.ToLocalTime():yyyy-MM-dd HH:mm} local";
    }
}
