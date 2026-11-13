namespace Flow.Domain.Identity;

/// <summary>
/// Stable process identity inputs. PID alone is not stable due to reuse.
/// StartTimeUtc is the preferred disambiguator (derived from process start ticks / boot time).
/// </summary>
public sealed record ProcessIdentity(
    int Pid,
    DateTimeOffset StartTimeUtc,
    string? ExecutablePath,
    uint? Uid,
    string? CgroupId = null,
    string? NetworkNamespaceId = null)
{
    public string StableKey => FormattableString.Invariant(
        $"{Pid}:{StartTimeUtc.ToUnixTimeMilliseconds()}:{(Uid.HasValue ? Uid.Value.ToString(System.Globalization.CultureInfo.InvariantCulture) : "-")}:{ExecutablePath ?? "-"}");
}
