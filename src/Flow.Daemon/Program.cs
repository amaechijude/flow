using Flow.Application.Monitoring;
using Flow.Application.Persistence;
using Flow.Daemon;
using Flow.Infrastructure.Sqlite;
using Flow.Linux.Monitoring;

var builder = Host.CreateApplicationBuilder(args);

var dbPath = builder.Configuration["Flow:DatabasePath"];
if (string.IsNullOrWhiteSpace(dbPath))
{
    var baseDir = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
    if (string.IsNullOrWhiteSpace(baseDir))
        baseDir = AppContext.BaseDirectory;
    dbPath = Path.Combine(baseDir, "flow", "flow.db");
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

var monitorKind = builder.Configuration["Flow:Monitor"];
if (string.Equals(monitorKind, "procfs", StringComparison.OrdinalIgnoreCase))
    builder.Services.AddSingleton<INetworkMonitor, ProcNetDevMonitor>();
else
    builder.Services.AddSingleton<INetworkMonitor, NullNetworkMonitor>();

builder.Services.AddSingleton<UsageIngestionPipeline>();
builder.Services.AddSingleton<IMonitoringController>(sp => sp.GetRequiredService<UsageIngestionPipeline>());
builder.Services.AddHostedService<Worker>();

var host = builder.Build();
host.Run();
