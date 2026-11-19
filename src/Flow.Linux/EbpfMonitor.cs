using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using Flow.Application.Monitoring;
using Flow.Domain.Measurement;

namespace Flow.Linux.Monitoring;

/// <summary>
/// Per-process TCP and UDP byte attribution provider backed by the validated eBPF
/// prototype loader (fexit tcp_sendmsg/udp_sendmsg, kretprobe tcp_recvmsg/udp_recvmsg).
/// Requires sufficient privileges to load/attach BPF. Emits delta events derived from
/// periodic snapshots produced by the loader in TSV mode. The loader/type retain the
/// historical "tcp"/"flow_tcp_bytes" names; QUIC is counted as UDP payload.
/// </summary>
public sealed class EbpfMonitor : INetworkMonitor, IDisposable
{
    public static readonly MeasurementPoint Point = new(
        "ebpf",
        "fexit/tcp_sendmsg+udp_sendmsg, kretprobe/tcp_recvmsg+udp_recvmsg, kretprobe/__skb_recv_udp (RX ifindex)",
        "TCP and UDP payload bytes from positive return values of the send/recv kernel functions observed via eBPF (TGID/UID attribution, per-interface where the socket route or received skb resolves it, cumulative snapshots converted to deltas). QUIC is counted as UDP payload; ICMP and other non-TCP/UDP paths are not counted.");

    private readonly EbpfMonitorOptions _options;
    private Process? _process;
    private CancellationTokenSource? _cts;
    private Task? _stderrTask;

    public EbpfMonitor(EbpfMonitorOptions? options = null)
    {
        _options = options ?? new EbpfMonitorOptions();
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
                "eBPF loader not found. Set Flow:Ebpf:LoaderPath or build prototype/bpf/flow_tcp_bytes_loader. " +
                "This provider requires a privileged loader built from the validated prototype (the file name is historical; it counts TCP and UDP).");

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

        // Drain stderr to avoid blocking and retain a short tail so attach/load
        // failures surface as provider errors instead of a silently idle source.
        var stderrTail = new ConcurrentQueue<string>();
        _stderrTask = Task.Run(async () =>
        {
            try
            {
                while (await _process.StandardError.ReadLineAsync(ct).ConfigureAwait(false) is { } errLine)
                {
                    stderrTail.Enqueue(errLine);
                    while (stderrTail.Count > 20)
                        stderrTail.TryDequeue(out _);
                }
            }
            catch { }
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
            {
                if (ct.IsCancellationRequested)
                    yield break;

                int? exitCode = null;
                try
                {
                    await _process.WaitForExitAsync(ct).ConfigureAwait(false);
                    exitCode = _process.ExitCode;
                }
                catch { }

                if (exitCode is null or 0)
                    yield break;

                // The stderr reader may still be draining when stdout closes.
                // Wait briefly so an error report includes the loader's diagnostics.
                if (_stderrTask is not null)
                {
                    try { await _stderrTask.WaitAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false); }
                    catch { }
                }

                var detail = string.Join(Environment.NewLine, stderrTail);
                throw new InvalidOperationException(
                    $"eBPF loader exited with code {exitCode} before completing the event stream. " +
                    (string.IsNullOrWhiteSpace(detail) ? "No stderr diagnostics were captured." : $"stderr:{Environment.NewLine}{detail}"));
            }

            if (line.Length == 0) continue;
            if (line.StartsWith("M\t", StringComparison.Ordinal))
            {
                EbpfSnapshotParser.ParseMeta(line, out clkTck, out bootUnixMs);
                continue;
            }

            if (line.StartsWith("E\t", StringComparison.Ordinal))
            {
                var ev = EbpfSnapshotParser.ParseEntry(line, Point, clkTck, bootUnixMs);
                if (ev is not null) yield return ev;
                continue;
            }

            if (line.StartsWith("F\t", StringComparison.Ordinal))
            {
                var ev = EbpfSnapshotParser.ParseOverflow(line, Point);
                if (ev is not null) yield return ev;
                continue;
            }

            if (line.StartsWith("U\t", StringComparison.Ordinal))
            {
                var ev = EbpfSnapshotParser.ParseUnattributed(line, Point);
                if (ev is not null) yield return ev;
                continue;
            }

            // Ignore S and any other lines
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

public sealed class EbpfMonitorOptions
{
    public string? LoaderPath { get; set; }
    public int IntervalSeconds { get; set; } = 2;
    public int Rounds { get; set; } = 1_000_000; // effectively continuous; process/stop controls lifetime
}
