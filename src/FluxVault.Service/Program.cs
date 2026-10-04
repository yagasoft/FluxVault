using FluxVault.Service;
using FluxVault.Core.Ipc;
using FluxVault.Windows.Security;
using Microsoft.Extensions.Logging.EventLog;

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
