namespace Flow.Contracts.Usage;

public sealed record InterfaceUsageDto(
    string InterfaceKey,
    string? InterfaceName,
    string? InterfaceType,
    long RxBytes,
    long TxBytes,
    long UnattributedRxBytes,
    long UnattributedTxBytes);
