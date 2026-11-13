namespace Flow.Domain.Measurement;

/// <summary>
/// Documents where/how bytes were observed. Different points yield different totals.
/// </summary>
public sealed record MeasurementPoint(
    string Provider,
    string Hook,
    string Description);
