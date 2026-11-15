using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Flow.Contracts.Usage;
using Flow.Desktop.Services;

namespace Flow.Desktop.ViewModels;

public sealed record RangeOption(string Label, TimeSpan Duration);

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

    [ObservableProperty] public partial string StatusText { get; set; } = "Loading…";
    [ObservableProperty] public partial string StatusDetail { get; set; } = "";
    [ObservableProperty] public partial string SummaryText { get; set; } = "";
    [ObservableProperty] public partial string RangeText { get; set; } = "";
    [ObservableProperty] public partial string DataSourceNote { get; set; } = "";
    [ObservableProperty] public partial string AttributionNote { get; set; } = "Per-process attribution depends on the active daemon/provider and measurement point. Unattributed traffic is shown separately and must not be treated as zero. Procfs provides interface-level totals (unattributed); ebpf-tcp is TCP-only and may not map per-interface.";
    [ObservableProperty] public partial bool IsBusy { get; set; }
    [ObservableProperty] public partial bool HasNoApplications { get; set; } = true;
    [ObservableProperty] public partial bool HasNoInterfaces { get; set; } = true;
    [ObservableProperty] public partial bool HasNoProcesses { get; set; } = true;
    [ObservableProperty] public partial RangeOption? SelectedRange { get; set; }

    public ObservableCollection<RangeOption> Ranges { get; } = new(
    [
        new RangeOption("Last 1 hour", TimeSpan.FromHours(1)),
        new RangeOption("Last 6 hours", TimeSpan.FromHours(6)),
        new RangeOption("Last 24 hours", TimeSpan.FromHours(24)),
        new RangeOption("Last 7 days", TimeSpan.FromDays(7)),
        new RangeOption("Last 30 days", TimeSpan.FromDays(30)),
    ]);

    public ObservableCollection<ApplicationUsageRow> TopApplications { get; } = new();
    public ObservableCollection<InterfaceUsageRow> Interfaces { get; } = new();
    public ObservableCollection<ProcessUsageRow> TopProcesses { get; } = new();

    public OverviewViewModel(IDaemonStatusClient statusClient, IUsageQueryClient usageClient)
    {
        _statusClient = statusClient;
        _usageClient = usageClient;
        SelectedRange = Ranges[2];
        _ = RefreshAsync();
    }

    partial void OnSelectedRangeChanged(RangeOption? value)
    {
        if (value is not null) _ = RefreshAsync();
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

            var end = DateTimeOffset.UtcNow;
            var duration = SelectedRange?.Duration ?? TimeSpan.FromHours(24);
            var start = end - duration;
            RangeText = $"{SelectedRange?.Label ?? "Range"} (UTC): {start.ToString("u", CultureInfo.InvariantCulture)} – {end.ToString("u", CultureInfo.InvariantCulture)}";

            var summary = await _usageClient.GetSummaryAsync(start, end);
            var totalRx = summary.RxBytes + summary.UnattributedRxBytes;
            var totalTx = summary.TxBytes + summary.UnattributedTxBytes;
            SummaryText = $"Attributed ↓ {FormatBytes(summary.RxBytes)} ↑ {FormatBytes(summary.TxBytes)}   |   " +
                          $"Unattributed ↓ {FormatBytes(summary.UnattributedRxBytes)} ↑ {FormatBytes(summary.UnattributedTxBytes)}   |   " +
                          $"Total ↓ {FormatBytes(totalRx)} ↑ {FormatBytes(totalTx)}";

            var apps = await _usageClient.GetTopApplicationsAsync(start, end, limit: 15);
            TopApplications.Clear();
            foreach (var a in apps) TopApplications.Add(ApplicationUsageRow.From(a));
            HasNoApplications = TopApplications.Count == 0;

            var ifaces = await _usageClient.GetInterfaceUsageAsync(start, end);
            Interfaces.Clear();
            foreach (var i in ifaces) Interfaces.Add(InterfaceUsageRow.From(i));
            HasNoInterfaces = Interfaces.Count == 0;

            var procs = await _usageClient.GetTopProcessesAsync(start, end, limit: 15);
            TopProcesses.Clear();
            foreach (var p in procs) TopProcesses.Add(ProcessUsageRow.From(p));
            HasNoProcesses = TopProcesses.Count == 0;
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

    private static string FormatBytes(long bytes) => ByteFormat.Bytes(bytes);
}
