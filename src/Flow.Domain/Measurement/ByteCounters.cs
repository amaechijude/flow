namespace Flow.Domain.Measurement;

public readonly record struct ByteCounters(long RxBytes, long TxBytes)
{
    public static ByteCounters Zero => new(0, 0);
    public ByteCounters Add(ByteCounters other) => new(
        checked(RxBytes + other.RxBytes),
        checked(TxBytes + other.TxBytes));
}
