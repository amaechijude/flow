using Flow.Contracts.Usage;

namespace Flow.Application.Queries;

/// <summary>One real queried window used as a side of a comparison.</summary>
public sealed record UsageComparisonWindow(
    DateTimeOffset StartUtc,
    DateTimeOffset EndUtc,
    string Granularity,
    UsageSummaryDto Summary,
    IReadOnlyList<ApplicationUsageDto> Applications,
    IReadOnlyList<ProcessUsageDto> Processes,
    IReadOnlyList<InterfaceUsageDto> Interfaces);

public sealed record ComparisonMetric(string Label, long Current, long Previous, long Delta, double? ChangePercent);

public sealed record ComparisonRow(
    string Key,
    string? DisplayName,
    long Current,
    long Previous,
    long Delta,
    bool CurrentMissing,
    bool PreviousMissing)
{
    public string Title => string.IsNullOrWhiteSpace(DisplayName) ? Key : DisplayName!;
}

public sealed record UsageComparisonResult(
    WindowPrecision Current,
    WindowPrecision Previous,
    IReadOnlyList<ComparisonMetric> Metrics,
    IReadOnlyList<ComparisonRow> Applications,
    IReadOnlyList<ComparisonRow> Processes,
    IReadOnlyList<ComparisonRow> Interfaces,
    bool IsExactComparison,
    string Verdict);

/// <summary>
/// Compares two real queried windows. Missing rows are marked as missing rather
/// than converted to zero, and bucket-boundary padding is always reported.
/// </summary>
public static class UsageComparison
{
    public static UsageComparisonResult Compare(UsageComparisonWindow current, UsageComparisonWindow previous)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(previous);

        var currentPrecision = UsagePrecision.For(current.StartUtc, current.EndUtc, current.Granularity);
        var previousPrecision = UsagePrecision.For(previous.StartUtc, previous.EndUtc, previous.Granularity);
        var isExact = currentPrecision.IsBucketAligned && previousPrecision.IsBucketAligned;

        var verdict = isExact
            ? $"Both windows have exact {current.Granularity} bucket coverage. Provider attribution caveats still apply; this is not billing-grade."
            : $"Approximate comparison. Current: {currentPrecision.Verdict} Previous: {previousPrecision.Verdict} " +
              "Boundary buckets can pull bytes from outside the requested windows; not billing-grade.";

        return new UsageComparisonResult(
            currentPrecision,
            previousPrecision,
            Metrics(current.Summary, previous.Summary),
            CompareRows(
                current.Applications.Select(a => (a.ApplicationKey, (string?)a.DisplayName, Total(a.RxBytes, a.TxBytes, a.UnattributedRxBytes, a.UnattributedTxBytes))),
                previous.Applications.Select(a => (a.ApplicationKey, (string?)a.DisplayName, Total(a.RxBytes, a.TxBytes, a.UnattributedRxBytes, a.UnattributedTxBytes)))),
            CompareRows(
                current.Processes.Select(p => (p.ProcessStableKey, (string?)p.DisplayName, Total(p.RxBytes, p.TxBytes, p.UnattributedRxBytes, p.UnattributedTxBytes))),
                previous.Processes.Select(p => (p.ProcessStableKey, (string?)p.DisplayName, Total(p.RxBytes, p.TxBytes, p.UnattributedRxBytes, p.UnattributedTxBytes)))),
            CompareRows(
                current.Interfaces.Select(i => (i.InterfaceKey, (string?)i.InterfaceName, Total(i.RxBytes, i.TxBytes, i.UnattributedRxBytes, i.UnattributedTxBytes))),
                previous.Interfaces.Select(i => (i.InterfaceKey, (string?)i.InterfaceName, Total(i.RxBytes, i.TxBytes, i.UnattributedRxBytes, i.UnattributedTxBytes)))),
            isExact,
            verdict);
    }

    private static long Total(long rx, long tx, long unattrRx, long unattrTx) => rx + tx + unattrRx + unattrTx;

    private static IReadOnlyList<ComparisonMetric> Metrics(UsageSummaryDto current, UsageSummaryDto previous)
    {
        var values = new (string Label, long Current, long Previous)[]
        {
            ("Total bytes", Total(current.RxBytes, current.TxBytes, current.UnattributedRxBytes, current.UnattributedTxBytes),
                            Total(previous.RxBytes, previous.TxBytes, previous.UnattributedRxBytes, previous.UnattributedTxBytes)),
            ("Attributed RX", current.RxBytes, previous.RxBytes),
            ("Attributed TX", current.TxBytes, previous.TxBytes),
            ("Unattributed RX", current.UnattributedRxBytes, previous.UnattributedRxBytes),
            ("Unattributed TX", current.UnattributedTxBytes, previous.UnattributedTxBytes),
            ("Total RX", current.RxBytes + current.UnattributedRxBytes, previous.RxBytes + previous.UnattributedRxBytes),
            ("Total TX", current.TxBytes + current.UnattributedTxBytes, previous.TxBytes + previous.UnattributedTxBytes),
        };
        return values.Select(v => new ComparisonMetric(
            v.Label, v.Current, v.Previous, v.Current - v.Previous,
            v.Previous <= 0 ? null : 100.0 * (v.Current - v.Previous) / v.Previous)).ToList();
    }

    private static IReadOnlyList<ComparisonRow> CompareRows(
        IEnumerable<(string Key, string? DisplayName, long Total)> current,
        IEnumerable<(string Key, string? DisplayName, long Total)> previous)
    {
        var currentMap = current.GroupBy(x => x.Key).ToDictionary(g => g.Key, g => (DisplayName: g.First().DisplayName, Amount: g.Sum(x => x.Total)));
        var previousMap = previous.GroupBy(x => x.Key).ToDictionary(g => g.Key, g => (DisplayName: g.First().DisplayName, Amount: g.Sum(x => x.Total)));

        var rows = new List<ComparisonRow>();
        foreach (var key in currentMap.Keys.Concat(previousMap.Keys).Distinct(StringComparer.Ordinal))
        {
            var hasCurrent = currentMap.TryGetValue(key, out var c);
            var hasPrevious = previousMap.TryGetValue(key, out var p);
            var currentValue = hasCurrent ? c.Amount : 0;
            var previousValue = hasPrevious ? p.Amount : 0;
            rows.Add(new ComparisonRow(
                key,
                hasCurrent ? c.DisplayName : p.DisplayName,
                currentValue,
                previousValue,
                hasCurrent && hasPrevious ? currentValue - previousValue : 0,
                !hasCurrent,
                !hasPrevious));
        }
        return rows.OrderByDescending(r => Math.Abs(r.Delta)).ThenBy(r => r.Title, StringComparer.OrdinalIgnoreCase).ToList();
    }
}
