namespace FluxVault.App.Services;

public enum FluxVaultWindowsServiceState
{
    Unknown = 0,
    Running = 1,
    Stopped = 2,
    StartPending = 3,
    StopPending = 4,
    NotInstalled = 5,
    Inaccessible = 6
}

public sealed record FluxVaultWindowsServiceStatus(
    string ServiceName,
    FluxVaultWindowsServiceState State,
    string Message)
{
    public bool IsRunning => State == FluxVaultWindowsServiceState.Running;

    public bool CanStart => State == FluxVaultWindowsServiceState.Stopped;

    public bool CanStop => State == FluxVaultWindowsServiceState.Running;

    public bool CanToggle => CanStart || CanStop;

    public bool IsWarning => State != FluxVaultWindowsServiceState.Running;

    public string ActionLabel => State == FluxVaultWindowsServiceState.Running
        ? "Stop service"
        : "Start service";
}

public sealed record FluxVaultWindowsServiceActionResult(
    bool Success,
    FluxVaultWindowsServiceStatus Status,
    string Message);

public interface IFluxVaultWindowsServiceController
{
    Task<FluxVaultWindowsServiceStatus> GetStatusAsync(CancellationToken cancellationToken = default);

    Task<FluxVaultWindowsServiceActionResult> StartAsync(CancellationToken cancellationToken = default);

    Task<FluxVaultWindowsServiceActionResult> StopAsync(CancellationToken cancellationToken = default);
}
