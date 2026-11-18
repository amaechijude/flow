using Flow.Domain.Measurement;
using Flow.Linux.Monitoring;

namespace Flow.Linux.Tests;

public class EbpfTcpSnapshotParserTests
{
    private const long BootUnixMs = 1_700_000_000_000;
    private const long ClkTck = 100;

    private static readonly MeasurementPoint Point = new("test", "test-hook", "test");

    [Fact]
    public void ParseEntryUsesEventTimeStartAndCgroup()
    {
        const ulong startNs = 123_456_789_000; // 123456.789 ms since boot
        var line = $"E\t4242\t1000\t9000\t8000\t900\t800\tfirefox\t/usr/lib/firefox/firefox\t12345\t1\t{startNs}\t777";

        var ev = EbpfTcpSnapshotParser.ParseEntry(line, Point, ClkTck, BootUnixMs);

        Assert.NotNull(ev);
        Assert.True(ev!.IsAttributed);
        Assert.Equal(900, ev.TxBytesDelta);
        Assert.Equal(800, ev.RxBytesDelta);
        Assert.Equal("firefox", ev.ApplicationHint!.DisplayName);
        Assert.Equal("/usr/lib/firefox/firefox", ev.ApplicationHint.ApplicationKey);
        Assert.Equal("777", ev.Process!.CgroupId);
        Assert.Null(ev.AttributionNote);

        var expected = DateTimeOffset.FromUnixTimeMilliseconds(BootUnixMs).AddMilliseconds(123_456.789);
        Assert.Equal(expected, ev.Process.StartTimeUtc);
    }

    [Fact]
    public void ParseEntryPidReuseProducesDistinctStableKeys()
    {
        var first = EbpfTcpSnapshotParser.ParseEntry(
            "E\t500\t1000\t100\t0\t100\t0\told\t/usr/bin/old\t0\t0\t1000000000\t7",
            Point, ClkTck, BootUnixMs);
        var reused = EbpfTcpSnapshotParser.ParseEntry(
            "E\t500\t1000\t50\t0\t50\t0\tnew\t/usr/bin/new\t0\t1\t2000000000\t8",
            Point, ClkTck, BootUnixMs);

        Assert.NotNull(first);
        Assert.NotNull(reused);
        Assert.Equal(first!.Process!.Pid, reused!.Process!.Pid);
        Assert.NotEqual(first.Process.StableKey, reused.Process.StableKey);
        Assert.NotEqual(first.Process.StartTimeUtc, reused.Process.StartTimeUtc);
    }

    [Fact]
    public void StableKeyIgnoresMutableEnrichmentMetadata()
    {
        var start = DateTimeOffset.FromUnixTimeMilliseconds(BootUnixMs);
        var exited = new Flow.Domain.Identity.ProcessIdentity(600, start, null, 1000);
        var enriched = new Flow.Domain.Identity.ProcessIdentity(600, start, "/usr/bin/tool", 1000);

        Assert.Equal(exited.StableKey, enriched.StableKey);
    }

    [Fact]
    public void ParseEntryPreEventTimeLineRemainsCompatible()
    {
        // Old loader: no start_ns / cgroup_id fields.
        var line = "E\t700\t1000\t500\t400\t500\t400\tlegacy\t/usr/bin/legacy\t1000\t1";

        var ev = EbpfTcpSnapshotParser.ParseEntry(line, Point, ClkTck, BootUnixMs);

        Assert.NotNull(ev);
        Assert.Null(ev!.Process!.CgroupId);
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(BootUnixMs).AddMilliseconds(10_000), ev.Process.StartTimeUtc);
        Assert.Null(ev.AttributionNote);
    }

    [Fact]
    public void ParseEntryUnverifiedIdentityKeepsEventTimeStartAndCommFallback()
    {
        // Process exited before snapshot: no /proc enrichment (identity_verified=0, exe "-").
        var line = "E\t800\t1000\t300\t200\t300\t200\tshort-lived\t-\t0\t0\t5000000000\t0";

        var ev = EbpfTcpSnapshotParser.ParseEntry(line, Point, ClkTck, BootUnixMs);

        Assert.NotNull(ev);
        Assert.Null(ev!.Process!.ExecutablePath);
        Assert.Equal("comm:short-lived", ev.ApplicationHint!.ApplicationKey);
        Assert.Equal("short-lived", ev.ApplicationHint.DisplayName);
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(BootUnixMs).AddMilliseconds(5_000), ev.Process.StartTimeUtc);
        Assert.NotNull(ev.AttributionNote);
        Assert.Contains("not verified", ev.AttributionNote);
        Assert.Contains("comm", ev.AttributionNote);
    }

    [Fact]
    public void ParseEntryPreEventTimeUnverifiedExplainsCachedIdentityFallback()
    {
        var line = "E\t900\t1000\t10\t10\t10\t10\t?\t-\t0\t0";

        var ev = EbpfTcpSnapshotParser.ParseEntry(line, Point, ClkTck, BootUnixMs);

        Assert.NotNull(ev);
        Assert.Null(ev!.Process!.ExecutablePath);
        Assert.Null(ev.ApplicationHint);
        Assert.NotNull(ev.AttributionNote);
        Assert.Contains("pre-event-time loader", ev.AttributionNote);
    }

    [Theory]
    [InlineData("E\t1\t2")] // too few fields
    [InlineData("E\tx\t1000\t1\t1\t1\t1\tc\te\t0\t1")]
    [InlineData("E\t123\t1000\t1\t1\t0\t0\tc\te\t0\t1")] // no positive delta
    public void ParseEntryInvalidOrEmptyReturnsNull(string line)
    {
        Assert.Null(EbpfTcpSnapshotParser.ParseEntry(line, Point, ClkTck, BootUnixMs));
    }

    [Fact]
    public void ParseUnattributedReturnsExplicitUnattributedEvent()
    {
        var ev = EbpfTcpSnapshotParser.ParseUnattributed("U\t100\t200\t10\t20\t1\t2", Point);

        Assert.NotNull(ev);
        Assert.False(ev!.IsAttributed);
        Assert.Null(ev.Process);
        Assert.Null(ev.ApplicationHint);
        Assert.Equal(10, ev.TxBytesDelta);
        Assert.Equal(20, ev.RxBytesDelta);
        Assert.NotNull(ev.AttributionNote);
    }

    [Fact]
    public void ParseUnattributedNoDeltaReturnsNull()
    {
        Assert.Null(EbpfTcpSnapshotParser.ParseUnattributed("U\t100\t200\t0\t0\t1\t2", Point));
    }

    [Fact]
    public void ParseMetaReadsClockAndBootTimeWithSafeDefaults()
    {
        EbpfTcpSnapshotParser.ParseMeta("M\t250\t1700000000000", out var tck, out var boot);
        Assert.Equal(250, tck);
        Assert.Equal(1_700_000_000_000, boot);

        EbpfTcpSnapshotParser.ParseMeta("M\tgarbage", out tck, out boot);
        Assert.Equal(100, tck);
        Assert.Equal(0, boot);
    }
}
