using FluxVault.Windows.Capture;
using FluxVault.Abstractions.Configuration;
using FluxVault.Core.Configuration;
using FluxVault.Core.Diagnostics;
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
    FluxVaultProfileManager profileManager) : IFluxVaultServiceRuntime
{
    public Task RunAsync(CancellationToken cancellationToken)
    {
        var ipcTask = ipcServer.RunAsync(cancellationToken);
        var profileTask = profileManager.RunEnabledProfilesAsync(cancellationToken);
        return Task.WhenAll(ipcTask, profileTask);
    }
}

public sealed class TelemetrySamplingService(
    IFluxVaultProfileSetStore profileSetStore,
    DiagnosticsPolicyRuntime diagnosticsPolicyRuntime,
    TelemetryCollector telemetryCollector,
    RollingJsonFileLoggerProvider rollingLogger,
    ILogger<TelemetrySamplingService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var delay = DiagnosticsPolicy.DefaultTelemetrySampleInterval;
            try
            {
                telemetryCollector.RecordLoopState("Telemetry sampler", "Running", "Sampling process resources");
                var profileSet = await profileSetStore.LoadAsync(stoppingToken).ConfigureAwait(false);
                var policy = profileSet.ActiveProfile.Configuration.DiagnosticsPolicy
                    ?? DiagnosticsPolicy.CreateDefault(Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                        "FluxVault"));
                diagnosticsPolicyRuntime.Update(policy);
                policy = diagnosticsPolicyRuntime.Current;
                delay = policy.TelemetrySampleInterval;
                telemetryCollector.SampleOnce(
                    policy,
                    droppedLogMessages: rollingLogger.GetStatus().DroppedMessageCount);
                telemetryCollector.RecordLoopState("Telemetry sampler", "Waiting", $"Delay {delay:g}");
                logger.LogTrace("Telemetry sample captured. Next sample in {Delay}.", delay);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "Telemetry sampling failed; FluxVault will retry on the next telemetry interval.");
            }

            await Task.Delay(delay, stoppingToken).ConfigureAwait(false);
        }
    }
}
