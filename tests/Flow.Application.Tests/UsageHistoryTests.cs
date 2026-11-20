using Flow.Application.Queries;
using Flow.Contracts.Usage;

namespace Flow.Application.Tests;

public sealed class UsageHistoryTests
{
    private static readonly TimeSpan PlusTwo = TimeSpan.FromHours(2);

    [Fact]
    public void GroupsUtcBucketsIntoLocalDaysUsingSuppliedOffset()
    {
        var points = new[]
        {
            // 2026-11-19 22:30Z and 23:30Z are both local 2026-11-20 (+02:00).
            new UsageTimePointDto(new DateTimeOffset(2026, 11, 19, 22, 30, 0, TimeSpan.Zero), 100, 10),
            new UsageTimePointDto(new DateTimeOffset(2026, 11, 19, 23, 30, 0, TimeSpan.Zero), 200, 20),
            // 2026-11-20 00:30Z is local 2026-11-20 02:30 (+02:00).
            new UsageTimePointDto(new DateTimeOffset(2026, 11, 20, 0, 30, 0, TimeSpan.Zero), 50, 5),
            // 2026-11-20 22:30Z is local 2026-11-21.
            new UsageTimePointDto(new DateTimeOffset(2026, 11, 20, 22, 30, 0, TimeSpan.Zero), 400, 40),
        };

        var days = UsageHistory.GroupByLocalDay(points, PlusTwo);

        Assert.Equal(2, days.Count);
        Assert.Equal(new DateTimeOffset(2026, 11, 20, 0, 0, 0, PlusTwo), days[0].LocalDay);
        Assert.Equal(350, days[0].RxBytes);
        Assert.Equal(35, days[0].TxBytes);
        Assert.Equal(3, days[0].Buckets);
        Assert.Equal(new DateTimeOffset(2026, 11, 21, 0, 0, 0, PlusTwo), days[1].LocalDay);
        Assert.Equal(400, days[1].RxBytes);
        Assert.Equal(1, days[1].Buckets);
    }

    [Fact]
    public void EmptySeriesProducesNoDays()
    {
        Assert.Empty(UsageHistory.GroupByLocalDay([], TimeSpan.Zero));
    }
}
