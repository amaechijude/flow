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
    /// <summary>
    /// Stable key for a process generation. Deliberately excludes executable path and
    /// other enrichment-only metadata: those may become known later (or differ between
    /// snapshots) and must not split one process generation into multiple identities.
    /// PID reuse is disambiguated by the event-time start time; network namespace id
    /// prevents identical (pid, start, uid) triples in different netns from merging.
    /// Missing netns is rendered as "-".
    /// </summary>
    public string StableKey => FormattableString.Invariant(
        $"{Pid}:{StartTimeUtc.ToUnixTimeMilliseconds()}:{(Uid.HasValue ? Uid.Value.ToString(System.Globalization.CultureInfo.InvariantCulture) : "-")}:{NetworkNamespaceId ?? "-"}");
}
