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
        logger.LogInformation("FluxVault service lifetime started; waiting for verified vault initialisation.");
        try { await runtime.RunAsync(stoppingToken).ConfigureAwait(false); }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        { logger.LogInformation("FluxVault service is stopping; accepted requests have been joined."); }
        catch (Exception exception)
        {
            logger.LogCritical(exception, "FluxVault service stopped because startup or its supervised runtime failed.");
            throw;
        }
    }
}

public interface IFluxVaultServiceRuntime
{
    Task RunAsync(CancellationToken cancellationToken);
}

public sealed class FluxVaultServiceRuntime(ILogger<FluxVaultServiceRuntime> logger, ServiceStartupPolicy policy) : IFluxVaultServiceRuntime
{
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        await using var vault = await ServiceDependencyStartup.OpenAsync(
            token => WindowsSingleVaultService.OpenAsync(WindowsVaultInstallation.DefaultPath, token),
            exception => logger.LogWarning(exception,
                "Waiting for the installed PostgreSQL endpoint. Protection has not started; retrying in {RetrySeconds} seconds. No source access is authorised by this wait.",
                policy.RetryDelay.TotalSeconds), policy, cancellationToken).ConfigureAwait(false);
        var ipcServer = new NamedPipeFluxVaultServer(vault,
            WindowsFluxVaultPipeServerFactory.ForService(), new WindowsFluxVaultCallerContextProvider());
        logger.LogInformation("Verified vault ready; automatic protection follows the saved preference and creator sign-in.");
        // Both children and accepted requests join before the owned vault is disposed.
        await SupervisedRuntime.RunAsync(ipcServer.RunAsync, vault.RunAutomaticProtectionAsync, cancellationToken).ConfigureAwait(false);
    }
}
