using FluxVault.Service;
using FluxVault.Abstractions.ChangeTracking;
using FluxVault.Abstractions.Capture;
using FluxVault.Core.Capture;
using FluxVault.Core.ChangeTracking;
using FluxVault.Core.Configuration;
using FluxVault.Core.Ipc;
using FluxVault.Core.Service;
using FluxVault.Windows.ChangeTracking;
using FluxVault.Windows.Capture;
using System.Runtime.Versioning;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddWindowsService(options => options.ServiceName = "FluxVaultService");
if (OperatingSystem.IsWindows())
{
    AddWindowsEventLog(builder.Logging);
}
var programDataPath = Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
    "FluxVault");
builder.Services.AddSingleton<IFluxVaultConfigurationStore>(
    new FileFluxVaultConfigurationStore(Path.Combine(programDataPath, "config.json"), programDataPath));
builder.Services.AddSingleton<IUsnJournalCheckpointStore>(
    new FileUsnJournalCheckpointStore(Path.Combine(programDataPath, "state", "usn-checkpoints.json")));
builder.Services.AddSingleton<IUsnChangeJournalReader, WindowsUsnChangeJournalReader>();
builder.Services.AddSingleton<UsnCatchUpService>();
builder.Services.AddSingleton(CapturePipelinePlanner.CreateDefault());
builder.Services.AddSingleton<IFileCaptureProvider>(
    _ => new FallbackFileCaptureProvider(new NormalFileCaptureProvider(), new VssAdminCaptureProvider()));
builder.Services.AddSingleton<FluxVaultOperations>();
builder.Services.AddSingleton<IFluxVaultRequestHandler>(provider => provider.GetRequiredService<FluxVaultOperations>());
builder.Services.AddSingleton<NamedPipeFluxVaultServer>();
builder.Services.AddSingleton<FileSystemProtectionLoop>();
builder.Services.AddSingleton<IFluxVaultServiceRuntime, FluxVaultServiceRuntime>();
builder.Services.AddHostedService<Worker>();

var host = builder.Build();
host.Run();

[SupportedOSPlatform("windows")]
static void AddWindowsEventLog(ILoggingBuilder logging)
{
    logging.AddEventLog(options =>
    {
        options.LogName = "Application";
        options.SourceName = "FluxVaultService";
    });
}
