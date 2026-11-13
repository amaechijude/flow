namespace Flow.Contracts.Usage;

public sealed record ApplicationUsageDto(
    string ApplicationKey,
    string? DisplayName,
    long RxBytes,
    long TxBytes,
    long UnattributedRxBytes,
    long UnattributedTxBytes);
