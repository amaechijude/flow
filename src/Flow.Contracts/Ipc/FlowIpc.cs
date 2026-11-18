namespace Flow.Contracts.Ipc;

public static class FlowIpc
{
    public const string ServiceName = "org.flow.Daemon";
    public const string ObjectPath = "/org/flow/Daemon";
    public const string InterfaceName = "org.flow.Daemon1";
    public const int ContractVersion = 3;

    /// <summary>
    /// D-Bus signal emitted when the daemon status snapshot changes. The payload is
    /// a JSON-serialized <see cref="StatusChangedSignalDto"/>.
    /// </summary>
    public const string StatusChangedSignal = "StatusChanged";
}
