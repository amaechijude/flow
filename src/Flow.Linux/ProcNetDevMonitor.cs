using System.Globalization;
using System.Runtime.CompilerServices;
using Flow.Application.Monitoring;
using Flow.Domain.Measurement;

namespace Flow.Linux.Monitoring;

/// <summary>
/// Interface-level monitor based on /proc/net/dev deltas.
/// Emits unattributed totals per interface (not per-process attribution).
/// </summary>
public sealed class ProcNetDevMonitor : INetworkMonitor
{
    public static readonly MeasurementPoint Point = new(
        "procfs",
        "/proc/net/dev",
        "Interface RX/TX byte counter deltas sampled from /proc/net/dev (unattributed, interface-level).");

    private readonly TimeSpan _interval;

    public ProcNetDevMonitor(TimeSpan? interval = null)
    {
        _interval = interval ?? TimeSpan.FromSeconds(2);
    }

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public async IAsyncEnumerable<NetworkUsageEvent> ReadEventsAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var previous = ReadCounters();
        while (!cancellationToken.IsCancellationRequested)
        {
            await Task.Delay(_interval, cancellationToken).ConfigureAwait(false);
            var now = DateTimeOffset.UtcNow;
            var current = ReadCounters();
            foreach (var (iface, counters) in current)
            {
                if (!previous.TryGetValue(iface, out var prev)) continue;
                var rxDelta = counters.RxBytes >= prev.RxBytes ? (long)(counters.RxBytes - prev.RxBytes) : 0;
                var txDelta = counters.TxBytes >= prev.TxBytes ? (long)(counters.TxBytes - prev.TxBytes) : 0;
                if (rxDelta == 0 && txDelta == 0) continue;
                yield return new NetworkUsageEvent(
                    now,
                    Process: null,
                    ApplicationHint: null,
                    InterfaceName: iface,
                    RxBytesDelta: rxDelta,
                    TxBytesDelta: txDelta,
                    MeasurementPoint: Point,
                    IsAttributed: false,
                    AttributionNote: "Interface-level counter delta; process/application attribution unavailable.");
            }
            previous = current;
        }
    }

    private static Dictionary<string, (ulong RxBytes, ulong TxBytes)> ReadCounters()
    {
        var dict = new Dictionary<string, (ulong, ulong)>(StringComparer.Ordinal);
        const string path = "/proc/net/dev";
        if (!File.Exists(path)) return dict;
        foreach (var line in File.ReadLines(path).Skip(2))
        {
            var idx = line.IndexOf(':');
            if (idx < 0) continue;
            var name = line[..idx].Trim();
            var rest = line[(idx + 1)..];
            var parts = rest.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 16) continue;
            if (!ulong.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var rx)) continue;
            if (!ulong.TryParse(parts[8], NumberStyles.Integer, CultureInfo.InvariantCulture, out var tx)) continue;
            dict[name] = (rx, tx);
        }
        return dict;
    }
}
