using Flow.Domain.Measurement;
using Flow.Linux.Monitoring;

namespace Flow.Linux.Tests;

public class EbpfSnapshotParserTests
{
    private const long BootUnixMs = 1_700_000_000_000;
    private const long ClkTck = 100;

    private static readonly MeasurementPoint Point = new("test", "test-hook", "test");

    [Fact]
    public void ParseEntryUsesEventTimeStartAndCgroup()
    {
        const ulong startNs = 123_456_789_000; // 123456.789 ms since boot
        var line = $"E\t4242\t1000\t9000\t8000\t900\t800\tfirefox\t/usr/lib/firefox/firefox\t12345\t1\t{startNs}\t777";

        var ev = EbpfSnapshotParser.ParseEntry(line, Point, ClkTck, BootUnixMs);

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
        var first = EbpfSnapshotParser.ParseEntry(
            "E\t500\t1000\t100\t0\t100\t0\told\t/usr/bin/old\t0\t0\t1000000000\t7",
            Point, ClkTck, BootUnixMs);
        var reused = EbpfSnapshotParser.ParseEntry(
            "E\t500\t1000\t50\t0\t50\t0\tnew\t/usr/bin/new\t0\t1\t2000000000\t8",
            Point, ClkTck, BootUnixMs);

        Assert.NotNull(first);
        Assert.NotNull(reused);
        Assert.Equal(first!.Process!.Pid, reused!.Process!.Pid);
        Assert.NotEqual(first.Process.StableKey, reused.Process.StableKey);
        Assert.NotEqual(first.Process.StartTimeUtc, reused.Process.StartTimeUtc);
    }

    [Fact]
    public void ParseEntryNetnsParticipatesInStableKey()
    {
        var inRoot = EbpfSnapshotParser.ParseEntry(
            "E\t501\t1000\t100\t0\t100\t0\tsame\t/usr/bin/same\t0\t1\t1000000000\t7\t1\tlo\t4026531833",
            Point, ClkTck, BootUnixMs);
        var inContainer = EbpfSnapshotParser.ParseEntry(
            "E\t501\t1000\t100\t0\t100\t0\tsame\t/usr/bin/same\t0\t1\t1000000000\t7\t1\tlo\t4026532000",
            Point, ClkTck, BootUnixMs);

        Assert.NotNull(inRoot);
        Assert.NotNull(inContainer);
        Assert.Equal("4026531833", inRoot!.Process!.NetworkNamespaceId);
        Assert.Equal("4026532000", inContainer!.Process!.NetworkNamespaceId);
        Assert.NotEqual(inRoot.Process.StableKey, inContainer.Process.StableKey);
    }

    [Fact]
    public void LegacyLineHasNoNetnsInStableKey()
    {
        var ev = EbpfSnapshotParser.ParseEntry(
            "E\t502\t1000\t100\t0\t100\t0\tlegacy\t/usr/bin/legacy\t0\t1\t1000000000\t7",
            Point, ClkTck, BootUnixMs);

        Assert.NotNull(ev);
        Assert.Null(ev!.Process!.NetworkNamespaceId);
        Assert.EndsWith(":-", ev.Process.StableKey);
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

        var ev = EbpfSnapshotParser.ParseEntry(line, Point, ClkTck, BootUnixMs);

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

        var ev = EbpfSnapshotParser.ParseEntry(line, Point, ClkTck, BootUnixMs);

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

        var ev = EbpfSnapshotParser.ParseEntry(line, Point, ClkTck, BootUnixMs);

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
        Assert.Null(EbpfSnapshotParser.ParseEntry(line, Point, ClkTck, BootUnixMs));
    }

    [Fact]
    public void ParseEntryCarriesResolvedInterfaceAndProtocol()
    {
        var line = "E\t1100\t1000\t4000\t3000\t400\t300\tcurl\t/usr/bin/curl\t1000\t1\t2000000000\t99\t1\tlo";

        var ev = EbpfSnapshotParser.ParseEntry(line, Point, ClkTck, BootUnixMs);

        Assert.NotNull(ev);
        Assert.Equal("lo", ev!.InterfaceName);
        Assert.Equal(400, ev.TxBytesDelta);
        Assert.Equal(300, ev.RxBytesDelta);
        Assert.Null(ev.AttributionNote);
    }

