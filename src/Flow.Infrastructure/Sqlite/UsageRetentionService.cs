using Flow.Application.Persistence;
using Microsoft.Data.Sqlite;

namespace Flow.Infrastructure.Sqlite;

/// <summary>
/// Default retention: 1m for 7 days, 5m for 35 days, 1h for 400 days;
/// 1d and 1mo buckets are kept indefinitely.
/// </summary>
public sealed class UsageRetentionService : IUsageRetentionService
{
    private static readonly (string Granularity, TimeSpan Keep)[] Policies =
    [
        ("1m", TimeSpan.FromDays(7)),
        ("5m", TimeSpan.FromDays(35)),
        ("1h", TimeSpan.FromDays(400)),
    ];

    private readonly SqliteConnectionFactory _factory;

    public UsageRetentionService(SqliteConnectionFactory factory) => _factory = factory;

    public Task<int> ApplyAsync(DateTimeOffset nowUtc, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var now = nowUtc.ToUniversalTime();
        var deleted = 0;

        using var conn = _factory.CreateOpenConnection();
        using var tx = conn.BeginTransaction();
        foreach (var (granularity, keep) in Policies)
        {
            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = """
                DELETE FROM usage_buckets
                WHERE granularity = $g AND bucket_start_utc < $cutoff;
                """;
            cmd.Parameters.AddWithValue("$g", granularity);
            cmd.Parameters.AddWithValue("$cutoff", (now - keep).ToString("O"));
            deleted += cmd.ExecuteNonQuery();
        }
        tx.Commit();
        return Task.FromResult(deleted);
    }
}
