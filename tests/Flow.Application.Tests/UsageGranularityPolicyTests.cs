using Flow.Application.Queries;

namespace Flow.Application.Tests;

public class UsageGranularityPolicyTests
{
    [Theory]
    [InlineData(0, "1m")]
    [InlineData(2, "1m")]
    [InlineData(3, "5m")]
    [InlineData(48, "5m")]
    [InlineData(49, "1h")]
    [InlineData(744, "1h")]
    [InlineData(745, "1d")]
    [InlineData(8784, "1d")]
    [InlineData(8785, "1mo")]
    public void ChoosesOneGranularityPerRange(int hours, string expected)
    {
        var start = new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero);
        Assert.Equal(expected, UsageGranularityPolicy.Choose(start, start.AddHours(hours)));
    }

    [Theory]
    [InlineData("1m", 2026, 11, 20, 10, 7, 10, 7)]
    [InlineData("5m", 2026, 11, 20, 10, 7, 10, 5)]
    [InlineData("1h", 2026, 11, 20, 10, 7, 10, 0)]
    [InlineData("1d", 2026, 11, 20, 10, 7, 0, 0)]
    [InlineData("1mo", 2026, 11, 20, 10, 7, 0, 0)]
    public void AlignsDown(string granularity, int year, int month, int day, int hour, int minute, int expectedHour, int expectedMinute)
    {
        var value = new DateTimeOffset(year, month, day, hour, minute, 0, TimeSpan.Zero);
        var aligned = UsageGranularityPolicy.AlignDown(value, granularity);

        Assert.Equal(expectedHour, aligned.Hour);
        Assert.Equal(expectedMinute, aligned.Minute);
        if (granularity == "1mo") Assert.Equal(1, aligned.Day);
    }
}
