using FluxVault.Service;
using FluxVault.Abstractions.ChangeTracking;
using FluxVault.Abstractions.Capture;
using FluxVault.Core.Capture;
using FluxVault.Core.ChangeTracking;
using FluxVault.Core.Configuration;
using FluxVault.Core.Diagnostics;
using FluxVault.Core.Ipc;
using FluxVault.Core.Service;
using FluxVault.Windows.ChangeTracking;
using FluxVault.Windows.Capture;
using System.Runtime.Versioning;
using FluxVault.Abstractions.Configuration;
using Microsoft.Extensions.Logging.EventLog;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddWindowsService(options => options.ServiceName = "FluxVaultService");
if (OperatingSystem.IsWindows())
{
    AddWindowsEventLog(builder.Logging);
}
var programDataPath = Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
    "FluxVault");
var configPath = Path.Combine(programDataPath, "config.json");
var profileSetStore = new FileFluxVaultProfileSetStore(configPath, programDataPath);
var diagnosticsPolicyRuntime = new DiagnosticsPolicyRuntime(programDataPath);
var rollingFileLoggerProvider = new RollingJsonFileLoggerProvider(diagnosticsPolicyRuntime);
builder.Logging.AddProvider(rollingFileLoggerProvider);
builder.Logging.AddFilter<RollingJsonFileLoggerProvider>(_ => true);
builder.Logging.AddFilter<EventLogLoggerProvider>(level => level >= LogLevel.Warning);
builder.Services.AddSingleton<IFluxVaultProfileSetStore>(profileSetStore);
builder.Services.AddSingleton(diagnosticsPolicyRuntime);
builder.Services.AddSingleton(rollingFileLoggerProvider);
builder.Services.AddSingleton<TelemetryCollector>();
builder.Services.AddSingleton<IUsnChangeJournalReader, WindowsUsnChangeJournalReader>();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton(CapturePipelinePlanner.CreateDefault());
builder.Services.AddSingleton<IFileCaptureProvider>(
    _ => new FallbackFileCaptureProvider(new NormalFileCaptureProvider(), new WriterAwareVssCaptureProvider()));
builder.Services.AddSingleton(provider => new FluxVaultProfileManager(
    provider.GetRequiredService<IFluxVaultProfileSetStore>(),
    profile => CreateProfileRuntime(profile, provider, programDataPath),
    provider.GetRequiredService<DiagnosticsPolicyRuntime>(),
    provider.GetRequiredService<TelemetryCollector>()));
builder.Services.AddSingleton<IFluxVaultRequestHandler>(provider => provider.GetRequiredService<FluxVaultProfileManager>());
builder.Services.AddSingleton<NamedPipeFluxVaultServer>();
builder.Services.AddSingleton<IFluxVaultServiceRuntime, FluxVaultServiceRuntime>();
builder.Services.AddHostedService<Worker>();
builder.Services.AddHostedService<TelemetrySamplingService>();

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

static FluxVaultProfileRuntime CreateProfileRuntime(
    FluxVaultProfileConfiguration profile,
    IServiceProvider provider,
    string programDataPath)
{
    var profileSetStore = provider.GetRequiredService<IFluxVaultProfileSetStore>();
    var configurationStore = new FluxVaultProfileConfigurationStore(profileSetStore, profile.Id);
    var stateRoot = ProfileStateRoot(programDataPath, profile.Id);
    var maintenanceStateStore = new FileRepositoryMaintenanceStateStore(Path.Combine(stateRoot, "repository-maintenance.json"));
    var runtimeCoordinator = new ProtectionRuntimeCoordinator();
    var loggerFactory = provider.GetRequiredService<ILoggerFactory>();
    var telemetry = provider.GetRequiredService<TelemetryCollector>();
    var rollingLogger = provider.GetRequiredService<RollingJsonFileLoggerProvider>();
    var operations = new FluxVaultOperations(
        configurationStore,
        provider.GetRequiredService<IFileCaptureProvider>(),
        maintenanceStateStore,
        stateRoot,
        runtimeCoordinator: runtimeCoordinator,
        telemetryCollector: telemetry,
        logStatusFactory: rollingLogger.GetStatus);
    var checkpointStore = new FileUsnJournalCheckpointStore(Path.Combine(stateRoot, "usn-checkpoints.json"));
    var usnCatchUpService = new UsnCatchUpService(provider.GetRequiredService<IUsnChangeJournalReader>(), checkpointStore);
    return new FluxVaultProfileRuntime(
        profile.Id,
        operations,
        new FileSystemProtectionLoop(
            operations,
            configurationStore,
            usnCatchUpService,
            loggerFactory.CreateLogger<FileSystemProtectionLoop>(),
            runtimeCoordinator,
            telemetry),
        new RepositoryMaintenanceLoop(
            operations,
            configurationStore,
            maintenanceStateStore,
            provider.GetRequiredService<TimeProvider>(),
            loggerFactory.CreateLogger<RepositoryMaintenanceLoop>(),
            telemetry));
}

static string ProfileStateRoot(string programDataPath, string profileId)
{
    return string.Equals(profileId, FluxVaultProfileConfiguration.DefaultProfileId, StringComparison.OrdinalIgnoreCase)
        ? Path.Combine(programDataPath, "state")
        : Path.Combine(programDataPath, "profiles", SafePathSegment(profileId), "state");
}

static string SafePathSegment(string value)
{
    var invalid = Path.GetInvalidFileNameChars();
    var chars = value
        .Select(ch => invalid.Contains(ch) ? '_' : ch)
        .ToArray();
    var safe = new string(chars).Trim();
    return string.IsNullOrWhiteSpace(safe) ? "profile" : safe;
}
