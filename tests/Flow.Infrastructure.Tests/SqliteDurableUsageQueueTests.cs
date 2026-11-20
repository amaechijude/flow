using Flow.Application.Persistence;
using Flow.Infrastructure.Sqlite;
using Microsoft.Data.Sqlite;

namespace Flow.Infrastructure.Tests;

public sealed class SqliteDurableUsageQueueTests
{
    private static readonly DateTimeOffset Bucket = new(2026, 11, 20, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task EnqueueFlushAppliesTotalsOnceAndClearsPending()
    {
        var (factory, dbPath) = CreateFactory();
        try
        {
            var queue = new SqliteDurableUsageQueue(factory);
            await queue.EnqueueAsync([Delta(100, 10), Delta(50, 5)]);
            Assert.Equal(2, await queue.PendingCountAsync());
            Assert.NotNull(await queue.OldestPendingAtUtcAsync());

            Assert.Equal(2, await queue.FlushOnceAsync());
            Assert.Equal(0, await queue.PendingCountAsync());
            Assert.Equal(0, await queue.FlushOnceAsync()); // second call must not re-apply

            Assert.Equal(150, await SumRx(factory));
            Assert.Equal(15, await SumTx(factory));
        }
        finally { TryDelete(dbPath); }
    }

    [Fact]
    public async Task PendingDeltasSurviveANewQueueInstance()
    {
        var (factory, dbPath) = CreateFactory();
        try
        {
            var firstProcessQueue = new SqliteDurableUsageQueue(factory);
            await firstProcessQueue.EnqueueAsync([Delta(200, 20)]);
            Assert.Equal(1, await firstProcessQueue.PendingCountAsync());

            // Simulate a process restart: a fresh queue over the same database.
            var restartedQueue = new SqliteDurableUsageQueue(factory);
            Assert.Equal(1, await restartedQueue.PendingCountAsync());
            Assert.Equal(1, await restartedQueue.FlushOnceAsync());
            Assert.Equal(200, await SumRx(factory));
            Assert.Equal(20, await SumTx(factory));
        }
        finally { TryDelete(dbPath); }
    }

    [Fact]
    public async Task FailedFlushRetainsRowsAndDoesNotDoubleCountAfterRecovery()
    {
        var (factory, dbPath) = CreateFactory();
        try
        {
            var queue = new SqliteDurableUsageQueue(factory);
            await queue.EnqueueAsync([Delta(100, 10), Delta(50, 5)]);

            // Force the apply step to abort; the transaction must roll back.
            using (var conn = factory.CreateOpenConnection())
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = """
                    CREATE TRIGGER fail_usage_insert BEFORE INSERT ON usage_buckets
                    BEGIN SELECT RAISE(ABORT, 'injected'); END;
                    """;
                cmd.ExecuteNonQuery();
            }

            await Assert.ThrowsAnyAsync<Exception>(() => queue.FlushOnceAsync());
            Assert.Equal(2, await queue.PendingCountAsync());
            Assert.Equal(0, await SumRx(factory));

            await queue.RecordFailureAsync(new InvalidOperationException("injected"));
            using (var conn = factory.CreateOpenConnection())
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT COUNT(*) FROM pending_usage_deltas WHERE attempts = 1;";
                Assert.Equal(2, ReadInt(cmd));
            }

            using (var conn = factory.CreateOpenConnection())
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "DROP TRIGGER fail_usage_insert;";
                cmd.ExecuteNonQuery();
            }

            Assert.Equal(2, await queue.FlushOnceAsync());
            Assert.Equal(150, await SumRx(factory));
            Assert.Equal(15, await SumTx(factory));
            Assert.Equal(0, await queue.PendingCountAsync());
        }
        finally { TryDelete(dbPath); }
    }

    [Fact]
    public async Task FlushRespectsBatchSize()
    {
        var (factory, dbPath) = CreateFactory();
        try
        {
            var queue = new SqliteDurableUsageQueue(factory, batchSize: 2);
            await queue.EnqueueAsync([Delta(10, 1), Delta(20, 2), Delta(30, 3)]);
            Assert.Equal(2, await queue.FlushOnceAsync());
            Assert.Equal(1, await queue.PendingCountAsync());
            Assert.Equal(1, await queue.FlushOnceAsync());
            Assert.Equal(60, await SumRx(factory));
            Assert.Equal(6, await SumTx(factory));
        }
        finally { TryDelete(dbPath); }
    }

    private static (SqliteConnectionFactory Factory, string Path) CreateFactory()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), "flow-tests", $"{Guid.NewGuid():N}.db");
        var factory = new SqliteConnectionFactory(dbPath);
        using var conn = factory.CreateOpenConnection();
        DbMigrator.Migrate(conn);
        return (factory, dbPath);
    }

    private static UsageBucketDelta Delta(long rx, long tx) => new(
        Bucket, "app.test", "p1", "eth0", rx, tx, true, null, "test", "hook");

    private static Task<long> SumRx(SqliteConnectionFactory factory)
    {
        using var conn = factory.CreateOpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COALESCE(SUM(rx_bytes),0) FROM usage_buckets WHERE granularity = '1m';";
        return Task.FromResult((long)(cmd.ExecuteScalar() ?? 0L));
    }

    private static Task<long> SumTx(SqliteConnectionFactory factory)
    {
        using var conn = factory.CreateOpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COALESCE(SUM(tx_bytes),0) FROM usage_buckets WHERE granularity = '1m';";
        return Task.FromResult((long)(cmd.ExecuteScalar() ?? 0L));
    }

    private static long ReadInt(SqliteCommand cmd) => (long)(cmd.ExecuteScalar() ?? 0L);

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch { }
    }
}
