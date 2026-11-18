using System;

namespace Flow.Desktop.Services;

/// <summary>
/// Notifies subscribers when the daemon publishes a status change over IPC.
/// Implementations must not synthesize change notifications without a real signal.
/// </summary>
public interface IDaemonChangeNotifier
{
    event EventHandler<DaemonStatusChangedEventArgs>? StatusChanged;
    bool IsSignalSubscribed { get; }
    DateTimeOffset? LastSignalAtUtc { get; }
}
