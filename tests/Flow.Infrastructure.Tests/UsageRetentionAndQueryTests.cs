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

    [Fact]
    public async Task PartialRollupsCannotMakeLongerRangeSmallerThanShorterRange()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), "flow-tests", $"{Guid.NewGuid():N}.db");
        try
        {
            var factory = new SqliteConnectionFactory(dbPath);
            using (var conn = factory.CreateOpenConnection())
                DbMigrator.Migrate(conn);

            var repo = new UsageRepository(factory);
            var shortStart = new DateTimeOffset(2026, 11, 20, 0, 0, 0, TimeSpan.Zero);
            var shortEnd = shortStart.AddDays(1);
            var longStart = new DateTimeOffset(2026, 11, 15, 0, 0, 0, TimeSpan.Zero);
            var longEnd = new DateTimeOffset(2026, 11, 22, 0, 0, 0, TimeSpan.Zero);

            await repo.WriteMinuteDeltasAsync(new[]
            {
                new UsageBucketDelta(shortStart.AddHours(10).AddMinutes(1), "app.test", "p1", "eth0", 1000, 100, true, null, "test", "hook"),
                new UsageBucketDelta(shortStart.AddDays(1).AddHours(10).AddMinutes(1), "app.test", "p1", "eth0", 2000, 200, true, null, "test", "hook"),
            });

            // Simulate the broken installed state: an hourly rollup exists only
            // for the most recent day, while 1m data covers both days.
            using (var conn = factory.CreateOpenConnection())
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = """
                    INSERT INTO usage_buckets (
                        bucket_start_utc, bucket_seconds, granularity, application_key, process_stable_key, interface_id,
                        rx_bytes, tx_bytes, is_attributed, measurement_provider
                    ) VALUES ('2026-11-21T10:00:00.0000000+00:00', 3600, '1h', 'app.test', 'p1', 'eth0', 500, 50, 1, 'test');
                    """;
                cmd.ExecuteNonQuery();
            }

            var shortSummary = await repo.GetSummaryAsync(shortStart, shortEnd);
            var longSummary = await repo.GetSummaryAsync(longStart, longEnd);

            Assert.Equal(1000, shortSummary.RxBytes);
            // The incomplete 1h rollup must not be trusted; 1m is authoritative.
            Assert.Equal(3000, longSummary.RxBytes);
            Assert.True(longSummary.RxBytes >= shortSummary.RxBytes);

            // Once rollups are complete, the long range uses them and still
            // returns the full total.
            var rollups = new UsageRollupService(factory);
            await rollups.RebuildThroughAsync(longEnd);
            var rebuilt = await repo.GetSummaryAsync(longStart, longEnd);
            Assert.Equal(3000, rebuilt.RxBytes);
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
