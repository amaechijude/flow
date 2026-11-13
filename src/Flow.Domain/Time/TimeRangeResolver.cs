namespace Flow.Domain.Time;

/// <summary>
/// Resolves named time ranges into UTC half-open windows [start, end).
/// Calendar boundaries are computed in the provided local time zone (system TZ by default).
/// </summary>
public static class TimeRangeResolver
{
    public static TimeWindow Resolve(
        TimeRangeKind kind,
        DateTimeOffset nowUtc,
        TimeZoneInfo localZone,
        DateTimeOffset? customStartUtc = null,
        DateTimeOffset? customEndUtc = null,
        DayOfWeek weekStart = DayOfWeek.Monday)
    {
        var nowLocal = TimeZoneInfo.ConvertTime(nowUtc, localZone);

        return kind switch
        {
            TimeRangeKind.Live => TimeWindow.FromUtc(nowUtc.AddMinutes(-1), nowUtc),
            TimeRangeKind.LastHour => TimeWindow.FromUtc(nowUtc.AddHours(-1), nowUtc),
            TimeRangeKind.Rolling6Hours => TimeWindow.FromUtc(nowUtc.AddHours(-6), nowUtc),
            TimeRangeKind.Rolling12Hours => TimeWindow.FromUtc(nowUtc.AddHours(-12), nowUtc),
            TimeRangeKind.Rolling24Hours => TimeWindow.FromUtc(nowUtc.AddHours(-24), nowUtc),
            TimeRangeKind.Today => CalendarDay(nowLocal, localZone, 0),
            TimeRangeKind.Yesterday => CalendarDay(nowLocal, localZone, -1),
            TimeRangeKind.Rolling7Days => TimeWindow.FromUtc(nowUtc.AddDays(-7), nowUtc),
            TimeRangeKind.ThisWeek => CalendarWeek(nowLocal, localZone, weekStart, 0),
            TimeRangeKind.LastWeek => CalendarWeek(nowLocal, localZone, weekStart, -1),
            TimeRangeKind.Rolling30Days => TimeWindow.FromUtc(nowUtc.AddDays(-30), nowUtc),
            TimeRangeKind.ThisMonth => CalendarMonth(nowLocal, localZone, 0),
            TimeRangeKind.LastMonth => CalendarMonth(nowLocal, localZone, -1),
            TimeRangeKind.Rolling90Days => TimeWindow.FromUtc(nowUtc.AddDays(-90), nowUtc),
            TimeRangeKind.Last6Months => CalendarMonthsBack(nowLocal, localZone, 6),
            TimeRangeKind.Last12Months => CalendarMonthsBack(nowLocal, localZone, 12),
            TimeRangeKind.ThisYear => CalendarYear(nowLocal, localZone, 0),
            TimeRangeKind.LastYear => CalendarYear(nowLocal, localZone, -1),
            TimeRangeKind.Custom => ResolveCustom(customStartUtc, customEndUtc),
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unsupported time range kind.")
        };
    }

    private static TimeWindow ResolveCustom(DateTimeOffset? startUtc, DateTimeOffset? endUtc)
    {
        if (startUtc is null || endUtc is null)
            throw new ArgumentException("Custom range requires start and end.");
        return TimeWindow.FromUtc(startUtc.Value, endUtc.Value);
    }

    private static TimeWindow CalendarDay(DateTimeOffset nowLocal, TimeZoneInfo zone, int dayOffset)
    {
        var date = nowLocal.Date.AddDays(dayOffset);
        var startLocal = new DateTimeOffset(date, nowLocal.Offset);
        // Normalize offset for that local date in zone (DST-safe)
        startLocal = ToZone(date, zone);
        var endLocal = ToZone(date.AddDays(1), zone);
        return TimeWindow.FromUtc(startLocal.ToUniversalTime(), endLocal.ToUniversalTime());
    }

    private static TimeWindow CalendarWeek(DateTimeOffset nowLocal, TimeZoneInfo zone, DayOfWeek weekStart, int weekOffset)
    {
        var localDate = nowLocal.Date;
        var diff = ((7 + (localDate.DayOfWeek - weekStart)) % 7);
        var startDate = localDate.AddDays(-diff).AddDays(7 * weekOffset);
        var endDate = startDate.AddDays(7);
        return TimeWindow.FromUtc(ToZone(startDate, zone).ToUniversalTime(), ToZone(endDate, zone).ToUniversalTime());
    }

    private static TimeWindow CalendarMonth(DateTimeOffset nowLocal, TimeZoneInfo zone, int monthOffset)
    {
        var first = new DateTime(nowLocal.Year, nowLocal.Month, 1).AddMonths(monthOffset);
        var next = first.AddMonths(1);
        return TimeWindow.FromUtc(ToZone(first, zone).ToUniversalTime(), ToZone(next, zone).ToUniversalTime());
    }

    private static TimeWindow CalendarMonthsBack(DateTimeOffset nowLocal, TimeZoneInfo zone, int months)
    {
        // "Last N months" as N calendar months ending at start of current month? Or rolling from now?
        // Plan states "Last 6 months" / "Last 12 months" as calendar-like named periods; use start of month (N months ago) to start of current month.
        var endMonth = new DateTime(nowLocal.Year, nowLocal.Month, 1);
        var startMonth = endMonth.AddMonths(-months);
        return TimeWindow.FromUtc(ToZone(startMonth, zone).ToUniversalTime(), ToZone(endMonth, zone).ToUniversalTime());
    }

    private static TimeWindow CalendarYear(DateTimeOffset nowLocal, TimeZoneInfo zone, int yearOffset)
    {
        var start = new DateTime(nowLocal.Year + yearOffset, 1, 1);
        var end = start.AddYears(1);
        return TimeWindow.FromUtc(ToZone(start, zone).ToUniversalTime(), ToZone(end, zone).ToUniversalTime());
    }

    private static DateTimeOffset ToZone(DateTime localDateTimeUnspecified, TimeZoneInfo zone)
    {
        // Treat provided DateTime as local wall-clock date/time in target zone.
        var unspecified = DateTime.SpecifyKind(localDateTimeUnspecified, DateTimeKind.Unspecified);
        if (zone.IsInvalidTime(unspecified))
        {
            // Spring forward gap: advance by one hour (simple, deterministic handling)
            unspecified = unspecified.AddHours(1);
        }
        else if (zone.IsAmbiguousTime(unspecified))
        {
            // Fall back overlap: choose standard offset deterministically by picking earlier UTC instant
            var offsets = zone.GetAmbiguousTimeOffsets(unspecified);
            var chosen = offsets.OrderByDescending(o => o).First();
            return new DateTimeOffset(unspecified, chosen);
        }
        var offset = zone.GetUtcOffset(unspecified);
        return new DateTimeOffset(unspecified, offset);
    }
}
