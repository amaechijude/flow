namespace Flow.Desktop.Services;

/// <summary>
/// Reports which real data path served the most recent query, so the UI can be
/// explicit about live IPC versus a direct local-database fallback.
/// </summary>
public interface IDataSourceStatus
{
    string UsageDataSource { get; }
    string StatusDataSource { get; }
}
