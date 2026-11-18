using Flow.Linux.Monitoring;

namespace Flow.Linux.Tests;

public class EbpfMonitorTests
{
    [Fact]
    public async Task LoaderFailureSurfacesStderrInsteadOfSilentCompletion()
    {
        var script = Path.Combine(Path.GetTempPath(), $"flow-fake-loader-{Guid.NewGuid():N}.sh");
        await File.WriteAllTextAsync(script, "#!/bin/sh\necho 'failed to attach test hook' >&2\nexit 3\n");
        File.SetUnixFileMode(
            script,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        try
        {
            using var monitor = new EbpfMonitor(new EbpfMonitorOptions
            {
                LoaderPath = script,
                IntervalSeconds = 1,
                Rounds = 1,
            });

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            {
                await foreach (var _ in monitor.ReadEventsAsync(CancellationToken.None))
                {
                }
            });

            Assert.Contains("code 3", ex.Message);
            Assert.Contains("failed to attach test hook", ex.Message);
        }
        finally
        {
            File.Delete(script);
        }
    }
}
