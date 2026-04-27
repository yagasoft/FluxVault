using FluxVault.Windows.Capture;

namespace FluxVault.Service;

public sealed class Worker(ILogger<Worker> logger, CapturePipelinePlan capturePlan) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation(
            "FluxVault service started. Durable change source: {DurableChangeSource}; open-file read strategy: {OpenFileReadStrategy}",
            capturePlan.DurableChangeSource,
            capturePlan.OpenFileReadStrategy);

        while (!stoppingToken.IsCancellationRequested)
        {
            await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
        }
    }
}
