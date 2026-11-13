namespace Flow.Daemon;

public class Worker(ILogger<Worker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            if (logger.IsEnabled(LogLevel.Information))
            {
#pragma warning disable CA1848 // Template worker placeholder; replace with structured service logging.
                logger.LogInformation("Worker running at: {Time}", DateTimeOffset.Now);
#pragma warning restore CA1848
            }
            await Task.Delay(1000, stoppingToken);
        }
    }
}
