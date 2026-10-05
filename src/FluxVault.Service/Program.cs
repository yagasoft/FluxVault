using FluxVault.Service;
using FluxVault.Core.Ipc;
using FluxVault.Windows.Security;
using Microsoft.Extensions.Logging.EventLog;

// Explicit, bounded once-only mode. It never starts the normal runtime and cannot
// select a product bootstrap path or accept configuration/ownership from a client.
if (args.Contains("--provision-installation", StringComparer.Ordinal))
{
    if (args.Length != 2 || args[0] != "--provision-installation")
        throw new ArgumentException("Setup requires exactly --provision-installation <protected-ticket-path>.");
    await using var setup = WindowsSingleVaultProvisioner.Open(args[1], WindowsVaultInstallation.DefaultPath);
    using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(setup.Ticket.TimeoutSeconds));
    var setupServer = new NamedPipeFluxVaultServer(setup, WindowsFluxVaultPipeServerFactory.ForSetup(),
        new WindowsFluxVaultCallerContextProvider(), new() { PendingListeners = 1, MaximumConcurrentRequests = 1 },
        stopAfterConnection: () => setup.Terminal);
    await setupServer.RunAsync(deadline.Token);
    if (setup.Result?.Success != true)
    {
        Console.Error.WriteLine(setup.Result?.ErrorMessage ?? "Setup ended without confirmation. Reconcile its protected bootstrap before another attempt.");
        Environment.ExitCode = 1;
    }
    return;
}

// No user-editable configuration, legacy profile or privileged source fallback.
// Missing or mismatched protected installation state fails before the service pipe opens.
await using var vault = await WindowsSingleVaultService.OpenAsync(WindowsVaultInstallation.DefaultPath);
var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddWindowsService(options => options.ServiceName = "FluxVaultService");
builder.Logging.AddEventLog(options =>
{
    options.LogName = "Application";
    options.SourceName = "FluxVaultService";
});
builder.Logging.AddFilter<EventLogLoggerProvider>(level => level >= LogLevel.Warning);
builder.Services.AddSingleton(_ => new NamedPipeFluxVaultServer(vault,
    WindowsFluxVaultPipeServerFactory.ForService(), new WindowsFluxVaultCallerContextProvider()));
builder.Services.AddSingleton<IFluxVaultServiceRuntime, FluxVaultServiceRuntime>();
builder.Services.AddSingleton<Worker>();
builder.Services.AddHostedService(provider => provider.GetRequiredService<Worker>());
using var host = builder.Build();
var worker = host.Services.GetRequiredService<Worker>();
try
{
    await host.StartAsync();
    await host.WaitForShutdownAsync();
}
finally
{
    // Also join a partly started/faulted host before either host or vault disposal.
    await worker.StopAsync(CancellationToken.None);
}
