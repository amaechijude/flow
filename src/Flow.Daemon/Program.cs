using Flow.Application.Monitoring;
using Flow.Application.Persistence;
using Flow.Application.Queries;
using Flow.Daemon;
using Flow.Daemon.Ipc;
using Flow.Infrastructure.Sqlite;
using Flow.Linux.Monitoring;

var builder = Host.CreateApplicationBuilder(args);

var mode = DaemonModeParser.Parse(builder.Configuration["Flow:Mode"]);

var dbPath = builder.Configuration["Flow:DatabasePath"];
if (string.IsNullOrWhiteSpace(dbPath))
    dbPath = FlowPaths.DefaultDatabasePath(mode);

var dbDirectory = Path.GetDirectoryName(Path.GetFullPath(dbPath));
if (!string.IsNullOrWhiteSpace(dbDirectory) && !Directory.Exists(dbDirectory))
{
    throw new InvalidOperationException(
        $"Database directory '{dbDirectory}' does not exist. Create it with the correct permissions before starting Flow.");
}

var connectionFactory = new SqliteConnectionFactory(dbPath);
using (var conn = connectionFactory.CreateOpenConnection())
{
    DbMigrator.Migrate(conn);
}

builder.Services.AddSingleton(connectionFactory);
builder.Services.AddSingleton<UsageRepository>();
builder.Services.AddSingleton<IUsageBucketStore>(sp => sp.GetRequiredService<UsageRepository>());
builder.Services.AddSingleton<IMonitoringMetadataStore>(sp => sp.GetRequiredService<UsageRepository>());
builder.Services.AddSingleton<IUsageQueryService>(sp => sp.GetRequiredService<UsageRepository>());
builder.Services.AddSingleton<UsageRollupService>();
builder.Services.AddSingleton<UsageRetentionService>();
builder.Services.AddSingleton<IUsageRetentionService>(sp => sp.GetRequiredService<UsageRetentionService>());
builder.Services.AddSingleton<IUsageRollupService>(sp => sp.GetRequiredService<UsageRollupService>());

if (mode != DaemonMode.Session)
{
    var monitorKind = builder.Configuration["Flow:Monitor"];
    if (string.Equals(monitorKind, "procfs", StringComparison.OrdinalIgnoreCase))
    {
        builder.Services.AddSingleton<INetworkMonitor, ProcNetDevMonitor>();
    }
    else if (string.Equals(monitorKind, "ebpf", StringComparison.OrdinalIgnoreCase) ||
             // "ebpf-tcp" is a legacy alias kept for existing configuration.
             string.Equals(monitorKind, "ebpf-tcp", StringComparison.OrdinalIgnoreCase))
    {
        var ebpfOptions = new EbpfMonitorOptions
        {
            LoaderPath = builder.Configuration["Flow:Ebpf:LoaderPath"],
        };
        if (int.TryParse(builder.Configuration["Flow:Ebpf:IntervalSeconds"], out var interval) && interval > 0)
            ebpfOptions.IntervalSeconds = interval;
        if (int.TryParse(builder.Configuration["Flow:Ebpf:Rounds"], out var rounds) && rounds > 0)
            ebpfOptions.Rounds = rounds;
        builder.Services.AddSingleton<INetworkMonitor>(_ => new EbpfMonitor(ebpfOptions));
    }
    else
    {
        builder.Services.AddSingleton<INetworkMonitor, NullNetworkMonitor>();
    }

    builder.Services.AddSingleton<UsageIngestionPipeline>();
    builder.Services.AddSingleton<IMonitoringController>(sp => sp.GetRequiredService<UsageIngestionPipeline>());
    builder.Services.AddSingleton<IMonitoringDiagnosticsSource>(sp => sp.GetRequiredService<UsageIngestionPipeline>());
}
else
{
    // Session mode has no monitor and no pipeline; status comes from the
    // collector's persisted monitoring session rows.
    builder.Services.AddSingleton<IMonitoringDiagnosticsSource, PersistedDiagnosticsSource>();
}

builder.Services.AddSingleton<IMonitoringStatusService, MonitoringStatusService>();

if (mode != DaemonMode.Session)
{
    builder.Services.AddHostedService<Worker>();
    builder.Services.AddHostedService<UsageRollupWorker>();
}

if (mode != DaemonMode.Collector)
{
    // Collector mode is privileged and headless: it writes to SQLite but does
    // not claim org.flow.Daemon on the user session bus.
    builder.Services.AddHostedService<DbusIpcHostedService>();
}

var host = builder.Build();
host.Run();
