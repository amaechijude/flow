namespace Flow.Domain.Time;

public enum TimeRangeKind
{
    Live,
    LastHour,
    Rolling6Hours,
    Rolling12Hours,
    Rolling24Hours,
    Today,
    Yesterday,
    Rolling7Days,
    ThisWeek,
    LastWeek,
    Rolling30Days,
    ThisMonth,
    LastMonth,
    Rolling90Days,
    Last6Months,
    Last12Months,
    ThisYear,
    LastYear,
    Custom
}
