using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Flow.Application.Monitoring;
using Flow.Domain.Identity;
using Flow.Domain.Measurement;
using Flow.Domain.Monitoring;
using Flow.Infrastructure.Sqlite;
using Microsoft.Extensions.Logging;

namespace Flow.Infrastructure.Tests;

public sealed class UsageIngestionPipelineSqliteTests
{
    [Fact]
    public async Task PipelinePersistsUsageMetadataAndSessionsToSqlite()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), "flow-tests", $"{Guid.NewGuid():N}.db");
        try
        {
            var factory = new SqliteConnectionFactory(dbPath);
            using (var conn = factory.CreateOpenConnection())
                DbMigrator.Migrate(conn);

            var repo = new UsageRepository(factory);
            var monitor = new ScriptedMonitor();
            var logger = new TestLogger<UsageIngestionPipeline>();
            await using var pipeline = new UsageIngestionPipeline(monitor, repo, logger, metadataStore: repo, flushInterval: TimeSpan.FromMilliseconds(40));

            var t0 = new DateTimeOffset(2026, 11, 15, 14, 0, 10, TimeSpan.Zero);
            var bucket = new DateTimeOffset(2026, 11, 15, 14, 0, 0, TimeSpan.Zero);
            var procStart = new DateTimeOffset(2026, 11, 15, 13, 0, 0, TimeSpan.Zero);
            var proc = new ProcessIdentity(4242, procStart, "/usr/bin/test-app", 1000);
            var app = new ApplicationIdentity("app.test", "Test App", null, "/usr/bin/test-app", null);
            var point = new MeasurementPoint("test-provider", "test-hook", "per-process");

            await pipeline.StartMonitoringAsync();
            await monitor.EmitAsync(new NetworkUsageEvent(t0, proc, app, "eth0", 100, 10, point, true));
            await WaitUntilAsync(() => pipeline.Diagnostics.BatchesWritten >= 1, TimeSpan.FromSeconds(2));

            // Same logical unattributed bucket across a later flush cycle must coalesce in SQLite (NULL-safe key).
            await monitor.EmitAsync(new NetworkUsageEvent(t0.AddSeconds(5), null, null, null, 7, 1, point, false, "unattributed"));
            await WaitUntilAsync(() => pipeline.Diagnostics.BatchesWritten >= 2, TimeSpan.FromSeconds(2));
            await monitor.EmitAsync(new NetworkUsageEvent(t0.AddSeconds(20), null, null, null, 3, 2, point, false, "unattributed"));
            await WaitUntilAsync(() => pipeline.Diagnostics.BatchesWritten >= 3, TimeSpan.FromSeconds(2));

            await pipeline.StopAsync();

            var summary = await repo.GetSummaryAsync(bucket, bucket.AddMinutes(1));
            Assert.Equal(100, summary.RxBytes);
            Assert.Equal(10, summary.TxBytes);
            Assert.Equal(10, summary.UnattributedRxBytes);
            Assert.Equal(3, summary.UnattributedTxBytes);

            using (var conn = factory.CreateOpenConnection())
            {
                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = "SELECT COUNT(*) FROM usage_buckets WHERE bucket_start_utc = $s AND granularity = '1m' AND is_attributed = 0;";
                    cmd.Parameters.AddWithValue("$s", bucket.ToString("O"));
                    Assert.Equal(1L, (long)(cmd.ExecuteScalar() ?? 0L));
                }
                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = "SELECT COUNT(*) FROM applications WHERE application_key = 'app.test';";
                    Assert.Equal(1L, (long)(cmd.ExecuteScalar() ?? 0L));
                }
                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = "SELECT COUNT(*) FROM processes WHERE process_stable_key = $k;";
                    cmd.Parameters.AddWithValue("$k", proc.StableKey);
                    Assert.Equal(1L, (long)(cmd.ExecuteScalar() ?? 0L));
                }
                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = "SELECT COUNT(*) FROM network_interfaces WHERE interface_id = 'eth0';";
                    Assert.Equal(1L, (long)(cmd.ExecuteScalar() ?? 0L));
                }
                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = "SELECT COUNT(*) FROM monitoring_sessions WHERE ended_at_utc IS NOT NULL;";
                    Assert.Equal(1L, (long)(cmd.ExecuteScalar() ?? 0L));
                }
            }

            Assert.Equal(0, pipeline.Diagnostics.WriteErrors);
            Assert.True(pipeline.Diagnostics.EventsReceived >= 3);
        }
        finally
        {
            TryDeleteDb(dbPath);
        }
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var start = DateTime.UtcNow;
        while (!condition())
        {
            if (DateTime.UtcNow - start > timeout)
                throw new TimeoutException("Condition not met in time.");
            await Task.Delay(10);
        }
    }

    private sealed class ScriptedMonitor : INetworkMonitor
    {
        private readonly Channel<NetworkUsageEvent> _channel = Channel.CreateUnbounded<NetworkUsageEvent>();
        public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task StopAsync(CancellationToken cancellationToken) { _channel.Writer.TryComplete(); return Task.CompletedTask; }
        public async Task EmitAsync(NetworkUsageEvent ev)
        {
            await _channel.Writer.WriteAsync(ev).ConfigureAwait(false);
            await Task.Delay(20).ConfigureAwait(false);
        }
        public async IAsyncEnumerable<NetworkUsageEvent> ReadEventsAsync([EnumeratorCancellation] CancellationToken cancellationToken)
        {
            while (await _channel.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
            {
                while (_channel.Reader.TryRead(out var ev))
                    yield return ev;
            }
        }
    }

    private sealed class TestLogger<T> : ILogger<T>
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => false;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) { }
    }

    private static void TryDeleteDb(string dbPath)
    {
        try
        {
            if (File.Exists(dbPath)) File.Delete(dbPath);
            if (File.Exists(dbPath + "-wal")) File.Delete(dbPath + "-wal");
            if (File.Exists(dbPath + "-shm")) File.Delete(dbPath + "-shm");
        }
        catch { }
    }
}
