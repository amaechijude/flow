using Flow.Application.Monitoring;
using Flow.Domain.Monitoring;

namespace Flow.Daemon;

public sealed partial class Worker(
    UsageIngestionPipeline pipeline,
    ILogger<Worker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        LogStarting(logger);
        await pipeline.StartMonitoringAsync(stoppingToken).ConfigureAwait(false);

        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30));
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                var d = pipeline.Diagnostics;
                LogDiagnostics(logger, d.State, d.EventsReceived, d.EventsDropped, d.EventsAggregated, d.BatchesWritten, d.WriteErrors, d.LastError ?? "-");
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // shutdown
        }
        finally
        {
            LogStopping(logger, pipeline.State);
            await pipeline.StopAsync(CancellationToken.None).ConfigureAwait(false);
        }
    }

    [LoggerMessage(EventId = 2001, Level = LogLevel.Information, Message = "Flow daemon worker starting monitoring pipeline")]
    private static partial void LogStarting(ILogger logger);

    [LoggerMessage(EventId = 2002, Level = LogLevel.Information, Message = "Flow daemon worker stopping (state={State})")]
    private static partial void LogStopping(ILogger logger, MonitoringState state);

    [LoggerMessage(EventId = 2003, Level = LogLevel.Information, Message = "Pipeline diagnostics: state={State} received={Received} dropped={Dropped} aggregated={Aggregated} batches={Batches} writeErrors={WriteErrors} lastError={LastError}")]
    private static partial void LogDiagnostics(ILogger logger, MonitoringState state, long received, long dropped, long aggregated, long batches, long writeErrors, string lastError);
}
