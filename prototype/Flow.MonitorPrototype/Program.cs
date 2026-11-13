using System.Diagnostics;
using System.Globalization;
using System.Text;

Console.OutputEncoding = Encoding.UTF8;

var mode = args.FirstOrDefault()?.Trim().ToLowerInvariant();
if (mode is "-h" or "--help" or "help")
{
    PrintHelp();
    return;
}

Console.WriteLine("Flow.MonitorPrototype (Phase 1 diagnostic scaffold)");
Console.WriteLine("NOTE: This build does NOT perform validated per-process byte attribution.");
Console.WriteLine("      It reports baseline discovery and interface counters for prototype work.\n");

PrintEnvironment();
Console.WriteLine();
PrintInterfaces();
Console.WriteLine();
PrintProcessSample(limit: 20);

if (mode == "sample")
{
    Console.WriteLine();
    await SampleInterfaceRatesAsync(TimeSpan.FromSeconds(2));
}

static void PrintHelp()
{
    Console.WriteLine("Usage: dotnet run --project prototype/Flow.MonitorPrototype [sample]\n");
    Console.WriteLine("  (default)  Print environment, /proc/net/dev interface snapshot, and process sample");
    Console.WriteLine("  sample     Also sample per-interface rx/tx deltas over ~2 seconds (aggregate counters)");
}

static void PrintEnvironment()
{
    Console.WriteLine("== Environment ==");
    Console.WriteLine($"UTC now:            {DateTimeOffset.UtcNow:O}");
    Console.WriteLine($"Local now:          {DateTimeOffset.Now:O}");
    Console.WriteLine($"OS:                 {System.Runtime.InteropServices.RuntimeInformation.OSDescription}");
    Console.WriteLine($"Kernel:             {Environment.OSVersion.VersionString}");
    Console.WriteLine($"Machine:            {Environment.MachineName}");
    Console.WriteLine($"64-bit OS/proc:     {Environment.Is64BitOperatingSystem}/{Environment.Is64BitProcess}");
    Console.WriteLine($"BTF present:        {File.Exists("/sys/kernel/btf/vmlinux")}");
    Console.WriteLine($"bpftool present:    {File.Exists("/usr/sbin/bpftool") || File.Exists("/usr/bin/bpftool")}");
    Console.WriteLine($"clang present:      {CanFindOnPath("clang")}");
    Console.WriteLine($"unprivileged_bpf:   {TryReadAllText("/proc/sys/kernel/unprivileged_bpf_disabled") ?? "(unavailable)"}");
}

static void PrintInterfaces()
{
    Console.WriteLine("== Interfaces (/proc/net/dev aggregate counters) ==");
    const string path = "/proc/net/dev";
    if (!File.Exists(path))
    {
        Console.WriteLine($"{path} not available.");
        return;
    }

    var lines = File.ReadAllLines(path);
    Console.WriteLine("Interface        RxBytes           TxBytes");
    foreach (var line in lines.Skip(2))
    {
        var parsed = ParseProcNetDevLine(line);
        if (parsed is null) continue;
        Console.WriteLine($"{parsed.Value.Name,-15} {parsed.Value.RxBytes,16} {parsed.Value.TxBytes,16}");
    }
}

static async Task SampleInterfaceRatesAsync(TimeSpan duration)
{
    Console.WriteLine($"== Interface delta sample ({duration.TotalSeconds:0.#}s, aggregate counters) ==");
    var before = ReadProcNetDev();
    var sw = Stopwatch.StartNew();
    await Task.Delay(duration);
    sw.Stop();
    var after = ReadProcNetDev();

    var secs = Math.Max(sw.Elapsed.TotalSeconds, 0.001);
    Console.WriteLine("Interface        RxB/s (approx)    TxB/s (approx)");
    foreach (var kv in after.OrderBy(k => k.Key, StringComparer.Ordinal))
    {
        before.TryGetValue(kv.Key, out var b);
        var rx = kv.Value.RxBytes >= b.RxBytes ? kv.Value.RxBytes - b.RxBytes : 0;
        var tx = kv.Value.TxBytes >= b.TxBytes ? kv.Value.TxBytes - b.TxBytes : 0;
        Console.WriteLine($"{kv.Key,-15} {rx / secs,16:0} {tx / secs,16:0}");
    }
}

static Dictionary<string, (ulong RxBytes, ulong TxBytes)> ReadProcNetDev()
{
    var dict = new Dictionary<string, (ulong, ulong)>(StringComparer.Ordinal);
    const string path = "/proc/net/dev";
    if (!File.Exists(path)) return dict;
    foreach (var line in File.ReadAllLines(path).Skip(2))
    {
        var parsed = ParseProcNetDevLine(line);
        if (parsed is null) continue;
        dict[parsed.Value.Name] = (parsed.Value.RxBytes, parsed.Value.TxBytes);
    }
    return dict;
}

static (string Name, ulong RxBytes, ulong TxBytes)? ParseProcNetDevLine(string line)
{
    var idx = line.IndexOf(':');
    if (idx < 0) return null;
    var name = line[..idx].Trim();
    var rest = line[(idx + 1)..];
    var parts = rest.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
    // rx: bytes packets errs drop fifo frame compressed multicast (8 fields)
    // tx: bytes packets errs drop fifo colls carrier compressed (8 fields)
    if (parts.Length < 16) return null;
    if (!ulong.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var rxBytes)) return null;
    if (!ulong.TryParse(parts[8], NumberStyles.Integer, CultureInfo.InvariantCulture, out var txBytes)) return null;
    return (name, rxBytes, txBytes);
}