    [Fact]
    public void ParseEntryUdpWithUnresolvedInterfaceKeepsBytesAndExplainsGap()
    {
        var line = "E\t1200\t1000\t500\t700\t500\t700\tquic-client\t/usr/bin/quic-client\t1000\t1\t3000000000\t123\t2\t-";

        var ev = EbpfSnapshotParser.ParseEntry(line, Point, ClkTck, BootUnixMs);

        Assert.NotNull(ev);
        Assert.Null(ev!.InterfaceName);
        Assert.Equal(500, ev.TxBytesDelta);
        Assert.Equal(700, ev.RxBytesDelta);
        Assert.NotNull(ev.AttributionNote);
        Assert.Contains("interface could not be resolved", ev.AttributionNote);
    }

    [Fact]
    public void ParseEntryEventTimeLineWithoutProtocolOrInterfaceRemainsCompatible()
    {
        // Loader after event-time identity but before UDP/interface fields (13 fields).
        var line = "E\t1300\t1000\t100\t200\t100\t200\tlegacy\t/usr/bin/legacy\t1000\t1\t4000000000\t55";

        var ev = EbpfSnapshotParser.ParseEntry(line, Point, ClkTck, BootUnixMs);

        Assert.NotNull(ev);
        Assert.Null(ev!.InterfaceName);
        Assert.Null(ev.AttributionNote);
        Assert.Equal("55", ev.Process!.CgroupId);
    }

    [Fact]
    public void ParseEntryMarksNamespaceScopedInterface()
    {
        var line = "E\t1400\t1000\t100\t200\t100\t200\tclient\t/usr/bin/client\t1000\t1\t5000000000\t55\t2\t7@4026532000\t4026532000";

        var ev = EbpfSnapshotParser.ParseEntry(line, Point, ClkTck, BootUnixMs);

        Assert.NotNull(ev);
        Assert.Equal("7@4026532000", ev!.InterfaceName);
        Assert.NotNull(ev.AttributionNote);
        Assert.Contains("different network namespace", ev.AttributionNote);
    }

    [Fact]
    public void ParseOverflowReturnsUnattributedEventWithNote()
    {
        var ev = EbpfSnapshotParser.ParseOverflow("F\t12\t7168\t7168\t1024\t1024\t1\t1", Point);

        Assert.NotNull(ev);
        Assert.False(ev!.IsAttributed);
        Assert.Null(ev.Process);
        Assert.Equal(1024, ev.TxBytesDelta);
        Assert.Equal(1024, ev.RxBytesDelta);
        Assert.NotNull(ev.AttributionNote);
        Assert.Contains("map was full", ev.AttributionNote);
    }

    [Fact]
    public void ParseOverflowZeroDeltaReturnsNull()
    {
        Assert.Null(EbpfSnapshotParser.ParseOverflow("F\t12\t0\t0\t0\t0\t0\t0", Point));
    }

    [Fact]
    public void ParseUnattributedReturnsExplicitUnattributedEvent()
    {
        var ev = EbpfSnapshotParser.ParseUnattributed("U\t100\t200\t10\t20\t1\t2", Point);

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
        Assert.Null(EbpfSnapshotParser.ParseUnattributed("U\t100\t200\t0\t0\t1\t2", Point));
    }

    [Fact]
    public void ParseMetaReadsClockAndBootTimeWithSafeDefaults()
    {
        EbpfSnapshotParser.ParseMeta("M\t250\t1700000000000", out var tck, out var boot);
        Assert.Equal(250, tck);
        Assert.Equal(1_700_000_000_000, boot);

        EbpfSnapshotParser.ParseMeta("M\tgarbage", out tck, out boot);
        Assert.Equal(100, tck);
        Assert.Equal(0, boot);
    }
}
