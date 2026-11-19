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

    [Fact]
    public void ParseRejectsSessionUntilImplemented()
        => Assert.Throws<InvalidOperationException>(() => DaemonModeParser.Parse("session"));

    [Fact]
    public void ParseRejectsUnknownMode()
        => Assert.Throws<InvalidOperationException>(() => DaemonModeParser.Parse("bogus"));
}
