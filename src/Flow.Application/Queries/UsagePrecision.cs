namespace Flow.Application.Queries;

/// <summary>
/// Describes which whole buckets a single-granularity usage query actually
/// included for a requested half-open UTC window. Padding is reported rather
/// than hidden: coarse rollup boundaries can pull bytes from outside the exact
/// requested window.
/// </summary>
public sealed record WindowPrecision(
    DateTimeOffset RequestedStartUtc,
    DateTimeOffset RequestedEndUtc,
    string Granularity,
    DateTimeOffset IncludedStartUtc,
    DateTimeOffset IncludedEndUtc,
    TimeSpan PaddingBefore,
    TimeSpan PaddingAfter)
{
    public TimeSpan RequestedDuration => RequestedEndUtc - RequestedStartUtc;
    public TimeSpan IncludedDuration => IncludedEndUtc - IncludedStartUtc;

    /// <summary>True only when the requested window coincides with whole buckets.</summary>
    public bool IsBucketAligned => PaddingBefore == TimeSpan.Zero && PaddingAfter == TimeSpan.Zero;

    public string Verdict => IsBucketAligned
        ? $"Exact bucket coverage ({Granularity}); provider caveats still apply, not billing-grade."
        : $"Approximate: includes {FormatPadding(PaddingBefore)} before and {FormatPadding(PaddingAfter)} after the requested window.";

    private static string FormatPadding(TimeSpan value)
    {
        if (value == TimeSpan.Zero) return "0s";
        if (value.TotalDays >= 1) return $"{value.TotalDays:0.#}d";
        if (value.TotalHours >= 1) return $"{value.TotalHours:0.#}h";
        if (value.TotalMinutes >= 1) return $"{value.TotalMinutes:0.#}m";
        return $"{value.TotalSeconds:0.#}s";
    }
}

public static class UsagePrecision
{
    /// <summary>
    /// Mirrors the repository semantics: buckets are floored at the start,
    /// included while their start is strictly before the end, and a coarse
    /// bucket is included whole.
    /// </summary>
    public static WindowPrecision For(DateTimeOffset startUtc, DateTimeOffset endUtc, string granularity)
    {
        var start = startUtc.ToUniversalTime();
        var end = endUtc.ToUniversalTime();
        if (end < start) (start, end) = (end, start);

        var includedStart = UsageGranularityPolicy.AlignDown(start, granularity);
        var endFloor = UsageGranularityPolicy.AlignDown(end, granularity);
        // The query includes buckets whose start is strictly before 'end'. If
        // 'end' is itself a bucket boundary, that bucket is excluded and the
        // included range ends exactly at 'end'.
        var includedEnd = endFloor == end
            ? end
            : endFloor + BucketDuration(endFloor, granularity);

        return new WindowPrecision(
            start,
            end,
            granularity,
            includedStart,
            includedEnd,
            start - includedStart,
            includedEnd - end);
    }

    public static TimeSpan BucketDuration(DateTimeOffset bucketStartUtc, string granularity)
    {
        var utc = bucketStartUtc.ToUniversalTime();
        return granularity switch
        {
            "1m" => TimeSpan.FromMinutes(1),
            "5m" => TimeSpan.FromMinutes(5),
            "1h" => TimeSpan.FromHours(1),
            "1d" => TimeSpan.FromDays(1),
            "1mo" => TimeSpan.FromDays(DateTime.DaysInMonth(utc.Year, utc.Month)),
            _ => throw new ArgumentOutOfRangeException(nameof(granularity), granularity, "Unsupported usage granularity"),
        };
    }
}
