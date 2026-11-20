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
