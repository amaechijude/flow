using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json;
using Flow.Contracts.Ipc;
using Flow.Contracts.Monitoring;
using Flow.Contracts.Usage;

namespace Flow.Desktop.ViewModels;

public sealed record UsageExportRange(DateTimeOffset StartUtc, DateTimeOffset EndUtc, string Label);
public sealed record UsageExportCaveats(
    string Attribution,
    string Measurement,
    string Identity,
    string Coverage);
public sealed record UsageExportStatus(MonitoringStatusDto DaemonStatus, string UsageDataSource, string StatusDataSource);
public sealed record UsageExportDocument(
    string Schema,
    DateTimeOffset GeneratedUtc,
    UsageExportRange Range,
    string? InterfaceFilter,
    UsageExportCaveats Caveats,
    UsageSummaryDto Summary,
    IReadOnlyList<ApplicationUsageDto> Applications,
    IReadOnlyList<ProcessUsageDto> Processes,
    IReadOnlyList<InterfaceUsageDto> Interfaces,
    IReadOnlyList<UsageTimePointDto> TimeSeries,
    UsageExportStatus Status);

/// <summary>
/// Builds export documents from a real <see cref="UsageSnapshot"/>. Values are never
/// synthesized; only what the daemon/local database returned is written.
/// </summary>
public static class UsageExport
{
    private static readonly JsonSerializerOptions JsonOptions = new(IpcJson.Options) { WriteIndented = true };

    public static UsageExportDocument BuildDocument(UsageSnapshot snapshot) => new(
        "flow.usage-export.v1",
        snapshot.GeneratedAtUtc,
        new UsageExportRange(snapshot.StartUtc, snapshot.EndUtc, snapshot.RangeLabel),
        snapshot.InterfaceId,
        new UsageExportCaveats(
            "Per-process attribution depends on the active provider/measurement point. Unattributed bytes are separate and must not be treated as zero.",
            "procfs reports interface-level counters without process attribution; ebpf-tcp counts TCP send/recv return bytes only and currently lacks per-interface mapping.",
            "Process identity uses stable keys derived from PID start time where available; PID-reuse/event-time lifecycle hardening is still in progress.",
            "UDP, namespace, VPN, and virtual-interface coverage is provider-dependent and not yet fully validated."),
        snapshot.Summary,
        snapshot.Applications,
        snapshot.Processes,
        snapshot.Interfaces,
        snapshot.Series,
        new UsageExportStatus(snapshot.Status, snapshot.UsageDataSource, snapshot.StatusDataSource));

    public static string ToJson(UsageSnapshot snapshot) => JsonSerializer.Serialize(BuildDocument(snapshot), JsonOptions);

    public static string ToCsv(UsageSnapshot snapshot)
    {
        var sb = new StringBuilder();
        var c = CultureInfo.InvariantCulture;
        sb.AppendLine("# Flow usage export (real persisted data)");
        sb.AppendLine(c, $"# Schema,{Escape("flow.usage-export.v1")}");
        sb.AppendLine(c, $"# GeneratedUtc,{snapshot.GeneratedAtUtc:O}");
        sb.AppendLine(c, $"# RangeLabel,{Escape(snapshot.RangeLabel)}");
        sb.AppendLine(c, $"# StartUtc,{snapshot.StartUtc:O}");
        sb.AppendLine(c, $"# EndUtc,{snapshot.EndUtc:O}");
        sb.AppendLine(c, $"# InterfaceFilter,{Escape(snapshot.InterfaceId ?? "(all)")}");
        sb.AppendLine(c, $"# UsageDataSource,{Escape(snapshot.UsageDataSource)}");
        sb.AppendLine(c, $"# StatusDataSource,{Escape(snapshot.StatusDataSource)}");
        sb.AppendLine(c, $"# DaemonState,{snapshot.Status.State}");
        sb.AppendLine(c, $"# Provider,{Escape(snapshot.Status.ProviderName ?? "n/a")}");
        sb.AppendLine("# Caveats,Per-process attribution depends on provider/measurement point; unattributed traffic is separate; procfs is interface-level/unattributed; ebpf-tcp is TCP-only and may lack per-interface mapping; process identity/lifecycle hardening ongoing.");
        sb.AppendLine();
        sb.AppendLine("section,summary");
        sb.AppendLine("metric,rx_bytes,tx_bytes");
        sb.AppendLine(c, $"attributed,{snapshot.Summary.RxBytes},{snapshot.Summary.TxBytes}");
        sb.AppendLine(c, $"unattributed,{snapshot.Summary.UnattributedRxBytes},{snapshot.Summary.UnattributedTxBytes}");
        sb.AppendLine(c, $"total,{snapshot.Summary.RxBytes + snapshot.Summary.UnattributedRxBytes},{snapshot.Summary.TxBytes + snapshot.Summary.UnattributedTxBytes}");
        sb.AppendLine();
        sb.AppendLine("section,applications");
        sb.AppendLine("application_key,display_name,attributed_rx,attributed_tx,unattributed_rx,unattributed_tx");
        foreach (var a in snapshot.Applications)
            sb.AppendLine(c, $"{Escape(a.ApplicationKey)},{Escape(a.DisplayName)},{a.RxBytes},{a.TxBytes},{a.UnattributedRxBytes},{a.UnattributedTxBytes}");
        sb.AppendLine();
        sb.AppendLine("section,processes");
        sb.AppendLine("process_stable_key,application_key,display_name,executable_path,attributed_rx,attributed_tx,unattributed_rx,unattributed_tx");
        foreach (var p in snapshot.Processes)
            sb.AppendLine(c, $"{Escape(p.ProcessStableKey)},{Escape(p.ApplicationKey)},{Escape(p.DisplayName)},{Escape(p.ExecutablePath)},{p.RxBytes},{p.TxBytes},{p.UnattributedRxBytes},{p.UnattributedTxBytes}");
        sb.AppendLine();
        sb.AppendLine("section,interfaces");
        sb.AppendLine("interface_key,interface_name,interface_type,attributed_rx,attributed_tx,unattributed_rx,unattributed_tx");
        foreach (var i in snapshot.Interfaces)
            sb.AppendLine(c, $"{Escape(i.InterfaceKey)},{Escape(i.InterfaceName)},{Escape(i.InterfaceType)},{i.RxBytes},{i.TxBytes},{i.UnattributedRxBytes},{i.UnattributedTxBytes}");
        sb.AppendLine();
        sb.AppendLine("section,time_series_1m");
        sb.AppendLine("bucket_start_utc,rx_bytes,tx_bytes");
        foreach (var p in snapshot.Series)
            sb.AppendLine(c, $"{p.BucketStartUtc:O},{p.RxBytes},{p.TxBytes}");
        return sb.ToString();
    }

    public static string SuggestedFileName(string extension, DateTimeOffset now)
        => $"flow-usage-{now:yyyyMMdd-HHmmss}.{extension}";

    private static string Escape(string? value)
    {
        if (string.IsNullOrEmpty(value)) return "";
        if (value.Contains(',') || value.Contains('"') || value.Contains('\n') || value.Contains('\r'))
            return "\"" + value.Replace("\"", "\"\"") + "\"";
        return value;
    }
}
