using FluxVault.Abstractions.Configuration;
using FluxVault.Abstractions.Storage;
using FluxVault.Core.Configuration;
using FluxVault.Core.Diagnostics;
using Microsoft.Extensions.Logging;

namespace FluxVault.Core.Service;

public sealed class RepositoryMaintenanceLoop(
    FluxVaultOperations operations,
    IFluxVaultConfigurationStore configurationStore,
    IRepositoryMaintenanceStateStore stateStore,
    TimeProvider timeProvider,
    ILogger<RepositoryMaintenanceLoop>? logger = null,
    TelemetryCollector? telemetryCollector = null)
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
                operations.SetRepositoryMaintenanceRuntime("Failed", 0, 0, $"Repository maintenance failed: {exception.Message}");
                telemetryCollector?.RecordLoopState("Repository maintenance loop", "Failed", exception.Message);
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
            logger?.LogTrace("Repository maintenance skipped because policy is disabled.");
            operations.SetRepositoryMaintenanceRuntime("Disabled", 0, 0, "Repository maintenance is disabled.");
            telemetryCollector?.RecordLoopState("Repository maintenance loop", "Disabled", "Policy disabled");
            return false;
        }

        if (!policy.RunAutomatically)
        {
            logger?.LogTrace("Repository maintenance automatic run skipped because automatic maintenance is disabled.");
            operations.SetRepositoryMaintenanceRuntime(
                "Manual only",
                0,
                0,
                "Automatic scrub and restore rehearsal are disabled.");
            telemetryCollector?.RecordLoopState("Repository maintenance loop", "Manual only", "Automatic maintenance disabled");
            return false;
        }

        operations.SetRepositoryMaintenanceRuntime("Checking", 0, 0, "Checking due maintenance");
        telemetryCollector?.RecordLoopState("Repository maintenance loop", "Checking", "Checking due maintenance");
        var state = await stateStore.LoadAsync(cancellationToken).ConfigureAwait(false);
        var now = timeProvider.GetUtcNow();
        if (state.LastMaintenanceUtc is not null && now - state.LastMaintenanceUtc.Value < policy.Interval)
        {
            logger?.LogTrace(
                "Repository maintenance not due. Last={LastMaintenanceUtc}; Interval={Interval}.",
                state.LastMaintenanceUtc,
                policy.Interval);
            operations.SetRepositoryMaintenanceRuntime("Waiting", 0, 0, $"Next automatic maintenance after {state.LastMaintenanceUtc.Value + policy.Interval:u}");
            telemetryCollector?.RecordLoopState("Repository maintenance loop", "Waiting", "Not due");
            return false;
        }

        logger?.LogTrace("Repository maintenance starting scrub and restore rehearsal.");
        telemetryCollector?.RecordLoopState("Repository maintenance loop", "Scrubbing", "Repository scrub");
        operations.SetRepositoryMaintenanceRuntime("Scrubbing", 1, 0, "Repository scrub in progress");
        logger?.LogTrace("Repository maintenance scrub starting.");
        await operations.RunRepositoryScrubAsync(cancellationToken).ConfigureAwait(false);
        logger?.LogTrace("Repository maintenance scrub completed.");
        telemetryCollector?.RecordLoopState("Repository maintenance loop", "Restore rehearsal", "Restore rehearsal");
        operations.SetRepositoryMaintenanceRuntime("Restore rehearsal", 1, policy.RestoreRehearsalVersionCount, "Restore rehearsal in progress");
        logger?.LogTrace("Repository maintenance restore rehearsal starting for {VersionCount} version(s).", policy.RestoreRehearsalVersionCount);
        await operations.RunRestoreRehearsalAsync(cancellationToken).ConfigureAwait(false);
        logger?.LogTrace("Repository maintenance restore rehearsal completed.");
        var updated = await stateStore.LoadAsync(cancellationToken).ConfigureAwait(false);
        await stateStore.SaveAsync(updated with { LastMaintenanceUtc = now }, cancellationToken).ConfigureAwait(false);
        operations.SetRepositoryMaintenanceRuntime("Waiting", 0, 0, $"Automatic maintenance completed at {now:u}");
        telemetryCollector?.RecordLoopState("Repository maintenance loop", "Waiting", "Automatic maintenance completed");
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
            LastRestoreRehearsal: state.LastRestoreRehearsal,
            LastMirrorRepair: state.LastMirrorRepair,
            LastMirrorRebalance: state.LastMirrorRebalance);
        await stateStore.SaveAsync(state with { LastHealth = snapshot }, cancellationToken).ConfigureAwait(false);
    }
}
