namespace Flow.Domain.Time;

/// <summary>
/// Half-open interval [StartUtc, EndUtc) in UTC.
/// </summary>
public readonly record struct TimeWindow(DateTimeOffset StartUtc, DateTimeOffset EndUtc)
{
    public TimeSpan Duration => EndUtc - StartUtc;

    public static TimeWindow FromUtc(DateTimeOffset startUtc, DateTimeOffset endUtc)
    {
        if (endUtc < startUtc)
            throw new ArgumentException("End must be >= start.", nameof(endUtc));
        return new TimeWindow(startUtc, endUtc);
    }

    public bool Contains(DateTimeOffset instantUtc) => instantUtc >= StartUtc && instantUtc < EndUtc;
}
