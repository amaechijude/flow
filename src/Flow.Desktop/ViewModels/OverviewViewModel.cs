using System;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Flow.Contracts.Monitoring;
using Flow.Contracts.Usage;
using Flow.Desktop.Services;

namespace Flow.Desktop.ViewModels;

public partial class OverviewViewModel : ViewModelBase
{
    private readonly IDaemonStatusClient _statusClient;
    private readonly IUsageQueryClient _usageClient;

    [ObservableProperty] public partial string StatusText { get; set; } = "Loading…";
    [ObservableProperty] public partial string StatusDetail { get; set; } = "";
    [ObservableProperty] public partial string SummaryText { get; set; } = "";
    [ObservableProperty] public partial string RangeText { get; set; } = "";
    [ObservableProperty] public partial string AttributionNote { get; set; } = "Historical totals are read from the local Flow database. Per-process attribution depends on the active daemon/provider; unattributed traffic is shown separately when present.";
    [ObservableProperty] public partial bool IsBusy { get; set; }
    [ObservableProperty] public partial bool HasNoApplications { get; set; } = true;

    public ObservableCollection<ApplicationUsageDto> TopApplications { get; } = new();

    public OverviewViewModel(IDaemonStatusClient statusClient, IUsageQueryClient usageClient)
    {
        _statusClient = statusClient;
        _usageClient = usageClient;
        _ = RefreshAsync();
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
            StatusDetail = status.HealthMessage ?? status.LastError ?? "No live daemon status available.";

            var end = DateTimeOffset.UtcNow;
            var start = end.AddHours(-24);
            RangeText = $"Last 24 hours (UTC): {start.ToString("u", CultureInfo.InvariantCulture)} – {end.ToString("u", CultureInfo.InvariantCulture)}";

            var summary = await _usageClient.GetSummaryAsync(start, end);
            var totalRx = summary.RxBytes + summary.UnattributedRxBytes;
            var totalTx = summary.TxBytes + summary.UnattributedTxBytes;
            SummaryText = $"Attributed ↓ {FormatBytes(summary.RxBytes)} ↑ {FormatBytes(summary.TxBytes)} | " +
                          $"Unattributed ↓ {FormatBytes(summary.UnattributedRxBytes)} ↑ {FormatBytes(summary.UnattributedTxBytes)} | " +
                          $"Total ↓ {FormatBytes(totalRx)} ↑ {FormatBytes(totalTx)}";

            TopApplications.Clear();
            foreach (var app in await _usageClient.GetTopApplicationsAsync(start, end, limit: 10))
                TopApplications.Add(app);
            HasNoApplications = TopApplications.Count == 0;
        }
        catch (Exception ex)
        {
            StatusDetail = $"Failed to load data: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KiB", "MiB", "GiB", "TiB"];
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1) { value /= 1024; unit++; }
        return unit == 0 ? $"{bytes} {units[unit]}" : $"{value.ToString("0.##", CultureInfo.InvariantCulture)} {units[unit]}";
    }
}
