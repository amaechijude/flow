using System;
using System.Collections.Generic;
using Flow.Contracts.Monitoring;
using Flow.Contracts.Usage;
using Flow.Desktop.Controls;
using Flow.Desktop.ViewModels;
using Flow.Domain.Monitoring;
using Xunit;

namespace Flow.SystemTests;

/// <summary>
/// Regression coverage for the launch-time empty chart: the chart's Points
/// binding must see a new list on every snapshot, and the x axis must use the
/// selected range rather than only the extent of the loaded buckets.
/// </summary>
public class OverviewChartTests
{
    private static readonly DateTimeOffset RangeStart =
        new(2026, 11, 23, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset RangeEnd = RangeStart.AddDays(1);

    [Fact]
    public void ApplyAssignsFreshPointsAndTheSelectedWindow()
    {
        var vm = new OverviewPageViewModel();
        var initialPoints = vm.ChartPoints;
        var series = new List<UsageTimePointDto>
        {
            new(RangeStart.AddHours(9).AddMinutes(58), 100, 20),
            new(RangeStart.AddHours(9).AddMinutes(59), 200, 40),
            new(RangeStart.AddHours(10), 400, 80),
        };

        vm.Apply(Snapshot(series));

        Assert.NotNull(initialPoints);
        Assert.Empty(initialPoints);
        Assert.NotSame(initialPoints, vm.ChartPoints);
        Assert.Equal(3, vm.ChartPoints.Count);
        Assert.Equal(RangeStart.AddHours(9).AddMinutes(58), vm.ChartPoints[0].Timestamp);
        Assert.True(vm.HasChartData);
        Assert.Equal(RangeStart, vm.ChartWindowStartUtc);
        Assert.Equal(RangeEnd, vm.ChartWindowEndUtc);
        Assert.Equal("1m", vm.ChartResolutionText);
        Assert.Contains("window", vm.ChartCaption, StringComparison.OrdinalIgnoreCase);

        // A later refresh with different data must also produce a new instance.
        var populatedPoints = vm.ChartPoints;
        vm.Apply(Snapshot([]));
        Assert.NotSame(populatedPoints, vm.ChartPoints);
        Assert.Empty(vm.ChartPoints);
        Assert.False(vm.HasChartData);
    }

    [Fact]
    public void ChartResolutionComesFromTheLoadedBuckets()
    {
        Assert.Equal("5m", OverviewPageViewModel.ResolveChartResolution(
            [new(RangeStart.AddHours(1), 1, 1), new(RangeStart.AddHours(1).AddMinutes(5), 1, 1)],
            RangeStart, RangeEnd));
        Assert.Equal("1h", OverviewPageViewModel.ResolveChartResolution(
            [new(RangeStart.AddHours(1), 1, 1), new(RangeStart.AddHours(2), 1, 1)],
            RangeStart, RangeEnd));
        Assert.Equal("1m", OverviewPageViewModel.ResolveChartResolution([], RangeStart, RangeEnd.AddHours(-23)));
    }

    [Fact]
    public void WindowFractionReflectsSelectedRangeAndClampsBoundaryBuckets()
    {
        Assert.Equal(0, TimeSeriesChart.WindowFraction(RangeStart, RangeStart, RangeEnd));
        Assert.Equal(0.5, TimeSeriesChart.WindowFraction(RangeStart.AddHours(12), RangeStart, RangeEnd));
        Assert.Equal(1, TimeSeriesChart.WindowFraction(RangeEnd, RangeStart, RangeEnd));
        Assert.Equal(0, TimeSeriesChart.WindowFraction(RangeStart.AddHours(-1), RangeStart, RangeEnd));
        Assert.Equal(1, TimeSeriesChart.WindowFraction(RangeEnd.AddHours(1), RangeStart, RangeEnd));
    }

    private static UsageSnapshot Snapshot(IReadOnlyList<UsageTimePointDto> series) => new(
        RangeStart,
        RangeEnd,
        "Today",
        null,
        new UsageSummaryDto(RangeStart, RangeEnd, null, 0, 0, 0, 0),
        [],
        [],
        [],
        series,
        new MonitoringStatusDto(MonitoringState.Monitoring, RangeStart, "test", null, null, 0, 0),
        "test",
        "test",
        true,
        false,
        null,
        RangeStart);
}
