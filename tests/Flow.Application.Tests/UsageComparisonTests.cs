using Flow.Application.Queries;
using Flow.Contracts.Usage;

namespace Flow.Application.Tests;

public sealed class UsageComparisonTests
{
    private static readonly DateTimeOffset AlignedStart = new(2026, 11, 20, 10, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset AlignedEnd = new(2026, 11, 20, 11, 0, 0, TimeSpan.Zero);

    [Fact]
    public void AlignedHourlyWindowHasExactBucketCoverage()
    {
        var precision = UsagePrecision.For(AlignedStart, AlignedEnd, "1h");
        Assert.True(precision.IsBucketAligned);
        Assert.Equal(TimeSpan.Zero, precision.PaddingBefore);
        Assert.Equal(TimeSpan.Zero, precision.PaddingAfter);
        Assert.Equal(AlignedEnd, precision.IncludedEndUtc);
        Assert.Contains("Exact bucket coverage", precision.Verdict);
    }

    [Fact]
    public void UnalignedCoarseWindowReportsPaddingInsteadOfClaimingExactness()
    {
        var start = AlignedStart.AddMinutes(10);
        var end = AlignedEnd.AddMinutes(10);
        var precision = UsagePrecision.For(start, end, "1h");
        Assert.False(precision.IsBucketAligned);
        Assert.Equal(TimeSpan.FromMinutes(10), precision.PaddingBefore);
        Assert.Equal(TimeSpan.FromMinutes(50), precision.PaddingAfter);
        Assert.Contains("Approximate", precision.Verdict);
    }

    [Fact]
    public void ComparesRealSummariesAndFlagsMissingRowsWithoutTreatingThemAsZero()
    {
        var current = Window(AlignedStart, AlignedEnd, 100, 10, 20, 2,
            [new ApplicationUsageDto("app.a", "A", 100, 10, 0, 0), new ApplicationUsageDto("app.only-now", "New", 5, 0, 0, 0)],
            [new ProcessUsageDto("p1", "app.a", "A", "/a", 100, 10, 0, 0)],
            [new InterfaceUsageDto("eth0", "eth0", "ethernet", 100, 10, 0, 0)]);
        var previous = Window(AlignedStart.AddHours(-1), AlignedStart, 50, 5, 10, 1,
            [new ApplicationUsageDto("app.a", "A", 50, 5, 0, 0), new ApplicationUsageDto("app.old", "Old", 7, 0, 0, 0)],
            [new ProcessUsageDto("p1", "app.a", "A", "/a", 50, 5, 0, 0)],
            [new InterfaceUsageDto("eth0", "eth0", "ethernet", 50, 5, 0, 0)]);

        var result = UsageComparison.Compare(current, previous);

        Assert.True(result.IsExactComparison);
        Assert.Equal(132, result.Metrics.Single(m => m.Label == "Total bytes").Current);
        Assert.Equal(66, result.Metrics.Single(m => m.Label == "Total bytes").Previous);
        Assert.Equal(66, result.Metrics.Single(m => m.Label == "Total bytes").Delta);
        Assert.Equal(10, result.Metrics.Single(m => m.Label == "Unattributed RX").Delta);

        var onlyNow = result.Applications.Single(r => r.Key == "app.only-now");
        Assert.True(onlyNow.CurrentMissing == false);
        Assert.True(onlyNow.PreviousMissing);
        Assert.Equal(0, onlyNow.Delta); // no fabricated delta for a missing side
        var old = result.Applications.Single(r => r.Key == "app.old");
        Assert.True(old.CurrentMissing);
        Assert.True(old.PreviousMissing == false);
    }

    [Fact]
    public void ApproximateWindowsAreMarkedApproximateInTheVerdict()
    {
        var current = Window(AlignedStart.AddMinutes(10), AlignedEnd.AddMinutes(10), 1, 1, 1, 1, [], [], []);
        var previous = Window(AlignedStart.AddHours(-1).AddMinutes(10), AlignedStart.AddMinutes(10), 1, 1, 1, 1, [], [], []);

        var result = UsageComparison.Compare(current, previous);

        Assert.False(result.IsExactComparison);
        Assert.Contains("Approximate", result.Verdict);
        Assert.False(result.Current.IsBucketAligned);
        Assert.False(result.Previous.IsBucketAligned);
    }

    private static UsageComparisonWindow Window(
        DateTimeOffset start,
        DateTimeOffset end,
        long rx,
        long tx,
        long unattrRx,
        long unattrTx,
        IReadOnlyList<ApplicationUsageDto> apps,
        IReadOnlyList<ProcessUsageDto> processes,
        IReadOnlyList<InterfaceUsageDto> interfaces)
        => new(start, end, "1h", new UsageSummaryDto(start, end, null, rx, tx, unattrRx, unattrTx), apps, processes, interfaces);
}
