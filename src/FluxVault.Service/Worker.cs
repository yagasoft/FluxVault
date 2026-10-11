using FluxVault.Core.Ipc;
using FluxVault.Core.Service;
using FluxVault.Windows.Security;

namespace FluxVault.Service;

public sealed class Worker(ILogger<Worker> logger, IFluxVaultServiceRuntime runtime) : BackgroundService
{
    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        try { await base.StopAsync(cancellationToken).ConfigureAwait(false); }
        finally
        {
            // The host deadline cannot release catalogue-only or queued requests.
            // The server's complete lifetime includes all accepted handler tasks.
            if (ExecuteTask is { } execution) await execution.ConfigureAwait(false);
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("FluxVault single-vault service started with supervised creator-signed-in automatic protection.");
        try { await runtime.RunAsync(stoppingToken).ConfigureAwait(false); }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        { logger.LogInformation("FluxVault service is stopping; accepted requests have been joined."); }
        catch (Exception exception)
        {
            logger.LogCritical(exception, "FluxVault service stopped because its authenticated transport failed.");
            throw;
        }
    }
}

public interface IFluxVaultServiceRuntime
{
    Task RunAsync(CancellationToken cancellationToken);
}

public sealed class FluxVaultServiceRuntime(NamedPipeFluxVaultServer ipcServer, WindowsSingleVaultService vault) : IFluxVaultServiceRuntime
{
    public Task RunAsync(CancellationToken cancellationToken) => SupervisedRuntime.RunAsync(ipcServer.RunAsync, vault.RunAutomaticProtectionAsync, cancellationToken);
}
