using Flow.Application.Persistence;
using Flow.Infrastructure.Sqlite;
using Microsoft.Data.Sqlite;

namespace Flow.Infrastructure.Tests;

public sealed class UsageRetentionAndQueryTests
{
    [Fact]
    public async Task LongRangeQueryUsesRollupsAndSurvivesFineGrainedRetention()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), "flow-tests", $"{Guid.NewGuid():N}.db");
        try
        {
            var factory = new SqliteConnectionFactory(dbPath);
            using (var conn = factory.CreateOpenConnection())
                DbMigrator.Migrate(conn);

            var repo = new UsageRepository(factory);
            var rollups = new UsageRollupService(factory);
            var retention = new UsageRetentionService(factory);
            var day1 = new DateTimeOffset(2026, 11, 10, 10, 1, 0, TimeSpan.Zero);
            var day2 = new DateTimeOffset(2026, 11, 11, 11, 1, 0, TimeSpan.Zero);
            var start = new DateTimeOffset(2026, 11, 10, 0, 0, 0, TimeSpan.Zero);
            var end = new DateTimeOffset(2026, 11, 13, 0, 0, 0, TimeSpan.Zero);

            await repo.WriteMinuteDeltasAsync(new[]
            {
                new UsageBucketDelta(day1, "app.test", "p1", "eth0", 100, 10, true, null, "test", "hook"),
                new UsageBucketDelta(day2, "app.test", "p1", "eth0", 200, 20, true, null, "test", "hook"),
            });

            await rollups.RebuildThroughAsync(end);
            Assert.True(Count(factory, "1h") > 0);
            var summary = await repo.GetSummaryAsync(start, end);
            Assert.Equal(300, summary.RxBytes);
            Assert.Equal(30, summary.TxBytes);

            var deleted = await retention.ApplyAsync(new DateTimeOffset(2026, 11, 20, 0, 0, 0, TimeSpan.Zero));
            Assert.True(deleted >= 2);

            Assert.Equal(0, Count(factory, "1m"));
            Assert.True(Count(factory, "1h") > 0);

            var afterRetention = await repo.GetSummaryAsync(start, end);
            Assert.Equal(300, afterRetention.RxBytes);
            Assert.Equal(30, afterRetention.TxBytes);
        }
        finally
        {
            TryDeleteDb(dbPath);
        }
    }

    private static long Count(SqliteConnectionFactory factory, string granularity)
    {
        using var conn = factory.CreateOpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM usage_buckets WHERE granularity = $g;";
        cmd.Parameters.AddWithValue("$g", granularity);
        return (long)(cmd.ExecuteScalar() ?? 0L);
    }

    private static void TryDeleteDb(string path)
    {
        try { File.Delete(path); } catch { }
    }
}
