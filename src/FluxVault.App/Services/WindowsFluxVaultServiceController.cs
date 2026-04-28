using System.ComponentModel;
using System.ServiceProcess;

namespace FluxVault.App.Services;

public sealed class WindowsFluxVaultServiceController(
    string serviceName = WindowsFluxVaultServiceController.DefaultServiceName,
    TimeSpan? transitionTimeout = null) : IFluxVaultWindowsServiceController
{
    public const string DefaultServiceName = "FluxVaultService";

    private readonly TimeSpan transitionTimeout = transitionTimeout ?? TimeSpan.FromSeconds(30);

    public Task<FluxVaultWindowsServiceStatus> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        return Task.Run(() => GetStatusCore(), cancellationToken);
    }

    public Task<FluxVaultWindowsServiceActionResult> StartAsync(CancellationToken cancellationToken = default)
    {
        return Task.Run(() =>
        {
            try
            {
                using var service = new ServiceController(serviceName);
                if (service.Status == ServiceControllerStatus.Running)
                {
                    return Success(GetStatusCore(), "FluxVault service is already running.");
                }

                service.Start();
                service.WaitForStatus(ServiceControllerStatus.Running, transitionTimeout);
                return Success(GetStatusCore(), "FluxVault service started.");
            }
            catch (Exception ex) when (IsServiceMissing(ex))
            {
                var status = new FluxVaultWindowsServiceStatus(
                    serviceName,
                    FluxVaultWindowsServiceState.NotInstalled,
                    "FluxVault service is not installed.");
                return new FluxVaultWindowsServiceActionResult(false, status, status.Message);
            }
            catch (Exception ex) when (IsAccessDenied(ex))
            {
                var status = GetStatusCore();
                return new FluxVaultWindowsServiceActionResult(
                    false,
                    status,
                    $"Starting {serviceName} requires elevated permissions.");
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ServiceProcess.TimeoutException or Win32Exception)
            {
                var status = GetStatusCore();
                return new FluxVaultWindowsServiceActionResult(false, status, $"Starting {serviceName} failed: {ex.Message}");
            }
        }, cancellationToken);
    }

    public Task<FluxVaultWindowsServiceActionResult> StopAsync(CancellationToken cancellationToken = default)
    {
        return Task.Run(() =>
        {
            try
            {
                using var service = new ServiceController(serviceName);
                if (service.Status == ServiceControllerStatus.Stopped)
                {
                    return Success(GetStatusCore(), "FluxVault service is already stopped.");
                }

                service.Stop();
                service.WaitForStatus(ServiceControllerStatus.Stopped, transitionTimeout);
                return Success(GetStatusCore(), "FluxVault service stopped.");
            }
            catch (Exception ex) when (IsServiceMissing(ex))
            {
                var status = new FluxVaultWindowsServiceStatus(
                    serviceName,
                    FluxVaultWindowsServiceState.NotInstalled,
                    "FluxVault service is not installed.");
                return new FluxVaultWindowsServiceActionResult(false, status, status.Message);
            }
            catch (Exception ex) when (IsAccessDenied(ex))
            {
                var status = GetStatusCore();
                return new FluxVaultWindowsServiceActionResult(
                    false,
                    status,
                    $"Stopping {serviceName} requires elevated permissions.");
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ServiceProcess.TimeoutException or Win32Exception)
            {
                var status = GetStatusCore();
                return new FluxVaultWindowsServiceActionResult(false, status, $"Stopping {serviceName} failed: {ex.Message}");
            }
        }, cancellationToken);
    }

    private FluxVaultWindowsServiceStatus GetStatusCore()
    {
        try
        {
            using var service = new ServiceController(serviceName);
            var state = ToState(service.Status);
            return new FluxVaultWindowsServiceStatus(serviceName, state, ToMessage(state));
        }
        catch (Exception ex) when (IsServiceMissing(ex))
        {
            return new FluxVaultWindowsServiceStatus(
                serviceName,
                FluxVaultWindowsServiceState.NotInstalled,
                "FluxVault service is not installed.");
        }
        catch (Exception ex) when (IsAccessDenied(ex))
        {
            return new FluxVaultWindowsServiceStatus(
                serviceName,
                FluxVaultWindowsServiceState.Inaccessible,
                $"FluxVault service status requires elevated permissions: {ex.Message}");
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
        {
            return new FluxVaultWindowsServiceStatus(
                serviceName,
                FluxVaultWindowsServiceState.Inaccessible,
                $"FluxVault service status is unavailable: {ex.Message}");
        }
    }

    private static FluxVaultWindowsServiceActionResult Success(FluxVaultWindowsServiceStatus status, string message)
    {
        return new FluxVaultWindowsServiceActionResult(true, status, message);
    }

    private static FluxVaultWindowsServiceState ToState(ServiceControllerStatus status)
    {
        return status switch
        {
            ServiceControllerStatus.Running => FluxVaultWindowsServiceState.Running,
            ServiceControllerStatus.Stopped => FluxVaultWindowsServiceState.Stopped,
            ServiceControllerStatus.StartPending => FluxVaultWindowsServiceState.StartPending,
            ServiceControllerStatus.StopPending => FluxVaultWindowsServiceState.StopPending,
            _ => FluxVaultWindowsServiceState.Unknown
        };
    }

    private static string ToMessage(FluxVaultWindowsServiceState state)
    {
        return state switch
        {
            FluxVaultWindowsServiceState.Running => "FluxVault service is running.",
            FluxVaultWindowsServiceState.Stopped => "FluxVault service is stopped.",
            FluxVaultWindowsServiceState.StartPending => "FluxVault service is starting.",
            FluxVaultWindowsServiceState.StopPending => "FluxVault service is stopping.",
            _ => "FluxVault service state is unknown."
        };
    }

    private static bool IsServiceMissing(Exception ex)
    {
        return ex is InvalidOperationException invalidOperation
            && invalidOperation.InnerException is Win32Exception { NativeErrorCode: 1060 };
    }

    private static bool IsAccessDenied(Exception ex)
    {
        return ex is UnauthorizedAccessException
            || ex is Win32Exception { NativeErrorCode: 5 }
            || ex.InnerException is Win32Exception { NativeErrorCode: 5 };
    }
}
