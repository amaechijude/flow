using System.Globalization;
using Microsoft.Data.Sqlite;

namespace Flow.Infrastructure.Sqlite;

internal sealed class UsageRollupState
{
    private readonly SqliteConnectionFactory _factory;

    public UsageRollupState(SqliteConnectionFactory factory) => _factory = factory;

    public DateTimeOffset? GetLastRolledMinuteUtc()
    {
        using var conn = _factory.CreateOpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT last_rolled_minute_utc FROM usage_rollup_state WHERE id = 1;";
        var value = cmd.ExecuteScalar() as string;
        return string.IsNullOrWhiteSpace(value)
            ? null
            : DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
    }

    public void SetLastRolledMinuteUtc(DateTimeOffset value)
    {
        using var conn = _factory.CreateOpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO usage_rollup_state (id, last_rolled_minute_utc)
            VALUES (1, $value)
            ON CONFLICT(id) DO UPDATE SET last_rolled_minute_utc = excluded.last_rolled_minute_utc;
            """;
        cmd.Parameters.AddWithValue("$value", value.ToString("O"));
        cmd.ExecuteNonQuery();
    }
}
