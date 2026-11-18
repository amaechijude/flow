using System.Globalization;
using Flow.Application.Monitoring;
using Flow.Domain.Identity;
using Flow.Domain.Measurement;

namespace Flow.Linux.Monitoring;

/// <summary>
/// Parses the tab-separated protocol emitted by <c>prototype/bpf/flow_tcp_bytes_loader</c>.
/// Fields are positional. The trailing identity fields (<c>start_ns</c>, <c>cgroup_id</c>)
/// were added with event-time identity, and <c>proto</c>/<c>ifname</c> with per-interface
/// TCP+UDP accounting; all are optional so older loaders keep working.
/// </summary>
internal static class EbpfSnapshotParser
{
    /// <summary>
    /// Minimum number of fields in an <c>E</c> line from the pre-event-time loader:
    /// E, tgid, uid, tx_total, rx_total, tx_delta, rx_delta, comm, exe, startticks, alive.
    /// </summary>
    internal const int MinimumEntryFieldCount = 11;

    /// <summary>Protocol values emitted by the loader's <c>proto</c> field.</summary>
    internal const int ProtoTcp = 1;
    internal const int ProtoUdp = 2;

    /// <summary>
    /// Parses an <c>M</c> metadata line: <c>M\tclk_tck\tboot_unix_ms</c>.
    /// Unparseable values keep safe defaults rather than failing the stream.
    /// </summary>
    internal static void ParseMeta(string line, out long clockTicksPerSecond, out long bootUnixMs)
    {
        clockTicksPerSecond = 100;
        bootUnixMs = 0;

        var p = line.Split('\t');
        if (p.Length >= 2)
            _ = long.TryParse(p[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out clockTicksPerSecond);
        if (p.Length >= 3)
            _ = long.TryParse(p[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out bootUnixMs);
        if (clockTicksPerSecond <= 0)
            clockTicksPerSecond = 100;
    }

    /// <summary>
    /// Parses one <c>E</c> line into a delta event.
    /// Returns null for malformed lines and snapshots without a positive delta.
    /// </summary>
    internal static NetworkUsageEvent? ParseEntry(
        string line,
        MeasurementPoint point,
        long clockTicksPerSecond,
        long bootUnixMs)
    {
        var p = line.Split('\t');
        if (p.Length < MinimumEntryFieldCount)
            return null;

        if (!int.TryParse(p[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var tgid))
            return null;
        if (!uint.TryParse(p[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var uid))
            return null;

        _ = long.TryParse(p[5], NumberStyles.Integer, CultureInfo.InvariantCulture, out var txDelta);
        _ = long.TryParse(p[6], NumberStyles.Integer, CultureInfo.InvariantCulture, out var rxDelta);
        if (txDelta <= 0 && rxDelta <= 0)
            return null;

        var comm = NormalizeComm(p[7]);
        var exePath = NormalizeExecutable(p[8]);

        _ = ulong.TryParse(p[9], NumberStyles.Integer, CultureInfo.InvariantCulture, out var startTicks);
        _ = int.TryParse(p[10], NumberStyles.Integer, CultureInfo.InvariantCulture, out var identityVerified);

        // Event-time start time from BPF (task_struct.start_time, ns since boot) is preferred.
        // It is PID-reuse-safe even when the process exits before /proc enrichment.
        ulong startNs = 0;
        if (p.Length > 11)
            _ = ulong.TryParse(p[11], NumberStyles.Integer, CultureInfo.InvariantCulture, out startNs);

        ulong cgroupId = 0;
        if (p.Length > 12)
            _ = ulong.TryParse(p[12], NumberStyles.Integer, CultureInfo.InvariantCulture, out cgroupId);

        // proto (1=TCP, 2=UDP) and the resolved interface name were added with
        // per-interface/UDP accounting. They are optional for older loaders.
        var proto = 0;
        if (p.Length > 13)
            _ = int.TryParse(p[13], NumberStyles.Integer, CultureInfo.InvariantCulture, out proto);

        var interfaceName = p.Length > 14 ? NormalizeInterface(p[14]) : null;

        // netns id is appended after proto/ifname. Interfaces in other network
        // namespaces are emitted as ifindex@netns because ifindex is scoped to
        // a namespace and cannot be named from the loader's namespace.
        ulong netnsId = 0;
        if (p.Length > 15)
            _ = ulong.TryParse(p[15], NumberStyles.Integer, CultureInfo.InvariantCulture, out netnsId);

        var startTimeUtc = DeriveStartTimeUtc(startNs, startTicks, clockTicksPerSecond, bootUnixMs);
        var proc = new ProcessIdentity(
            tgid,
            startTimeUtc,
            exePath,
            uid,
            CgroupId: cgroupId > 0 ? cgroupId.ToString(CultureInfo.InvariantCulture) : null);

        ApplicationIdentity? appHint = null;
        if (exePath is not null || comm is not null)
        {
            appHint = new ApplicationIdentity(
                ApplicationKey: exePath ?? $"comm:{comm}",
                DisplayName: comm ?? exePath ?? $"PID {tgid}",
                ExecutablePath: exePath);
        }

        return new NetworkUsageEvent(
            DateTimeOffset.UtcNow,
            proc,
            appHint,
            InterfaceName: interfaceName,
            RxBytesDelta: rxDelta > 0 ? rxDelta : 0,
            TxBytesDelta: txDelta > 0 ? txDelta : 0,
            MeasurementPoint: point,
            IsAttributed: true,
            AttributionNote: BuildAttributionNote(
                identityVerified != 0,
                startNs > 0,
                exePath is not null,
                comm is not null,
                proto,
                interfaceName,
                netnsId));
    }

    /// <summary>
    /// Parses one <c>U</c> line: <c>U\ttx_total\trx_total\ttx_delta\trx_delta\ttx_events\trx_events</c>.
    /// </summary>
    internal static NetworkUsageEvent? ParseUnattributed(string line, MeasurementPoint point)
    {
        var p = line.Split('\t');
        if (p.Length < 5)
            return null;

        _ = long.TryParse(p[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out var txDelta);
        _ = long.TryParse(p[4], NumberStyles.Integer, CultureInfo.InvariantCulture, out var rxDelta);
        if (txDelta <= 0 && rxDelta <= 0)
            return null;

        return new NetworkUsageEvent(
            DateTimeOffset.UtcNow,
            Process: null,
            ApplicationHint: null,
            InterfaceName: null,
            RxBytesDelta: rxDelta > 0 ? rxDelta : 0,
            TxBytesDelta: txDelta > 0 ? txDelta : 0,
            MeasurementPoint: point,
            IsAttributed: false,
            AttributionNote: "eBPF hook context without an attributable task (tgid==0) at observation time.");
    }

    /// <summary>
    /// Converts event-time identity to a UTC timestamp. Preference order:
    /// <c>start_ns</c> (ns since boot), then <c>startticks</c>/<c>clk_tck</c>, then the current time.
    /// The last fallback is intentionally documented as degraded identity.
    /// </summary>
    internal static DateTimeOffset DeriveStartTimeUtc(
        ulong startNs,
        ulong startTicks,
        long clockTicksPerSecond,
        long bootUnixMs)
    {
        try
        {
            if (bootUnixMs > 0)
            {
                if (startNs > 0)
                {
                    var ms = startNs / 1_000_000.0;
                    return DateTimeOffset.FromUnixTimeMilliseconds(bootUnixMs).AddMilliseconds(ms);
                }

                if (startTicks > 0 && clockTicksPerSecond > 0)
                {
                    var ms = (double)startTicks * 1000.0 / clockTicksPerSecond;
                    return DateTimeOffset.FromUnixTimeMilliseconds(bootUnixMs).AddMilliseconds(ms);
                }
            }
        }
        catch
        {
            // Fall through to the degraded fallback below.
        }

        return DateTimeOffset.UtcNow;
    }

    private static string? NormalizeComm(string value)
        => string.IsNullOrWhiteSpace(value) || value == "?" ? null : value;

    private static string? NormalizeExecutable(string value)
        => string.IsNullOrWhiteSpace(value) || value == "-" ? null : value;

    /// <summary>
    /// The loader writes "-" when the socket's interface could not be resolved.
    /// Such traffic is still attributed to the process; it is counted in the
    /// no-interface bucket rather than guessed onto an interface.
    /// </summary>
    private static string? NormalizeInterface(string value)
        => string.IsNullOrWhiteSpace(value) || value is "-" or "?" or "0" ? null : value;

    private static string? BuildAttributionNote(
        bool identityVerified,
        bool hasEventTimeStart,
        bool hasExecutable,
        bool hasComm,
        int proto,
        string? interfaceName,
        ulong netnsId)
    {
        var identityNote = BuildIdentityNote(identityVerified, hasEventTimeStart, hasExecutable, hasComm);
        var interfaceNote = interfaceName is null && proto is ProtoTcp or ProtoUdp
            ? "Socket interface could not be resolved at observation time (ifindex 0); bytes are counted without interface attribution."
            : netnsId > 0 && interfaceName?.Contains('@') == true
                ? "Interface belongs to a different network namespace and is reported as ifindex@netns; ifindex values are namespace-scoped."
                : null;

        return (identityNote, interfaceNote) switch
        {
            (null, null) => null,
            (null, _) => interfaceNote,
            (_, null) => identityNote,
            _ => $"{identityNote} {interfaceNote}",
        };
    }

    private static string? BuildIdentityNote(bool identityVerified, bool hasEventTimeStart, bool hasExecutable, bool hasComm)
    {
        if (identityVerified)
            return null;

        var detail = hasEventTimeStart
            ? "Identity not verified against the live process at snapshot time; event-time start time/cgroup/comm captured in the kernel are retained."
            : "Process was not live at snapshot enrichment time; cached identity may be incomplete (pre-event-time loader).";

        if (!hasExecutable)
            detail += hasComm
                ? " Executable path unavailable; application grouped by kernel-captured comm."
                : " Executable path and comm unavailable.";

        return detail;
    }
}
