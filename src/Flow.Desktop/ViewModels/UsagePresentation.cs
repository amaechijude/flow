using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Flow.Contracts.Usage;
using Flow.Desktop.Controls;

namespace Flow.Desktop.ViewModels;

public sealed record RangeOption(string Label, TimeSpan Duration, bool IsCustom = false);

public sealed record InterfaceFilterOption(string Label, string? InterfaceId);

/// <summary>
/// Local-time display formatting with an explicit weekday. Storage and export
/// remain UTC-labelled; only UI strings use this helper.
/// </summary>
public static class DisplayTime
{
    public static string Range(DateTimeOffset startUtc, DateTimeOffset endUtc)
        => $"{Full(startUtc)} → {Full(endUtc)} local";

    public static string Full(DateTimeOffset utc)
        => utc.ToLocalTime().ToString("yyyy-MM-dd ddd HH:mm", CultureInfo.InvariantCulture);

    public static string FullWithSeconds(DateTimeOffset utc)
        => utc.ToLocalTime().ToString("yyyy-MM-dd ddd HH:mm:ss", CultureInfo.InvariantCulture);

    public static string Day(DateTimeOffset local)
        => local.ToString("yyyy-MM-dd ddd", CultureInfo.InvariantCulture);

    public static string Bucket(DateTimeOffset utc)
        => utc.ToLocalTime().ToString("MM-dd ddd HH:mm", CultureInfo.InvariantCulture);

    public static string Axis(DateTimeOffset utc)
        => utc.ToLocalTime().ToString("ddd HH:mm", CultureInfo.InvariantCulture);
}

public sealed record TimeSeriesRow(string Bucket, string Rx, string Tx, string Total, long TotalBytes)
{
    public static TimeSeriesRow From(UsageTimePointDto dto) => new(
        DisplayTime.Bucket(dto.BucketStartUtc),
        ByteFormat.Bytes(dto.RxBytes),
        ByteFormat.Bytes(dto.TxBytes),
        ByteFormat.Bytes(dto.RxBytes + dto.TxBytes),
        dto.RxBytes + dto.TxBytes);
}

public static class ByteFormat
{
    private static readonly string[] Units = ["B", "KiB", "MiB", "GiB", "TiB", "PiB"];

    public static string Pair(long rx, long tx) => $"↓ {Bytes(rx)}  ↑ {Bytes(tx)}";

    public static string Bytes(long bytes)
    {
        var (value, unit) = Split(bytes);
        return $"{value} {unit}";
    }

    public static (string Value, string Unit) Split(long bytes)
    {
        double value = Math.Abs(bytes);
        var unit = 0;
        while (value >= 1024 && unit < Units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        var formatted = unit == 0
            ? bytes.ToString(CultureInfo.InvariantCulture)
            : (Math.Sign(bytes) * value).ToString(value >= 100 ? "0" : value >= 10 ? "0.0" : "0.00", CultureInfo.InvariantCulture);
        return (formatted, Units[unit]);
    }

    public static string Percent(long part, long total)
        => total <= 0 ? "0.0%" : (100.0 * part / total).ToString("0.0", CultureInfo.InvariantCulture) + "%";

    public static double Share(long part, long total)
        => total <= 0 ? 0 : Math.Clamp((double)part / total, 0, 1);
}

public sealed class ApplicationRow
{
    public required string Key { get; init; }
    public string? DisplayName { get; init; }
    public long RxBytes { get; init; }
    public long TxBytes { get; init; }
    public long UnattributedRxBytes { get; init; }
    public long UnattributedTxBytes { get; init; }
    public required long GrandTotal { get; init; }

    public string Title => string.IsNullOrWhiteSpace(DisplayName) ? Key : DisplayName!;
    public long AttributedTotal => RxBytes + TxBytes;
    public long UnattributedTotal => UnattributedRxBytes + UnattributedTxBytes;
    public long TotalBytes => AttributedTotal + UnattributedTotal;
    public string RxText => ByteFormat.Bytes(RxBytes);
    public string TxText => ByteFormat.Bytes(TxBytes);
    public string TotalText => ByteFormat.Bytes(TotalBytes);
    public string AttributedText => ByteFormat.Pair(RxBytes, TxBytes);
    public string UnattributedText => ByteFormat.Pair(UnattributedRxBytes, UnattributedTxBytes);
    public string ShareText => ByteFormat.Percent(TotalBytes, GrandTotal);
    public double Share => ByteFormat.Share(TotalBytes, GrandTotal);
    public bool HasDisplayName => !string.IsNullOrWhiteSpace(DisplayName) && !string.Equals(DisplayName, Key, StringComparison.Ordinal);
    public string SearchBlob => $"{Key} {DisplayName}";

