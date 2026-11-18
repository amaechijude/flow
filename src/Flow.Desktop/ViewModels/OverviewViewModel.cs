using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Flow.Contracts.Usage;
using Flow.Desktop.Services;

namespace Flow.Desktop.ViewModels;

public sealed record RangeOption(string Label, TimeSpan Duration, bool IsCustom = false);
public sealed record InterfaceFilterOption(string Label, string? InterfaceId);
public sealed record TimeSeriesRow(string Bucket, string Usage, string Total);

internal static class ByteFormat
{
    public static string Pair(long rx, long tx) => $"↓ {Bytes(rx)}  ↑ {Bytes(tx)}";
    public static string Bytes(long bytes)
    {
        string[] units = ["B", "KiB", "MiB", "GiB", "TiB"];
        double value = Math.Abs(bytes);
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1) { value /= 1024; unit++; }
        var s = unit == 0 ? bytes.ToString(CultureInfo.InvariantCulture) : (Math.Sign(bytes) * value).ToString("0.##", CultureInfo.InvariantCulture);
        return $"{s} {units[unit]}";
    }
}

public sealed record ApplicationUsageRow(string Key, string? DisplayName, string Attributed, string Unattributed, string Total)
{
    public static ApplicationUsageRow From(ApplicationUsageDto dto)
    {
        var attrRx = dto.RxBytes; var attrTx = dto.TxBytes;
        var unRx = dto.UnattributedRxBytes; var unTx = dto.UnattributedTxBytes;
        return new(dto.ApplicationKey, dto.DisplayName,
            ByteFormat.Pair(attrRx, attrTx), ByteFormat.Pair(unRx, unTx), ByteFormat.Pair(attrRx + unRx, attrTx + unTx));
    }
}

public sealed record InterfaceUsageRow(string Key, string? Name, string? Type, string Attributed, string Unattributed, string Total)
{
    public static InterfaceUsageRow From(InterfaceUsageDto dto)
        => new(dto.InterfaceKey, dto.InterfaceName, dto.InterfaceType,
            ByteFormat.Pair(dto.RxBytes, dto.TxBytes), ByteFormat.Pair(dto.UnattributedRxBytes, dto.UnattributedTxBytes),
            ByteFormat.Pair(dto.RxBytes + dto.UnattributedRxBytes, dto.TxBytes + dto.UnattributedTxBytes));
}

public sealed record ProcessUsageRow(string Key, string? Application, string? Executable, string Attributed, string Unattributed, string Total)
{
    public static ProcessUsageRow From(ProcessUsageDto dto)
        => new(dto.ProcessStableKey, dto.DisplayName ?? dto.ApplicationKey, dto.ExecutablePath,
            ByteFormat.Pair(dto.RxBytes, dto.TxBytes), ByteFormat.Pair(dto.UnattributedRxBytes, dto.UnattributedTxBytes),
            ByteFormat.Pair(dto.RxBytes + dto.UnattributedRxBytes, dto.TxBytes + dto.UnattributedTxBytes));
}

public partial class OverviewViewModel : ViewModelBase
{
    private readonly IDaemonStatusClient _statusClient;
    private readonly IUsageQueryClient _usageClient;
    private bool _suppressRefresh;

    [ObservableProperty] public partial string StatusText { get; set; } = "Loading…";
    [ObservableProperty] public partial string StatusDetail { get; set; } = "";
    [ObservableProperty] public partial string SummaryText { get; set; } = "";
    [ObservableProperty] public partial string RangeText { get; set; } = "";
    [ObservableProperty] public partial string DataSourceNote { get; set; } = "";
    [ObservableProperty] public partial string ExportStatus { get; set; } = "";
    [ObservableProperty] public partial string AttributionNote { get; set; } = "Per-process attribution depends on the active daemon/provider and measurement point. Unattributed traffic is shown separately and must not be treated as zero. Procfs provides interface-level totals (unattributed); ebpf-tcp is TCP-only and may not map per-interface.";
    [ObservableProperty] public partial bool IsBusy { get; set; }
    [ObservableProperty] public partial bool HasNoApplications { get; set; } = true;
    [ObservableProperty] public partial bool HasNoInterfaces { get; set; } = true;
    [ObservableProperty] public partial bool HasNoProcesses { get; set; } = true;
    [ObservableProperty] public partial bool HasNoSeries { get; set; } = true;
    [ObservableProperty] public partial bool IsCustomRange { get; set; }
    [ObservableProperty] public partial RangeOption? SelectedRange { get; set; }
    [ObservableProperty] public partial InterfaceFilterOption? SelectedInterfaceFilter { get; set; }
    [ObservableProperty] public partial DateTimeOffset? CustomStartDate { get; set; }
    [ObservableProperty] public partial DateTimeOffset? CustomEndDate { get; set; }

