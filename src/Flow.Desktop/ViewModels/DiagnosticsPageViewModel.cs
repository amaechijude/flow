using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using Flow.Domain.Monitoring;

namespace Flow.Desktop.ViewModels;

public partial class DiagnosticsPageViewModel : ViewModelBase
{
    private static readonly IBrush LiveBrush = new SolidColorBrush(Color.Parse("#4EDEA3"));
    private static readonly IBrush WarningBrush = new SolidColorBrush(Color.Parse("#F5B44A"));
    private static readonly IBrush ErrorBrush = new SolidColorBrush(Color.Parse("#FF8A93"));
    private IReadOnlyList<TimeSeriesRow> _allBuckets = Array.Empty<TimeSeriesRow>();

    [ObservableProperty] public partial string StateText { get; set; } = "Unknown";
    [ObservableProperty] public partial IBrush StateBrush { get; set; } = WarningBrush;
    [ObservableProperty] public partial string ProviderText { get; set; } = "n/a";
    [ObservableProperty] public partial string MeasurementText { get; set; } = "Measurement description not reported by daemon.";
    [ObservableProperty] public partial string HealthText { get; set; } = "No health detail.";
    [ObservableProperty] public partial string LastErrorText { get; set; } = "";
    [ObservableProperty] public partial bool HasLastError { get; set; }
    [ObservableProperty] public partial string LastUpdatedText { get; set; } = "—";
    [ObservableProperty] public partial string LastEventText { get; set; } = "—";
    [ObservableProperty] public partial string LastFlushText { get; set; } = "—";

    [ObservableProperty] public partial string EventsReceivedText { get; set; } = "0";
    [ObservableProperty] public partial string EventsAggregatedText { get; set; } = "0";
    [ObservableProperty] public partial string EventsDroppedText { get; set; } = "0";
    [ObservableProperty] public partial string BatchesWrittenText { get; set; } = "0";
    [ObservableProperty] public partial string WriteErrorsText { get; set; } = "0";
    [ObservableProperty] public partial string DropRateText { get; set; } = "0.00%";
    [ObservableProperty] public partial double DropShare { get; set; }

    [ObservableProperty] public partial string PendingDurableText { get; set; } = "0";
    [ObservableProperty] public partial string OldestPendingText { get; set; } = "—";
    [ObservableProperty] public partial string DurableAttemptsText { get; set; } = "0";
    [ObservableProperty] public partial string NextRetryText { get; set; } = "—";
    [ObservableProperty] public partial string BackoffText { get; set; } = "0.0 s";
    [ObservableProperty] public partial bool HasPendingDurable { get; set; }

    [ObservableProperty] public partial string UsageSourceText { get; set; } = "not queried";
    [ObservableProperty] public partial string StatusSourceText { get; set; } = "not queried";
    [ObservableProperty] public partial string SignalText { get; set; } = "No signal state yet.";
    [ObservableProperty] public partial string LastSignalText { get; set; } = "—";
    [ObservableProperty] public partial string ContractText { get; set; } = "org.flow.Daemon1 · v3";
    [ObservableProperty] public partial string ServicePathText { get; set; } = "org.flow.Daemon · /org/flow/Daemon";

    [ObservableProperty] public partial string BucketSearchText { get; set; } = "";
    [ObservableProperty] public partial string BucketSummary { get; set; } = "0 buckets";
    [ObservableProperty] public partial bool HasNoBuckets { get; set; } = true;
    [ObservableProperty] public partial string WindowText { get; set; } = "—";

    public ObservableCollection<TimeSeriesRow> Buckets { get; } = new();

    partial void OnBucketSearchTextChanged(string value) => ApplyBuckets();

