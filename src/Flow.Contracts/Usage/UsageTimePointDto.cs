namespace Flow.Contracts.Usage;

public sealed record UsageTimePointDto(
    DateTimeOffset BucketStartUtc,
    long RxBytes,
    long TxBytes);
