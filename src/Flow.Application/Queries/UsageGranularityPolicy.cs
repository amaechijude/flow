namespace Flow.Application.Queries;

/// <summary>
/// Chooses a single persisted granularity for a query range. Using one
/// granularity per query avoids double-counting overlapping 1m and rollup rows.
/// Coarse buckets are aligned to their period; a boundary bucket is included
/// whole, so long-range totals may include bytes slightly outside the exact
/// requested window.
/// </summary>
public static class UsageGranularityPolicy
{
    public static string Choose(DateTimeOffset startUtc, DateTimeOffset endUtc)
    {
        var duration = endUtc - startUtc;
        if (duration <= TimeSpan.FromHours(2)) return "1m";
        if (duration <= TimeSpan.FromDays(2)) return "5m";
        if (duration <= TimeSpan.FromDays(31)) return "1h";
        if (duration <= TimeSpan.FromDays(366)) return "1d";
        return "1mo";
    }

    public static DateTimeOffset AlignDown(DateTimeOffset valueUtc, string granularity)
    {
        var utc = valueUtc.ToUniversalTime();
        return granularity switch
        {
            "1m" => new DateTimeOffset(utc.Year, utc.Month, utc.Day, utc.Hour, utc.Minute, 0, TimeSpan.Zero),
            "5m" => new DateTimeOffset(utc.Year, utc.Month, utc.Day, utc.Hour, utc.Minute - (utc.Minute % 5), 0, TimeSpan.Zero),
            "1h" => new DateTimeOffset(utc.Year, utc.Month, utc.Day, utc.Hour, 0, 0, TimeSpan.Zero),
            "1d" => new DateTimeOffset(utc.Year, utc.Month, utc.Day, 0, 0, 0, TimeSpan.Zero),
            "1mo" => new DateTimeOffset(utc.Year, utc.Month, 1, 0, 0, 0, TimeSpan.Zero),
            _ => throw new ArgumentOutOfRangeException(nameof(granularity), granularity, "Unsupported usage granularity"),
        };
    }
}
