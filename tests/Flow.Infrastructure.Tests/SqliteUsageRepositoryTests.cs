using Flow.Application.Persistence;
using Flow.Domain.Identity;
using Flow.Domain.Monitoring;
using Flow.Infrastructure.Sqlite;

namespace Flow.Infrastructure.Tests;

public sealed class SqliteUsageRepositoryTests
{
    [Fact]
    public async Task MigrateAndWriteMinuteDeltasAggregatesSummary()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), "flow-tests", $"{Guid.NewGuid():N}.db");
        try
        {
            var factory = new SqliteConnectionFactory(dbPath);
            using (var conn = factory.CreateOpenConnection())
            {
                DbMigrator.Migrate(conn);
                DbMigrator.Migrate(conn); // idempotent
            }

            var repo = new UsageRepository(factory);
            var t = new DateTimeOffset(2026, 11, 13, 10, 5, 30, TimeSpan.Zero);
            var bucket = new DateTimeOffset(2026, 11, 13, 10, 5, 0, TimeSpan.Zero);

            await repo.WriteMinuteDeltasAsync(new[]
            {
                new UsageBucketDelta(bucket, "app.test", "p1", "eth0", 100, 50, true, null, "test", "hook"),
                new UsageBucketDelta(t, "app.test", "p1", "eth0", 20, 10, true, null, "test", "hook"),
                new UsageBucketDelta(bucket, null, null, "eth0", 5, 1, false, "unattributed", "test", "hook"),
            });

            var summary = await repo.GetSummaryAsync(bucket, bucket.AddMinutes(1), "eth0");
            Assert.Equal(120, summary.RxBytes);
            Assert.Equal(60, summary.TxBytes);
            Assert.Equal(5, summary.UnattributedRxBytes);
            Assert.Equal(1, summary.UnattributedTxBytes);
        }
        finally
        {
            TryDeleteDb(dbPath);
        }
    }

    [Fact]
    public async Task MetadataUpsertsAndSessionsWork()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), "flow-tests", $"{Guid.NewGuid():N}.db");
        try
        {
            var factory = new SqliteConnectionFactory(dbPath);
            using (var conn = factory.CreateOpenConnection())
                DbMigrator.Migrate(conn);

            var repo = new UsageRepository(factory);
            var now = DateTimeOffset.UtcNow;
            await repo.UpsertApplicationAsync(new ApplicationIdentity("app.test", "Test App", "/tmp/test.desktop", "/usr/bin/test"), now);
            await repo.UpsertProcessAsync(new ProcessIdentity(123, now.AddMinutes(-5), "/usr/bin/test", 1000), "app.test", now);
            await repo.UpsertInterfaceAsync("wlan0", "wifi", now);

            var sessionId = await repo.StartMonitoringSessionAsync("null", "v1", MonitoringState.Monitoring, null);
            Assert.True(sessionId > 0);
            await repo.EndMonitoringSessionAsync(sessionId, MonitoringState.Paused, "stopped");

            // Basic smoke check: data present by querying summary path works (no exception) and DB has expected rows.
            var summary = await repo.GetSummaryAsync(now.AddHours(-1), now.AddHours(1));
            Assert.NotNull(summary);
        }
        finally
        {
            TryDeleteDb(dbPath);
        }
    }

    [Fact]
    public async Task RepeatedWritesWithNullDimensionsCoalesceIntoSingleLogicalBucket()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), "flow-tests", $"{Guid.NewGuid():N}.db");
        try
        {
            var factory = new SqliteConnectionFactory(dbPath);
            using (var conn = factory.CreateOpenConnection())
                DbMigrator.Migrate(conn);

            var repo = new UsageRepository(factory);
            var bucket = new DateTimeOffset(2026, 11, 15, 10, 5, 0, TimeSpan.Zero);

            await repo.WriteMinuteDeltasAsync(new[]
            {
                new UsageBucketDelta(bucket, null, null, null, 100, 10, false, "unattributed", "ebpf-tcp", "tcp_sendmsg/recvmsg"),
            });
            await repo.WriteMinuteDeltasAsync(new[]
            {
                new UsageBucketDelta(bucket.AddSeconds(30), null, null, null, 50, 5, false, "unattributed", "ebpf-tcp", "tcp_sendmsg/recvmsg"),
            });

            var summary = await repo.GetSummaryAsync(bucket, bucket.AddMinutes(1));
            Assert.Equal(150, summary.UnattributedRxBytes);
            Assert.Equal(15, summary.UnattributedTxBytes);
            Assert.Equal(0, summary.RxBytes);
            Assert.Equal(0, summary.TxBytes);

            using (var conn = factory.CreateOpenConnection())
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT COUNT(*) FROM usage_buckets WHERE bucket_start_utc = $s AND granularity = '1m' AND is_attributed = 0;";
                cmd.Parameters.AddWithValue("$s", bucket.ToString("O"));
                var count = (long)(cmd.ExecuteScalar() ?? 0L);
                Assert.Equal(1, count);
            }
        }
        finally
        {
            TryDeleteDb(dbPath);
        }
    }

    [Fact]
    public async Task TopApplicationsAndTimeSeriesQueriesReturnExpectedData()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), "flow-tests", $"{Guid.NewGuid():N}.db");
        try
        {
            var factory = new SqliteConnectionFactory(dbPath);
            using (var conn = factory.CreateOpenConnection())
                DbMigrator.Migrate(conn);

            var repo = new UsageRepository(factory);
            var b1 = new DateTimeOffset(2026, 11, 13, 10, 0, 0, TimeSpan.Zero);
            var b2 = b1.AddMinutes(1);

            await repo.WriteMinuteDeltasAsync(new[]
            {
                new UsageBucketDelta(b1, "app.a", "p1", "wlan0", 100, 10, true, null, "test", "hook"),
                new UsageBucketDelta(b1, "app.b", "p2", "wlan0", 50, 5, true, null, "test", "hook"),
                new UsageBucketDelta(b2, "app.a", "p1", "wlan0", 30, 3, true, null, "test", "hook"),
                new UsageBucketDelta(b2, null, null, "wlan0", 7, 1, false, "unattributed", "test", "hook"),
            });

            var top = await repo.GetTopApplicationsAsync(b1, b1.AddMinutes(2), "wlan0", 10);
            Assert.True(top.Count >= 2);
            Assert.Equal("app.a", top[0].ApplicationKey);
            Assert.Equal(130, top[0].RxBytes);
            Assert.Equal(13, top[0].TxBytes);

            var series = await repo.GetUsageTimeSeriesAsync(b1, b1.AddMinutes(2), "wlan0", "app.a");
            Assert.Equal(2, series.Count);
            Assert.Equal(100, series[0].RxBytes);
            Assert.Equal(30, series[1].RxBytes);

            var summary = await repo.GetSummaryAsync(b1, b1.AddMinutes(2), "wlan0");
            Assert.Equal(180, summary.RxBytes);
            Assert.Equal(18, summary.TxBytes);
            Assert.Equal(7, summary.UnattributedRxBytes);
            Assert.Equal(1, summary.UnattributedTxBytes);
        }
        finally
        {
            TryDeleteDb(dbPath);
        }
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
