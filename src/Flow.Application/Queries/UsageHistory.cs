using Flow.Contracts.Usage;

namespace Flow.Application.Queries;

/// <summary>
/// Groups persisted usage buckets into local calendar days for a History view.
/// Buckets are supplied by a single-granularity query, so summing them never
/// double-counts. The offset is passed in rather than read from the machine so
/// the grouping is deterministic and testable.
/// </summary>
public static class UsageHistory
{
    public sealed record DailyRow(DateTimeOffset LocalDay, long RxBytes, long TxBytes, int Buckets);

    public static IReadOnlyList<DailyRow> GroupByLocalDay(IEnumerable<UsageTimePointDto> points, TimeSpan localOffset)
    {
        ArgumentNullException.ThrowIfNull(points);

        var rows = new Dictionary<DateTimeOffset, DailyRow>();
        foreach (var point in points)
        {
            var local = point.BucketStartUtc.ToOffset(localOffset);
            var day = new DateTimeOffset(local.Year, local.Month, local.Day, 0, 0, 0, localOffset);
            rows[day] = rows.TryGetValue(day, out var existing)
                ? existing with
                {
                    RxBytes = existing.RxBytes + point.RxBytes,
                    TxBytes = existing.TxBytes + point.TxBytes,
                    Buckets = existing.Buckets + 1,
                }
                : new DailyRow(day, point.RxBytes, point.TxBytes, 1);
        }

        return rows.Values.OrderBy(r => r.LocalDay).ToList();
    }
}
