using System;
using System.Globalization;

namespace Flow.Desktop.Controls;

/// <summary>
/// A single real persisted 1-minute bucket rendered by the telemetry chart.
/// </summary>
public sealed record ChartPoint(DateTimeOffset Timestamp, long RxBytes, long TxBytes)
{
    public long Total => RxBytes + TxBytes;
}
