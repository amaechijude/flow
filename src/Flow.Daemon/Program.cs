using Flow.Application.Monitoring;
using Flow.Application.Persistence;
using Flow.Application.Queries;
using Flow.Daemon;
using Flow.Daemon.Ipc;
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
builder.Services.AddSingleton<IUsageQueryService>(sp => sp.GetRequiredService<UsageRepository>());

var monitorKind = builder.Configuration["Flow:Monitor"];
if (string.Equals(monitorKind, "procfs", StringComparison.OrdinalIgnoreCase))
{
    builder.Services.AddSingleton<INetworkMonitor, ProcNetDevMonitor>();
}
else if (string.Equals(monitorKind, "ebpf", StringComparison.OrdinalIgnoreCase) ||
         string.Equals(monitorKind, "ebpf-tcp", StringComparison.OrdinalIgnoreCase))
{
    var ebpfOptions = new EbpfTcpMonitorOptions
    {
        LoaderPath = builder.Configuration["Flow:Ebpf:LoaderPath"],
    };
    if (int.TryParse(builder.Configuration["Flow:Ebpf:IntervalSeconds"], out var interval) && interval > 0)
        ebpfOptions.IntervalSeconds = interval;
    if (int.TryParse(builder.Configuration["Flow:Ebpf:Rounds"], out var rounds) && rounds > 0)
        ebpfOptions.Rounds = rounds;
    builder.Services.AddSingleton<INetworkMonitor>(_ => new EbpfTcpMonitor(ebpfOptions));
}
else
{
    builder.Services.AddSingleton<INetworkMonitor, NullNetworkMonitor>();
}

builder.Services.AddSingleton<UsageIngestionPipeline>();
builder.Services.AddSingleton<IMonitoringController>(sp => sp.GetRequiredService<UsageIngestionPipeline>());
builder.Services.AddSingleton<IMonitoringDiagnosticsSource>(sp => sp.GetRequiredService<UsageIngestionPipeline>());
builder.Services.AddSingleton<IMonitoringStatusService, MonitoringStatusService>();
builder.Services.AddHostedService<Worker>();
builder.Services.AddHostedService<DbusIpcHostedService>();

var host = builder.Build();
host.Run();