    public ObservableCollection<RangeOption> Ranges { get; } = new(
    [
        new RangeOption("Last 1 hour", TimeSpan.FromHours(1)),
        new RangeOption("Last 6 hours", TimeSpan.FromHours(6)),
        new RangeOption("Last 12 hours", TimeSpan.FromHours(12)),
        new RangeOption("Last 24 hours", TimeSpan.FromHours(24)),
        new RangeOption("Today", TimeSpan.Zero),
        new RangeOption("Yesterday", TimeSpan.Zero),
        new RangeOption("Last 7 days", TimeSpan.FromDays(7)),
        new RangeOption("Last 30 days", TimeSpan.FromDays(30)),
        new RangeOption("Custom range", TimeSpan.Zero, true),
    ]);

    public ObservableCollection<InterfaceFilterOption> InterfaceFilters { get; } = new([new InterfaceFilterOption("All interfaces", null)]);
    public ObservableCollection<ApplicationUsageRow> TopApplications { get; } = new();
    public ObservableCollection<InterfaceUsageRow> Interfaces { get; } = new();
    public ObservableCollection<ProcessUsageRow> TopProcesses { get; } = new();
    public ObservableCollection<TimeSeriesRow> TimeSeries { get; } = new();

    public OverviewViewModel(IDaemonStatusClient statusClient, IUsageQueryClient usageClient)
    {
        _statusClient = statusClient;
        _usageClient = usageClient;
        var todayLocal = DateTimeOffset.Now.Date;
        CustomStartDate = new DateTimeOffset(todayLocal, DateTimeOffset.Now.Offset);
        CustomEndDate = new DateTimeOffset(todayLocal, DateTimeOffset.Now.Offset);
        SelectedRange = Ranges.First(r => r.Label == "Last 24 hours");
        SelectedInterfaceFilter = InterfaceFilters[0];
        _ = RefreshAsync();
    }

    partial void OnSelectedRangeChanged(RangeOption? value)
    {
        IsCustomRange = value?.IsCustom == true;
        if (!_suppressRefresh && value is not null && !value.IsCustom) _ = RefreshAsync();
    }

    partial void OnSelectedInterfaceFilterChanged(InterfaceFilterOption? value)
    {
        if (!_suppressRefresh && value is not null) _ = RefreshAsync();
    }