static void PrintProcessSample(int limit)
{
    Console.WriteLine($"== Process sample (top {limit} by PID, identity inputs only; no byte attribution) ==");
    Console.WriteLine("PID      StartTime(UTC)        UID   Comm             Exe");
    var count = 0;
    foreach (var dir in Directory.EnumerateDirectories("/proc"))
    {
        var name = Path.GetFileName(dir);
        if (!int.TryParse(name, out var pid)) continue;
        try
        {
            var stat = TryReadAllText(Path.Combine(dir, "stat"));
            var startTicks = TryGetProcessStartTimeUtc(stat);
            var status = TryReadAllText(Path.Combine(dir, "status"));
            var uid = TryParseUid(status);
            var comm = TryReadComm(stat) ?? "?";
            var exe = TryReadLink(Path.Combine(dir, "exe")) ?? "(unavailable)";
            Console.WriteLine($"{pid,-8} {FormatTime(startTicks),-21} {uid,-5} {Trunc(comm, 16),-16} {Trunc(exe, 60)}");
            count++;
            if (count >= limit) break;
        }
        catch
        {
            // Process may exit between enumeration and reads; ignore for diagnostic sample.
        }
    }
    if (count == 0) Console.WriteLine("(no processes readable)");
}

static DateTimeOffset? TryGetProcessStartTimeUtc(string? stat)
{
    // /proc/[pid]/stat: pid (comm) state ... field 22 = starttime (clock ticks after boot)
    // Parsing comm is tricky due to spaces/parens; use last ')' to split.
    if (string.IsNullOrWhiteSpace(stat)) return null;
    var close = stat.LastIndexOf(')');
    if (close < 0 || close + 2 >= stat.Length) return null;
    var after = stat[(close + 2)..]; // skip ") "
    var fields = after.Split(' ', StringSplitOptions.RemoveEmptyEntries);
    // fields[0] = state (field 3 overall), so starttime (field 22) is index 19 in this array
    if (fields.Length <= 19) return null;
    if (!ulong.TryParse(fields[19], NumberStyles.Integer, CultureInfo.InvariantCulture, out var startTicks)) return null;
    try
    {
        var clkTck = (long)TicksPerSecond();
        var uptimeSeconds = GetUptimeSeconds();
        if (clkTck <= 0 || uptimeSeconds is null) return null;
        var bootUtc = DateTimeOffset.UtcNow - TimeSpan.FromSeconds(uptimeSeconds.Value);
        var procStart = bootUtc + TimeSpan.FromSeconds(startTicks / (double)clkTck);
        return procStart;
    }
    catch
    {
        return null;
    }
}

static long TicksPerSecond()
{
    try
    {
        using var p = Process.Start(new ProcessStartInfo("getconf", "CLK_TCK") { RedirectStandardOutput = true, UseShellExecute = false });
        if (p is null) return 100;
        var s = p.StandardOutput.ReadToEnd().Trim();
        p.WaitForExit(1000);
        return long.TryParse(s, out var v) && v > 0 ? v : 100;
    }
    catch { return 100; }
}

static double? GetUptimeSeconds()
{
    var text = TryReadAllText("/proc/uptime");
    if (string.IsNullOrWhiteSpace(text)) return null;
    var first = text.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
    return double.TryParse(first, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : null;
}

static string? TryParseUid(string? status)
{
    if (string.IsNullOrWhiteSpace(status)) return null;
    using var sr = new StringReader(status);
    string? line;
    while ((line = sr.ReadLine()) is not null)
    {
        if (line.StartsWith("Uid:", StringComparison.Ordinal))
        {
            var parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            return parts.Length > 1 ? parts[1] : null;
        }
    }
    return null;
}

static string? TryReadComm(string? stat)
{
    if (string.IsNullOrWhiteSpace(stat)) return null;
    var open = stat.IndexOf('(');
    var close = stat.LastIndexOf(')');
    if (open < 0 || close <= open) return null;
    return stat.Substring(open + 1, close - open - 1);
}

static string FormatTime(DateTimeOffset? dto) => dto?.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) ?? "(unknown)";

static string Trunc(string s, int max) => s.Length <= max ? s : s[..(max - 1)] + "…";

static string? TryReadAllText(string path)
{
    try { return File.ReadAllText(path); } catch { return null; }
}

static string? TryReadLink(string path)
{
    try
    {
        var fi = new FileInfo(path);
        var target = fi.ResolveLinkTarget(true);
        return target?.FullName ?? fi.LinkTarget;
    }
    catch { return null; }
}

static bool CanFindOnPath(string exe)
{
    var path = Environment.GetEnvironmentVariable("PATH") ?? "";
    foreach (var dir in path.Split(':', StringSplitOptions.RemoveEmptyEntries))
    {
        try
        {
            var full = Path.Combine(dir, exe);
            if (File.Exists(full)) return true;
        }
        catch { }
    }
    return false;
}