    public void Apply(UsageSnapshot snapshot)
    {
        var status = snapshot.Status;
        StateText = status.State switch
        {
            MonitoringState.Monitoring => "Monitoring",
            MonitoringState.Degraded => "Degraded",
            MonitoringState.Paused => "Paused",
            MonitoringState.Initializing => "Initializing",
            _ => "Unavailable",
        };
        StateBrush = status.State switch
        {
            MonitoringState.Monitoring => LiveBrush,
            MonitoringState.Degraded or MonitoringState.Paused or MonitoringState.Initializing => WarningBrush,
            _ => ErrorBrush,
        };

        ProviderText = status.ProviderName ?? "n/a";
        MeasurementText = status.MeasurementDescription ?? "Daemon does not report a measurement description for this provider.";
        HealthText = status.HealthMessage ?? "No health message.";
        HasLastError = !string.IsNullOrWhiteSpace(status.LastError);
        LastErrorText = status.LastError ?? "";
        LastUpdatedText = Format(status.LastUpdatedUtc);
        LastEventText = Format(status.LastEventAtUtc);
        LastFlushText = Format(status.LastFlushAtUtc);

        EventsReceivedText = status.EventsReceived.ToString("N0", CultureInfo.InvariantCulture);
        EventsAggregatedText = status.EventsAggregated.ToString("N0", CultureInfo.InvariantCulture);
        EventsDroppedText = status.EventsDropped.ToString("N0", CultureInfo.InvariantCulture);
        BatchesWrittenText = status.BatchesWritten.ToString("N0", CultureInfo.InvariantCulture);
        WriteErrorsText = status.WriteErrors.ToString("N0", CultureInfo.InvariantCulture);
        var dropShare = status.EventsReceived <= 0 ? 0 : Math.Clamp((double)status.EventsDropped / status.EventsReceived, 0, 1);
        DropShare = dropShare;
        DropRateText = (dropShare * 100).ToString("0.00", CultureInfo.InvariantCulture) + "%";

        PendingDurableText = status.PendingDurableDeltas.ToString("N0", CultureInfo.InvariantCulture);
        HasPendingDurable = status.PendingDurableDeltas > 0;
        OldestPendingText = Format(status.OldestPendingAtUtc);
        DurableAttemptsText = status.DurableFlushAttempts.ToString("N0", CultureInfo.InvariantCulture);
        NextRetryText = Format(status.NextRetryAtUtc);
        BackoffText = status.RetryBackoffSeconds.ToString("0.0", CultureInfo.InvariantCulture) + " s";

        UsageSourceText = snapshot.UsageDataSource;
        StatusSourceText = snapshot.StatusDataSource;
        SignalText = snapshot.IpcLive
            ? (snapshot.SignalSubscribed ? "Subscribed to D-Bus StatusChanged signals." : "IPC reachable, but status signal subscription is unavailable.")
            : "Not subscribed: daemon IPC unreachable.";
        LastSignalText = snapshot.LastSignalAtUtc is null ? "no signal received" : $"{snapshot.LastSignalAtUtc.Value.ToLocalTime():yyyy-MM-dd HH:mm:ss}";
        ContractText = $"org.flow.Daemon1 · contract v{Flow.Contracts.Ipc.FlowIpc.ContractVersion}";
        ServicePathText = $"{Flow.Contracts.Ipc.FlowIpc.ServiceName} · {Flow.Contracts.Ipc.FlowIpc.ObjectPath}";

        WindowText = $"{snapshot.StartUtc.ToLocalTime():yyyy-MM-dd HH:mm} → {snapshot.EndUtc.ToLocalTime():yyyy-MM-dd HH:mm} local";
        _allBuckets = snapshot.Series.Select(TimeSeriesRow.From).ToList();
        ApplyBuckets();
    }

    private void ApplyBuckets()
    {
        var filtered = _allBuckets.Where(b => RowFilter.Matches($"{b.Bucket} {b.Rx} {b.Tx} {b.Total}", BucketSearchText));
        Buckets.Clear();
        foreach (var bucket in filtered) Buckets.Add(bucket);
        HasNoBuckets = Buckets.Count == 0;
        BucketSummary = $"{Buckets.Count.ToString(CultureInfo.InvariantCulture)} of {_allBuckets.Count.ToString(CultureInfo.InvariantCulture)} persisted 1-minute buckets";
    }

    private static string Format(DateTimeOffset? value)
        => value is null ? "—" : $"{value.Value.ToLocalTime():yyyy-MM-dd HH:mm:ss} local";
}