    [RelayCommand]
    public async Task ExportCsvAsync()
    {
        if (IsBusy) return;
        try
        {
            IsBusy = true;
            ExportStatus = "";
            var (start, end, rangeLabel) = GetCurrentRange();
            var ifaceId = SelectedInterfaceFilter?.InterfaceId;
            var summary = await _usageClient.GetSummaryAsync(start, end, ifaceId);
            var apps = await _usageClient.GetTopApplicationsAsync(start, end, ifaceId, limit: 1000);
            var procs = await _usageClient.GetTopProcessesAsync(start, end, ifaceId, limit: 1000);
            var ifaces = await _usageClient.GetInterfaceUsageAsync(start, end);
            var series = await _usageClient.GetUsageTimeSeriesAsync(start, end, ifaceId);

            var dir = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var file = Path.Combine(dir, $"flow-export-{DateTimeOffset.UtcNow:yyyyMMddHHmmss}.csv");
            using var sw = new StreamWriter(file, false, System.Text.Encoding.UTF8);
            sw.WriteLine("# Flow usage export (real persisted data)");
            sw.WriteLine($"# GeneratedUtc,{DateTimeOffset.UtcNow:O}");
            sw.WriteLine($"# RangeLabel,{Escape(rangeLabel)}");
            sw.WriteLine($"# StartUtc,{start:O}");
            sw.WriteLine($"# EndUtc,{end:O}");
            sw.WriteLine($"# InterfaceFilter,{Escape(ifaceId ?? "(all)")}");
            sw.WriteLine("# Caveats,Per-process attribution depends on provider/measurement point; unattributed traffic is separate; procfs is interface-level/unattributed; ebpf-tcp is TCP-only and may lack per-interface mapping; process identity/lifecycle hardening ongoing.");
            sw.WriteLine();
            sw.WriteLine("section,summary");
            sw.WriteLine("metric,rx_bytes,tx_bytes");
            sw.WriteLine($"attributed,{summary.RxBytes},{summary.TxBytes}");
            sw.WriteLine($"unattributed,{summary.UnattributedRxBytes},{summary.UnattributedTxBytes}");
            sw.WriteLine($"total,{summary.RxBytes + summary.UnattributedRxBytes},{summary.TxBytes + summary.UnattributedTxBytes}");
            sw.WriteLine();
            sw.WriteLine("section,applications");
            sw.WriteLine("application_key,display_name,attributed_rx,attributed_tx,unattributed_rx,unattributed_tx");
            foreach (var a in apps) sw.WriteLine($"{Escape(a.ApplicationKey)},{Escape(a.DisplayName)},{a.RxBytes},{a.TxBytes},{a.UnattributedRxBytes},{a.UnattributedTxBytes}");
            sw.WriteLine();
            sw.WriteLine("section,processes");
            sw.WriteLine("process_stable_key,application_key,display_name,executable_path,attributed_rx,attributed_tx,unattributed_rx,unattributed_tx");
            foreach (var p in procs) sw.WriteLine($"{Escape(p.ProcessStableKey)},{Escape(p.ApplicationKey)},{Escape(p.DisplayName)},{Escape(p.ExecutablePath)},{p.RxBytes},{p.TxBytes},{p.UnattributedRxBytes},{p.UnattributedTxBytes}");
            sw.WriteLine();
            sw.WriteLine("section,interfaces");
            sw.WriteLine("interface_key,interface_name,interface_type,attributed_rx,attributed_tx,unattributed_rx,unattributed_tx");
            foreach (var i in ifaces) sw.WriteLine($"{Escape(i.InterfaceKey)},{Escape(i.InterfaceName)},{Escape(i.InterfaceType)},{i.RxBytes},{i.TxBytes},{i.UnattributedRxBytes},{i.UnattributedTxBytes}");
            sw.WriteLine();
            sw.WriteLine("section,time_series_1m");
            sw.WriteLine("bucket_start_utc,rx_bytes,tx_bytes");
            foreach (var p in series) sw.WriteLine($"{p.BucketStartUtc:O},{p.RxBytes},{p.TxBytes}");
            ExportStatus = $"Exported CSV: {file}";
        }
        catch (Exception ex)
        {
            ExportStatus = $"Export failed: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private (DateTimeOffset start, DateTimeOffset end, string label) GetCurrentRange()
    {
        if (SelectedRange?.IsCustom == true)
        {
            var sLocal = (CustomStartDate ?? DateTimeOffset.Now).Date;
            var eLocal = (CustomEndDate ?? CustomStartDate ?? DateTimeOffset.Now).Date;
            if (eLocal < sLocal) (sLocal, eLocal) = (eLocal, sLocal);
            var localOffset = DateTimeOffset.Now.Offset;
            return (new DateTimeOffset(sLocal, localOffset).ToUniversalTime(), new DateTimeOffset(eLocal.AddDays(1), localOffset).ToUniversalTime(), "Custom range");
        }
        if (string.Equals(SelectedRange?.Label, "Today", StringComparison.Ordinal))
        {
            var localOffset = DateTimeOffset.Now.Offset;
            var todayLocal = DateTimeOffset.Now.Date;
            return (new DateTimeOffset(todayLocal, localOffset).ToUniversalTime(), new DateTimeOffset(todayLocal.AddDays(1), localOffset).ToUniversalTime(), "Today");
        }
        if (string.Equals(SelectedRange?.Label, "Yesterday", StringComparison.Ordinal))
        {
            var localOffset = DateTimeOffset.Now.Offset;
            var todayLocal = DateTimeOffset.Now.Date;
            return (new DateTimeOffset(todayLocal.AddDays(-1), localOffset).ToUniversalTime(), new DateTimeOffset(todayLocal, localOffset).ToUniversalTime(), "Yesterday");
        }
        var end = DateTimeOffset.UtcNow;
        var duration = SelectedRange?.Duration ?? TimeSpan.FromHours(24);
        return (end - duration, end, SelectedRange?.Label ?? "Range");
    }

    private static string Escape(string? value)
    {
        if (string.IsNullOrEmpty(value)) return "";
        if (value.Contains(',') || value.Contains('"') || value.Contains('\n') || value.Contains('\r'))
            return "\"" + value.Replace("\"", "\"\"") + "\"";
        return value;
    }

    [RelayCommand]
    public async Task RefreshAsync()
    {
        if (IsBusy) return;
        try
        {
            IsBusy = true;
            var status = await _statusClient.GetStatusAsync();
            StatusText = $"State: {status.State} (provider: {status.ProviderName ?? "n/a"})";
            var diag = $"events rx={status.EventsReceived} agg={status.EventsAggregated} drop={status.EventsDropped} batches={status.BatchesWritten} writeErr={status.WriteErrors}";
            StatusDetail = $"{status.HealthMessage ?? status.LastError ?? "No details."} | {diag}";
            DataSourceNote = status.State == Flow.Domain.Monitoring.MonitoringState.Unavailable
                ? "Live daemon status unavailable (IPC unreachable). Historical data shown from local database when available; this is real persisted data, not fabricated."
                : "Live status from daemon IPC. Historical usage queried via daemon IPC (with local DB fallback to the same persisted data if needed).";

            var (start, end, rangeLabel) = GetCurrentRange();
            var ifaceId = SelectedInterfaceFilter?.InterfaceId;
            RangeText = $"{rangeLabel} (UTC): {start:u} – {end:u}" + (ifaceId is null ? "" : $" | Interface filter: {ifaceId}");

            // Interface breakdown is always unfiltered for the range (shows all interfaces observed).
            var ifaces = await _usageClient.GetInterfaceUsageAsync(start, end);
            Interfaces.Clear();
            foreach (var i in ifaces) Interfaces.Add(InterfaceUsageRow.From(i));
            HasNoInterfaces = Interfaces.Count == 0;
            UpdateInterfaceFilters(ifaces);

            var summary = await _usageClient.GetSummaryAsync(start, end, ifaceId);
            var totalRx = summary.RxBytes + summary.UnattributedRxBytes;
            var totalTx = summary.TxBytes + summary.UnattributedTxBytes;
            SummaryText = $"Attributed ↓ {ByteFormat.Bytes(summary.RxBytes)} ↑ {ByteFormat.Bytes(summary.TxBytes)}   |   " +
                          $"Unattributed ↓ {ByteFormat.Bytes(summary.UnattributedRxBytes)} ↑ {ByteFormat.Bytes(summary.UnattributedTxBytes)}   |   " +
                          $"Total ↓ {ByteFormat.Bytes(totalRx)} ↑ {ByteFormat.Bytes(totalTx)}";

            var apps = await _usageClient.GetTopApplicationsAsync(start, end, ifaceId, limit: 15);
            TopApplications.Clear();
            foreach (var a in apps) TopApplications.Add(ApplicationUsageRow.From(a));
            HasNoApplications = TopApplications.Count == 0;

            var procs = await _usageClient.GetTopProcessesAsync(start, end, ifaceId, limit: 15);
            TopProcesses.Clear();
            foreach (var p in procs) TopProcesses.Add(ProcessUsageRow.From(p));
            HasNoProcesses = TopProcesses.Count == 0;

            var series = await _usageClient.GetUsageTimeSeriesAsync(start, end, ifaceId);
            TimeSeries.Clear();
            foreach (var p in series.TakeLast(48))
                TimeSeries.Add(new TimeSeriesRow(p.BucketStartUtc.ToString("MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture), ByteFormat.Pair(p.RxBytes, p.TxBytes), ByteFormat.Bytes(p.RxBytes + p.TxBytes)));
            HasNoSeries = TimeSeries.Count == 0;
        }
        catch (Exception ex)
        {
            StatusDetail = $"Failed to load data: {ex.Message}";
            DataSourceNote = "Query failed; values may be stale or unavailable. No fabricated data is shown.";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void UpdateInterfaceFilters(IReadOnlyList<InterfaceUsageDto> ifaces)
    {
        var desired = new List<InterfaceFilterOption> { new("All interfaces", null) };
        desired.AddRange(ifaces.Select(i => new InterfaceFilterOption(string.IsNullOrWhiteSpace(i.InterfaceName) ? i.InterfaceKey : $"{i.InterfaceName} ({i.InterfaceKey})", i.InterfaceKey)));
        var currentId = SelectedInterfaceFilter?.InterfaceId;
        var same = InterfaceFilters.Count == desired.Count && InterfaceFilters.Zip(desired).All(z => z.First.InterfaceId == z.Second.InterfaceId && z.First.Label == z.Second.Label);
        if (same) return;

        _suppressRefresh = true;
        try
        {
            InterfaceFilters.Clear();
            foreach (var o in desired) InterfaceFilters.Add(o);
            SelectedInterfaceFilter = InterfaceFilters.FirstOrDefault(o => o.InterfaceId == currentId) ?? InterfaceFilters[0];
        }
        finally
        {
            _suppressRefresh = false;
        }
    }
}
