using Flow.Application.Persistence;
using Flow.Infrastructure.Sqlite;
using Microsoft.Data.Sqlite;

namespace Flow.Infrastructure.Tests;

public sealed class UsageRollupServiceTests
{
    [Fact]
    public async Task RebuildPreservesTotalsAndIsIdempotent()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), "flow-tests", $"{Guid.NewGuid():N}.db");
        try
        {
            var factory = new SqliteConnectionFactory(dbPath);
            using (var conn = factory.CreateOpenConnection())
                DbMigrator.Migrate(conn);

            var repo = new UsageRepository(factory);
            var rollups = new UsageRollupService(factory);
            var through = new DateTimeOffset(2026, 11, 20, 10, 10, 0, TimeSpan.Zero);

            await repo.WriteMinuteDeltasAsync(new[]
            {
                Delta(10, 1, 100, 10),
                Delta(10, 2, 50, 5),
                Delta(10, 6, 20, 2),
            });

            var written = await rollups.RebuildThroughAsync(through);
            Assert.True(written > 0);

            Assert.Equal(150, Sum(factory, "5m", new DateTimeOffset(2026, 11, 20, 10, 0, 0, TimeSpan.Zero)));
            Assert.Equal(20, Sum(factory, "5m", new DateTimeOffset(2026, 11, 20, 10, 5, 0, TimeSpan.Zero)));
            Assert.Equal(170, Sum(factory, "1h", new DateTimeOffset(2026, 11, 20, 10, 0, 0, TimeSpan.Zero)));
            Assert.Equal(170, Sum(factory, "1d", new DateTimeOffset(2026, 11, 20, 0, 0, 0, TimeSpan.Zero)));
            Assert.Equal(170, Sum(factory, "1mo", new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero)));

            // Rebuilds recompute a recent lookback window, so row count may be > 0;
            // values must be unchanged (idempotent totals).
            _ = await rollups.RebuildThroughAsync(through);
            Assert.Equal(170, Sum(factory, "1h", new DateTimeOffset(2026, 11, 20, 10, 0, 0, TimeSpan.Zero)));

            // New minute in an already-rolled range is included exactly once.
            await repo.WriteMinuteDeltasAsync(new[] { Delta(10, 3, 30, 3) });
            await rollups.RebuildThroughAsync(through);
            Assert.Equal(180, Sum(factory, "5m", new DateTimeOffset(2026, 11, 20, 10, 0, 0, TimeSpan.Zero)));
            Assert.Equal(200, Sum(factory, "1h", new DateTimeOffset(2026, 11, 20, 10, 0, 0, TimeSpan.Zero)));
        }
        finally
        {
            TryDeleteDb(dbPath);
        }
    }

    [Fact]
    public async Task RepeatedRebuildsDoNotEraseOlderRollupCoverage()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), "flow-tests", $"{Guid.NewGuid():N}.db");
        try
        {
            var factory = new SqliteConnectionFactory(dbPath);
            using (var conn = factory.CreateOpenConnection())
                DbMigrator.Migrate(conn);

            var repo = new UsageRepository(factory);
            var rollups = new UsageRollupService(factory);
            var day = new DateTimeOffset(2026, 11, 20, 0, 0, 0, TimeSpan.Zero);

            await repo.WriteMinuteDeltasAsync(new[]
            {
                new UsageBucketDelta(day.AddHours(1).AddMinutes(5), "app.test", "p1", "eth0", 100, 10, true, null, "test", "hook"),
                new UsageBucketDelta(day.AddHours(11).AddMinutes(5), "app.test", "p1", "eth0", 200, 20, true, null, "test", "hook"),
            });
            await rollups.RebuildThroughAsync(day.AddHours(11).AddMinutes(10));

            Assert.Equal(100, Sum(factory, "1h", day.AddHours(1)));
            Assert.Equal(200, Sum(factory, "1h", day.AddHours(11)));

            // The next tick is more than the 2h lookback after the last rollup.
            // A broad delete with a narrow rebuild would drop the 01:00 row.
            await repo.WriteMinuteDeltasAsync(new[]
            {
                new UsageBucketDelta(day.AddHours(13).AddMinutes(5), "app.test", "p1", "eth0", 300, 30, true, null, "test", "hook"),
            });
            await rollups.RebuildThroughAsync(day.AddHours(13).AddMinutes(10));

            Assert.Equal(100, Sum(factory, "1h", day.AddHours(1)));
            Assert.Equal(200, Sum(factory, "1h", day.AddHours(11)));
            Assert.Equal(300, Sum(factory, "1h", day.AddHours(13)));
            Assert.Equal(600, Sum(factory, "1d", day));
        }
        finally
        {
            TryDeleteDb(dbPath);
        }
    }

    [Fact]
    public async Task RebuildBackfillsMissingHistoryWhenWatermarkIsAhead()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), "flow-tests", $"{Guid.NewGuid():N}.db");
        try
        {
            var factory = new SqliteConnectionFactory(dbPath);
            using (var conn = factory.CreateOpenConnection())
                DbMigrator.Migrate(conn);

            var repo = new UsageRepository(factory);
            var rollups = new UsageRollupService(factory);
            var day = new DateTimeOffset(2026, 11, 20, 0, 0, 0, TimeSpan.Zero);

            await repo.WriteMinuteDeltasAsync(new[]
            {
                new UsageBucketDelta(day.AddHours(1).AddMinutes(5), "app.test", "p1", "eth0", 100, 10, true, null, "test", "hook"),
                new UsageBucketDelta(day.AddHours(11).AddMinutes(5), "app.test", "p1", "eth0", 200, 20, true, null, "test", "hook"),
            });
            // Simulate a DB where the watermark advanced but rollups were never
            // built for the older data (the installed-app state that triggered
            // the 7-day-undercount report).
            SetWatermark(factory, day.AddHours(12));

            await rollups.RebuildThroughAsync(day.AddHours(12).AddMinutes(5));

            Assert.Equal(100, Sum(factory, "1h", day.AddHours(1)));
            Assert.Equal(200, Sum(factory, "1h", day.AddHours(11)));
        }
        finally
        {
            TryDeleteDb(dbPath);
        }
    }

    private static void SetWatermark(SqliteConnectionFactory factory, DateTimeOffset value)
    {
        using var conn = factory.CreateOpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO usage_rollup_state (id, last_rolled_minute_utc)
            VALUES (1, $value)
            ON CONFLICT(id) DO UPDATE SET last_rolled_minute_utc = excluded.last_rolled_minute_utc;
            """;
        cmd.Parameters.AddWithValue("$value", value.ToString("O"));
        cmd.ExecuteNonQuery();
    }

    private static UsageBucketDelta Delta(int hour, int minute, long rx, long tx)
        => new(
            new DateTimeOffset(2026, 11, 20, hour, minute, 30, TimeSpan.Zero),
            "app.test", "p1", "eth0", rx, tx, true, null, "test", "hook");

    private static long Sum(SqliteConnectionFactory factory, string granularity, DateTimeOffset bucketStart)
    {
        using var conn = factory.CreateOpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT COALESCE(SUM(rx_bytes), 0)
            FROM usage_buckets
            WHERE granularity = $g AND bucket_start_utc = $start;
            """;
        cmd.Parameters.AddWithValue("$g", granularity);
        cmd.Parameters.AddWithValue("$start", bucketStart.ToString("O"));
        return (long)(cmd.ExecuteScalar() ?? 0L);
    }

    private static void TryDeleteDb(string path)
    {
        try { File.Delete(path); } catch { }
    }
}
