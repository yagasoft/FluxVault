using FluxVault.Windows.Capture;
using FluxVault.Core.Ipc;
using FluxVault.Core.Service;

namespace FluxVault.Service;

public sealed class Worker(
    ILogger<Worker> logger,
    CapturePipelinePlan capturePlan,
    NamedPipeFluxVaultServer ipcServer,
    FileSystemProtectionLoop protectionLoop) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation(
            "FluxVault service started. Durable change source: {DurableChangeSource}; open-file read strategy: {OpenFileReadStrategy}",
            capturePlan.DurableChangeSource,
            capturePlan.OpenFileReadStrategy);

        var ipcTask = ipcServer.RunAsync(stoppingToken);
        var protectionTask = protectionLoop.RunAsync(stoppingToken);

        try
        {
            await Task.WhenAll(ipcTask, protectionTask).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            logger.LogInformation("FluxVault service is stopping.");
        }
    }
}
