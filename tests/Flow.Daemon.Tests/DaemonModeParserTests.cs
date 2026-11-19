using Flow.Daemon;

namespace Flow.Daemon.Tests;

public class DaemonModeParserTests
{
    [Theory]
    [InlineData(null, DaemonMode.Full)]
    [InlineData("", DaemonMode.Full)]
    [InlineData("full", DaemonMode.Full)]
    [InlineData("DEVELOPMENT", DaemonMode.Full)]
    [InlineData("collector", DaemonMode.Collector)]
    [InlineData(" HEADLESS ", DaemonMode.Collector)]
    public void ParseRecognisesSupportedModes(string? value, DaemonMode expected)
        => Assert.Equal(expected, DaemonModeParser.Parse(value));

    [Theory]
    [InlineData("session", DaemonMode.Session)]
    [InlineData("UI", DaemonMode.Session)]
    public void ParseRecognisesSessionMode(string value, DaemonMode expected)
        => Assert.Equal(expected, DaemonModeParser.Parse(value));

    [Fact]
    public void ParseRejectsUnknownMode()
        => Assert.Throws<InvalidOperationException>(() => DaemonModeParser.Parse("bogus"));

    [Theory]
    [InlineData(DaemonMode.Collector)]
    [InlineData(DaemonMode.Session)]
    public void PackagedModesUseSystemDatabase(DaemonMode mode)
        => Assert.Equal(FlowPaths.SystemDatabasePath, FlowPaths.DefaultDatabasePath(mode));

    [Fact]
    public void FullModeUsesPerUserDatabase()
        => Assert.EndsWith(Path.Combine("flow", "flow.db"), FlowPaths.DefaultDatabasePath(DaemonMode.Full));
}
