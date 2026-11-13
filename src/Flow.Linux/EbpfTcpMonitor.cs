using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using Flow.Application.Monitoring;
using Flow.Domain.Identity;
using Flow.Domain.Measurement;

namespace Flow.Linux.Monitoring;

/// <summary>
/// Per-process TCP byte attribution provider backed by the validated eBPF prototype loader
/// (fexit/tcp_sendmsg, kretprobe/tcp_recvmsg). Requires sufficient privileges to load/attach BPF.
/// Emits delta events derived from periodic snapshots produced by the loader in TSV mode.
/// </summary>
public sealed class EbpfTcpMonitor : INetworkMonitor, IDisposable
{
    public static readonly MeasurementPoint Point = new(
        "ebpf-tcp",
        "fexit/tcp_sendmsg+kretprobe/tcp_recvmsg",
        "TCP bytes from positive return values of tcp_sendmsg/tcp_recvmsg observed via eBPF (TGID/UID attribution, cumulative snapshots converted to deltas). UDP/non-TCP paths not counted.");

    private readonly EbpfTcpMonitorOptions _options;
    private Process? _process;
    private CancellationTokenSource? _cts;

    public EbpfTcpMonitor(EbpfTcpMonitorOptions? options = null)
    {
        _options = options ?? new EbpfTcpMonitorOptions();
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        // Process is started lazily in ReadEventsAsync to allow streaming stdout.
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        Dispose();
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        try { _cts?.Cancel(); } catch { }
        try
        {
            if (_process is { HasExited: false })
                _process.Kill(entireProcessTree: true);
        }
        catch { }
        try { _process?.Dispose(); } catch { }
        try { _cts?.Dispose(); } catch { }
        _process = null;
        _cts = null;
    }

    public async IAsyncEnumerable<NetworkUsageEvent> ReadEventsAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var ct = _cts.Token;

        var loaderPath = ResolveLoaderPath(_options.LoaderPath);
        if (loaderPath is null)
            throw new InvalidOperationException(
                "eBPF TCP loader not found. Set Flow:Ebpf:LoaderPath or build prototype/bpf/flow_tcp_bytes_loader. " +
                "This provider requires a privileged loader built from the validated prototype.");

