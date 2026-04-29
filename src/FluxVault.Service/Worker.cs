using FluxVault.Windows.Capture;
using FluxVault.Core.Ipc;
using FluxVault.Core.Service;

namespace FluxVault.Service;

public sealed class Worker(
    ILogger<Worker> logger,
    CapturePipelinePlan capturePlan,
    IFluxVaultServiceRuntime runtime) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation(
            "FluxVault service started. Durable change source: {DurableChangeSource}; open-file read strategy: {OpenFileReadStrategy}",
            capturePlan.DurableChangeSource,
            capturePlan.OpenFileReadStrategy);

        try
        {
            await runtime.RunAsync(stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            logger.LogInformation("FluxVault service is stopping.");
        }
        catch (Exception ex)
        {
            logger.LogCritical(
                ex,
                "FluxVault service stopped because an internal background task failed. Windows Service recovery should restart it.");
            throw;
        }
    }
}

public interface IFluxVaultServiceRuntime
{
    Task RunAsync(CancellationToken cancellationToken);
}

public sealed class FluxVaultServiceRuntime(
    NamedPipeFluxVaultServer ipcServer,
    FileSystemProtectionLoop protectionLoop,
    RepositoryMaintenanceLoop repositoryMaintenanceLoop) : IFluxVaultServiceRuntime
{
    public Task RunAsync(CancellationToken cancellationToken)
    {
        var ipcTask = ipcServer.RunAsync(cancellationToken);
        var protectionTask = protectionLoop.RunAsync(cancellationToken);
        var maintenanceTask = repositoryMaintenanceLoop.RunAsync(cancellationToken);
        return Task.WhenAll(ipcTask, protectionTask, maintenanceTask);
    }
}
