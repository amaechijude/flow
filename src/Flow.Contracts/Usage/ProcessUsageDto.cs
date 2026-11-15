namespace Flow.Contracts.Usage;

public sealed record ProcessUsageDto(
    string ProcessStableKey,
    string? ApplicationKey,
    string? DisplayName,
    string? ExecutablePath,
    long RxBytes,
    long TxBytes,
    long UnattributedRxBytes,
    long UnattributedTxBytes);
