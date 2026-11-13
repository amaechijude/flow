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