        var psi = new ProcessStartInfo
        {
            FileName = loaderPath,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add(_options.IntervalSeconds.ToString(CultureInfo.InvariantCulture));
        psi.ArgumentList.Add(_options.Rounds.ToString(CultureInfo.InvariantCulture));
        psi.ArgumentList.Add("tsv");

        _process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        if (!_process.Start())
            throw new InvalidOperationException($"Failed to start eBPF loader process: {loaderPath}");

        // Drain stderr to avoid blocking; could be surfaced as diagnostics in future.
        _ = Task.Run(async () =>
        {
            try { while (await _process.StandardError.ReadLineAsync(ct).ConfigureAwait(false) is not null) { } } catch { }
        }, ct);

        long clkTck = 100;
        long bootUnixMs = 0;
        var stdout = _process.StandardOutput;

        while (!ct.IsCancellationRequested)
        {
            string? line;
            try { line = await stdout.ReadLineAsync(ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { yield break; }
            catch { yield break; }

            if (line is null)
                yield break;

            if (line.Length == 0) continue;
            if (line.StartsWith("M\t", StringComparison.Ordinal))
            {
                var p = line.Split('\t');
                if (p.Length >= 3)
                {
                    _ = long.TryParse(p[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out clkTck);
                    _ = long.TryParse(p[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out bootUnixMs);
                    if (clkTck <= 0) clkTck = 100;
                }
                continue;
            }

            if (line.StartsWith("E\t", StringComparison.Ordinal))
            {
                var ev = ParseEntry(line, clkTck, bootUnixMs);
                if (ev is not null) yield return ev;
                continue;
            }

            if (line.StartsWith("U\t", StringComparison.Ordinal))
            {
                var ev = ParseUnattributed(line);
                if (ev is not null) yield return ev;
                continue;
            }

            // Ignore S and any other lines
        }
    }

    private static NetworkUsageEvent? ParseEntry(string line, long clkTck, long bootUnixMs)
    {
        // E\ttgid\tuid\ttx_total\trx_total\ttx_delta\trx_delta\tcomm\texe\tstartticks\talive
        var p = line.Split('\t');
        if (p.Length < 11) return null;
        if (!int.TryParse(p[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var tgid)) return null;
        if (!uint.TryParse(p[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var uid)) return null;
        _ = long.TryParse(p[5], NumberStyles.Integer, CultureInfo.InvariantCulture, out var txDelta);
        _ = long.TryParse(p[6], NumberStyles.Integer, CultureInfo.InvariantCulture, out var rxDelta);
        if (txDelta <= 0 && rxDelta <= 0) return null;

        var comm = p[7];
        var exe = p[8];
        _ = ulong.TryParse(p[9], NumberStyles.Integer, CultureInfo.InvariantCulture, out var startTicks);
        _ = int.TryParse(p[10], NumberStyles.Integer, CultureInfo.InvariantCulture, out var alive);

        var startTimeUtc = DeriveStartTimeUtc(startTicks, clkTck, bootUnixMs);
        var exePath = string.IsNullOrWhiteSpace(exe) || exe == "-" ? null : exe;
        var proc = new ProcessIdentity(tgid, startTimeUtc, exePath, uid);

        string? appKey = exePath;
        var appHint = string.IsNullOrWhiteSpace(appKey) && string.IsNullOrWhiteSpace(comm)
            ? null
            : new ApplicationIdentity(
                ApplicationKey: appKey ?? $"comm:{comm}",
                DisplayName: !string.IsNullOrWhiteSpace(comm) && comm != "?" ? comm : (exePath ?? $"PID {tgid}"),
                ExecutablePath: exePath);

        var note = alive == 0 ? "Process not live at snapshot enrichment time (cached/unknown identity possible)." : null;
        return new NetworkUsageEvent(
            DateTimeOffset.UtcNow,
            proc,
            appHint,
            InterfaceName: null, // per-interface attribution not provided by this hook
            RxBytesDelta: rxDelta > 0 ? rxDelta : 0,
            TxBytesDelta: txDelta > 0 ? txDelta : 0,
            MeasurementPoint: Point,
            IsAttributed: true,
            AttributionNote: note);
    }

    private static NetworkUsageEvent? ParseUnattributed(string line)
    {
        // U\ttx_total\trx_total\ttx_delta\trx_delta\ttx_events\trx_events
        var p = line.Split('\t');
        if (p.Length < 5) return null;
        _ = long.TryParse(p[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out var txDelta);
        _ = long.TryParse(p[4], NumberStyles.Integer, CultureInfo.InvariantCulture, out var rxDelta);
        if (txDelta <= 0 && rxDelta <= 0) return null;
        return new NetworkUsageEvent(
            DateTimeOffset.UtcNow,
            Process: null,
            ApplicationHint: null,
            InterfaceName: null,
            RxBytesDelta: rxDelta > 0 ? rxDelta : 0,
            TxBytesDelta: txDelta > 0 ? txDelta : 0,
            MeasurementPoint: Point,
            IsAttributed: false,
            AttributionNote: "eBPF hook context without attributable task (tgid==0) at observation time.");
    }

    private static DateTimeOffset DeriveStartTimeUtc(ulong startTicks, long clkTck, long bootUnixMs)
    {
        if (startTicks == 0 || clkTck <= 0 || bootUnixMs <= 0)
            return DateTimeOffset.UtcNow; // fallback; reduces identity stability but avoids invalid time
        try
        {
            var ms = (double)startTicks * 1000.0 / clkTck;
            return DateTimeOffset.FromUnixTimeMilliseconds(bootUnixMs).AddMilliseconds(ms);
        }
        catch
        {
            return DateTimeOffset.UtcNow;
        }
    }

    private static string? ResolveLoaderPath(string? configured)
    {
        if (!string.IsNullOrWhiteSpace(configured))
            return File.Exists(configured) ? Path.GetFullPath(configured) : null;

        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "flow_tcp_bytes_loader"),
            Path.Combine(AppContext.BaseDirectory, "prototype", "bpf", "flow_tcp_bytes_loader"),
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "prototype", "bpf", "flow_tcp_bytes_loader")),
            Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), "prototype", "bpf", "flow_tcp_bytes_loader")),
            "/usr/lib/flow/flow_tcp_bytes_loader",
        };
        foreach (var c in candidates)
        {
            if (File.Exists(c)) return c;
        }
        return null;
    }
}

public sealed class EbpfTcpMonitorOptions
{
    public string? LoaderPath { get; set; }
    public int IntervalSeconds { get; set; } = 2;
    public int Rounds { get; set; } = 1_000_000; // effectively continuous; process/stop controls lifetime
}