    public static ApplicationRow From(ApplicationUsageDto dto, long grandTotal) => new()
    {
        Key = dto.ApplicationKey,
        DisplayName = dto.DisplayName,
        RxBytes = dto.RxBytes,
        TxBytes = dto.TxBytes,
        UnattributedRxBytes = dto.UnattributedRxBytes,
        UnattributedTxBytes = dto.UnattributedTxBytes,
        GrandTotal = grandTotal,
    };
}

public sealed class ProcessRow
{
    public required string Key { get; init; }
    public string? ApplicationKey { get; init; }
    public string? DisplayName { get; init; }
    public string? ExecutablePath { get; init; }
    public long RxBytes { get; init; }
    public long TxBytes { get; init; }
    public long UnattributedRxBytes { get; init; }
    public long UnattributedTxBytes { get; init; }
    public required long GrandTotal { get; init; }

    public string Title => !string.IsNullOrWhiteSpace(DisplayName) ? DisplayName! : !string.IsNullOrWhiteSpace(ApplicationKey) ? ApplicationKey! : Key;
    public string? Subtitle => string.IsNullOrWhiteSpace(ExecutablePath) ? ApplicationKey : ExecutablePath;
    public long AttributedTotal => RxBytes + TxBytes;
    public long UnattributedTotal => UnattributedRxBytes + UnattributedTxBytes;
    public long TotalBytes => AttributedTotal + UnattributedTotal;
    public string RxText => ByteFormat.Bytes(RxBytes);
    public string TxText => ByteFormat.Bytes(TxBytes);
    public string TotalText => ByteFormat.Bytes(TotalBytes);
    public string AttributedText => ByteFormat.Pair(RxBytes, TxBytes);
    public string UnattributedText => ByteFormat.Pair(UnattributedRxBytes, UnattributedTxBytes);
    public string ShareText => ByteFormat.Percent(TotalBytes, GrandTotal);
    public double Share => ByteFormat.Share(TotalBytes, GrandTotal);
    public string SearchBlob => $"{Key} {ApplicationKey} {DisplayName} {ExecutablePath}";

    public static ProcessRow From(ProcessUsageDto dto, long grandTotal) => new()
    {
        Key = dto.ProcessStableKey,
        ApplicationKey = dto.ApplicationKey,
        DisplayName = dto.DisplayName,
        ExecutablePath = dto.ExecutablePath,
        RxBytes = dto.RxBytes,
        TxBytes = dto.TxBytes,
        UnattributedRxBytes = dto.UnattributedRxBytes,
        UnattributedTxBytes = dto.UnattributedTxBytes,
        GrandTotal = grandTotal,
    };
}

public sealed class InterfaceRow
{
    public required string Key { get; init; }
    public string? Name { get; init; }
    public string? Type { get; init; }
    public long RxBytes { get; init; }
    public long TxBytes { get; init; }
    public long UnattributedRxBytes { get; init; }
    public long UnattributedTxBytes { get; init; }
    public required long GrandTotal { get; init; }

    public string Title => string.IsNullOrWhiteSpace(Name) ? Key : Name!;
    public string TypeText => string.IsNullOrWhiteSpace(Type) ? "type unknown" : Type!;
    public long AttributedTotal => RxBytes + TxBytes;
    public long UnattributedTotal => UnattributedRxBytes + UnattributedTxBytes;
    public long TotalBytes => AttributedTotal + UnattributedTotal;
    public string RxText => ByteFormat.Bytes(RxBytes);
    public string TxText => ByteFormat.Bytes(TxBytes);
    public string TotalText => ByteFormat.Bytes(TotalBytes);
    public string AttributedText => ByteFormat.Pair(RxBytes, TxBytes);
    public string UnattributedText => ByteFormat.Pair(UnattributedRxBytes, UnattributedTxBytes);
    public string ShareText => ByteFormat.Percent(TotalBytes, GrandTotal);
    public double Share => ByteFormat.Share(TotalBytes, GrandTotal);
    public string SearchBlob => $"{Key} {Name} {Type}";

    public static InterfaceRow From(InterfaceUsageDto dto, long grandTotal) => new()
    {
        Key = dto.InterfaceKey,
        Name = dto.InterfaceName,
        Type = dto.InterfaceType,
        RxBytes = dto.RxBytes,
        TxBytes = dto.TxBytes,
        UnattributedRxBytes = dto.UnattributedRxBytes,
        UnattributedTxBytes = dto.UnattributedTxBytes,
        GrandTotal = grandTotal,
    };
}

public enum SortDirection
{
    Descending = 0,
    Ascending = 1,
}

internal static class RowFilter
{
    public static bool Matches(string blob, string? search)
    {
        if (string.IsNullOrWhiteSpace(search)) return true;
        var terms = search.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return terms.All(t => blob.Contains(t, StringComparison.OrdinalIgnoreCase));
    }

    public static IEnumerable<T> Apply<T>(IEnumerable<T> rows, string? search, Func<T, string> blob, Func<T, IComparable> key, SortDirection direction)
        => direction == SortDirection.Descending
            ? rows.Where(r => Matches(blob(r), search)).OrderByDescending(key)
            : rows.Where(r => Matches(blob(r), search)).OrderBy(key);
}
