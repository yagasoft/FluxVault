using FluxVault.Abstractions.Configuration;
using FluxVault.Abstractions.Storage;
using FluxVault.Core.Configuration;
using Microsoft.Extensions.Logging;

namespace FluxVault.Core.Service;

public sealed class RepositoryMaintenanceLoop(
    FluxVaultOperations operations,
    IFluxVaultConfigurationStore configurationStore,
    IRepositoryMaintenanceStateStore stateStore,
    TimeProvider timeProvider,
    ILogger<RepositoryMaintenanceLoop>? logger = null)
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMinutes(5);

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await RunDueMaintenanceOnceAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                logger?.LogWarning(exception, "Repository maintenance failed; FluxVault will retry on the next maintenance cycle.");
                await StoreMaintenanceFailureAsync(exception, cancellationToken).ConfigureAwait(false);
            }

            await Task.Delay(PollInterval, timeProvider, cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task<bool> RunDueMaintenanceOnceAsync(CancellationToken cancellationToken = default)
    {
        var configuration = await configurationStore.LoadAsync(cancellationToken).ConfigureAwait(false);
        var policy = configuration.RepositoryMaintenancePolicy ?? RepositoryMaintenancePolicy.CreateDefault();
        if (!policy.IsEnabled)
        {
            return false;
        }

        var state = await stateStore.LoadAsync(cancellationToken).ConfigureAwait(false);
        var now = timeProvider.GetUtcNow();
        if (state.LastMaintenanceUtc is not null && now - state.LastMaintenanceUtc.Value < policy.Interval)
        {
            return false;
        }

        await operations.RunRepositoryScrubAsync(cancellationToken).ConfigureAwait(false);
        await operations.RunRestoreRehearsalAsync(cancellationToken).ConfigureAwait(false);
        var updated = await stateStore.LoadAsync(cancellationToken).ConfigureAwait(false);
        await stateStore.SaveAsync(updated with { LastMaintenanceUtc = now }, cancellationToken).ConfigureAwait(false);
        return true;
    }

    private async Task StoreMaintenanceFailureAsync(Exception exception, CancellationToken cancellationToken)
    {
        var state = await stateStore.LoadAsync(cancellationToken).ConfigureAwait(false);
        var snapshot = new RepositoryHealthSnapshot(
            CheckedAtUtc: timeProvider.GetUtcNow(),
            OverallState: RepositoryHealthState.Warning,
            Summary: $"Repository maintenance failed: {exception.Message}",
            LastScrub: state.LastScrub,
            LastRestoreRehearsal: state.LastRestoreRehearsal);
        await stateStore.SaveAsync(state with { LastHealth = snapshot }, cancellationToken).ConfigureAwait(false);
    }
}
