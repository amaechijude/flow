namespace Flow.Contracts.Usage;

public sealed record UsageSummaryDto(
    DateTimeOffset StartUtc,
    DateTimeOffset EndUtc,
    string? InterfaceName,
    long RxBytes,
    long TxBytes,
    long UnattributedRxBytes,
    long UnattributedTxBytes);
