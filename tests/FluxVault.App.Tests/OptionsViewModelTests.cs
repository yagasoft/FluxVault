using FluxVault.Abstractions.Configuration;
using FluxVault.Abstractions.Ipc;
using FluxVault.Abstractions.Policies;
using FluxVault.Abstractions.Storage;
using FluxVault.App.Services;
using FluxVault.App.ViewModels;
using FluxVault.Core.Ipc;

namespace FluxVault.App.Tests;

public sealed class OptionsViewModelTests
{
    [Fact]
    public async Task Initialise_loads_retention_policy_from_service_status()
    {
        var policy = new RetentionPolicy(
            IsEnabled: false,
            KeepAllFor: TimeSpan.FromHours(12),
            KeepHourlyFor: TimeSpan.FromDays(10),
            KeepDailyFor: TimeSpan.FromDays(90),
            MinimumVersionsPerFile: 7);
        var client = new FakeFluxVaultServiceClient(StatusWithPolicy(policy));
        var viewModel = new OptionsViewModel(client);

        await viewModel.InitialiseAsync();

        Assert.False(viewModel.RetentionEnabled);
        Assert.Equal(12, viewModel.KeepAllHours);
        Assert.Equal(10, viewModel.KeepHourlyDays);
        Assert.Equal(90, viewModel.KeepDailyDays);
        Assert.Equal(7, viewModel.MinimumVersionsPerFile);
    }

    [Fact]
    public async Task Save_persists_retention_policy_without_real_named_pipe()
    {
        var client = new FakeFluxVaultServiceClient(StatusWithPolicy(RetentionPolicy.CreateDefault()));
        var viewModel = new OptionsViewModel(client);
        await viewModel.InitialiseAsync();
        viewModel.RetentionEnabled = false;
        viewModel.KeepAllHours = 6;
        viewModel.KeepHourlyDays = 14;
        viewModel.KeepDailyDays = 60;
        viewModel.MinimumVersionsPerFile = 9;

        await viewModel.SaveAsync();

        var saved = Assert.Single(client.SavedConfigurations);
        Assert.False(saved.RetentionPolicy.IsEnabled);
        Assert.Equal(TimeSpan.FromHours(6), saved.RetentionPolicy.KeepAllFor);
        Assert.Equal(TimeSpan.FromDays(14), saved.RetentionPolicy.KeepHourlyFor);
        Assert.Equal(TimeSpan.FromDays(60), saved.RetentionPolicy.KeepDailyFor);
        Assert.Equal(9, saved.RetentionPolicy.MinimumVersionsPerFile);
        Assert.Contains("saved", viewModel.StatusText);
    }

    [Fact]
    public async Task Save_persists_capture_cadence_and_codec_policy()
    {
        var client = new FakeFluxVaultServiceClient(StatusWithPolicy(RetentionPolicy.CreateDefault()));
        var viewModel = new OptionsViewModel(client);
        await viewModel.InitialiseAsync();
        viewModel.WatcherPollSeconds = 4;
        viewModel.PeriodicReconciliationMinutes = 9;
        viewModel.BalancedDebounceSeconds = 7;
        viewModel.BalancedMaxHotFileDelayMinutes = 3;
        viewModel.MinimumSameFileCaptureIntervalSeconds = 11;
        viewModel.MaximumConcurrentCaptures = 2;
        viewModel.CodecProfile = CodecProfile.Ratio;
        viewModel.DefaultCodec = CompressionPreference.Brotli;
        viewModel.HotFileCodec = CompressionPreference.Lz4;
        viewModel.CodecMinimumKb = 128;

        await viewModel.SaveAsync();

        var saved = Assert.Single(client.SavedConfigurations);
        Assert.Equal(TimeSpan.FromSeconds(4), saved.CaptureCadencePolicy.WatcherPollInterval);
        Assert.Equal(TimeSpan.FromMinutes(9), saved.CaptureCadencePolicy.PeriodicReconciliationInterval);
        Assert.Equal(TimeSpan.FromSeconds(7), saved.CaptureCadencePolicy.BalancedDebounce);
        Assert.Equal(TimeSpan.FromMinutes(3), saved.CaptureCadencePolicy.BalancedMaxHotFileDelay);
        Assert.Equal(TimeSpan.FromSeconds(11), saved.CaptureCadencePolicy.MinimumSameFileCaptureInterval);
        Assert.Equal(2, saved.CaptureCadencePolicy.MaximumConcurrentCaptures);
        Assert.Equal(CodecProfile.Ratio, saved.CodecPolicy.Profile);
        Assert.Equal(CompressionPreference.Brotli, saved.CodecPolicy.Codec);
        Assert.Equal(CompressionPreference.Lz4, saved.CodecPolicy.HotFileOverride);
        Assert.Equal(128 * 1024, saved.CodecPolicy.MinimumBytes);
    }

    [Fact]
    public async Task Save_removes_old_global_exclusion_rules_from_options_payload()
    {
        var existingRule = new ProtectionExclusionRule(
            "legacy",
            @"\\bin(\\|$)",
            ProtectionExclusionTarget.Folder,
            IsEnabled: true,
            Label: "Build output");
        var client = new FakeFluxVaultServiceClient(StatusWithPolicy(RetentionPolicy.CreateDefault()) with
        {
            Configuration = StatusWithPolicy(RetentionPolicy.CreateDefault()).Configuration with
            {
                ExclusionRules = [existingRule]
            }
        });
        var viewModel = new OptionsViewModel(client);
        await viewModel.InitialiseAsync();

        await viewModel.SaveAsync();

        var saved = Assert.Single(client.SavedConfigurations);
        Assert.Empty(saved.ExclusionRules);
    }

    [Fact]
    public async Task Preview_and_run_retention_show_service_results()
    {
        var preview = new RepositoryRetentionPreview(
            Decisions: [],
            KeptVersionCount: 42,
            PrunableVersionCount: 8,
            EstimatedReclaimableBytes: 1024,
            RepositorySizeBytes: 4096);
        var result = new RepositoryRetentionResult(
            Decisions: [],
            KeptVersionCount: 42,
            PrunedVersionCount: 8,
            DeletedChunkCount: 3,
            ReclaimedBytes: 1024,
            RepositorySizeBytes: 2048,
            MirrorWarnings: []);
        var client = new FakeFluxVaultServiceClient(StatusWithPolicy(RetentionPolicy.CreateDefault()), preview, result);
        var viewModel = new OptionsViewModel(client);
        await viewModel.InitialiseAsync();

        await viewModel.PreviewRetentionAsync();
        await viewModel.RunRetentionNowAsync();

        Assert.Contains("8 version(s)", viewModel.PreviewText);
        Assert.Contains("1.0 KB", viewModel.PreviewText);
        Assert.Contains("pruned 8", viewModel.StatusText);
        Assert.Contains(FluxVaultIpcCommand.PreviewRetention, client.Commands);
        Assert.Contains(FluxVaultIpcCommand.RunRetentionNow, client.Commands);
    }

    [Fact]
    public async Task Explorer_context_menu_can_be_registered_and_unregistered_from_options()
    {
        var client = new FakeFluxVaultServiceClient(StatusWithPolicy(RetentionPolicy.CreateDefault()));
        var explorerContextMenu = new FakeExplorerContextMenuService(
            new ExplorerContextMenuStatus(
                IsRegistered: false,
                IsClassicRegistered: false,
                IsCompactRegistered: false,
                Message: "Explorer context menu is not registered."));
        var viewModel = new OptionsViewModel(client, explorerContextMenu);

        await viewModel.InitialiseAsync();
        viewModel.RegisterExplorerContextMenuCommand.Execute(null);
        viewModel.UnregisterExplorerContextMenuCommand.Execute(null);

        Assert.Equal(1, explorerContextMenu.RegisterCount);
        Assert.Equal(1, explorerContextMenu.UnregisterCount);
        Assert.False(viewModel.IsExplorerContextMenuRegistered);
        Assert.Contains("unregistered", viewModel.ExplorerContextMenuStatus, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Explorer_context_menu_status_reports_classic_and_compact_paths()
    {
        var client = new FakeFluxVaultServiceClient(StatusWithPolicy(RetentionPolicy.CreateDefault()));
        var explorerContextMenu = new FakeExplorerContextMenuService(
            new ExplorerContextMenuStatus(
                IsRegistered: true,
                IsClassicRegistered: true,
                IsCompactRegistered: false,
                Message: "Full menu registered. Windows 11 compact menu unavailable without app identity."));
        var viewModel = new OptionsViewModel(client, explorerContextMenu);

        Assert.True(viewModel.IsExplorerContextMenuRegistered);
        Assert.Contains("Full menu", viewModel.ExplorerContextMenuStatus);
        Assert.Contains("compact", viewModel.ExplorerContextMenuStatus, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Windows_explorer_context_menu_status_uses_compact_package_detection()
    {
        var compactRegistration = new FakeCompactExplorerContextMenuRegistration(isRegistered: true);
        var explorerContextMenu = new WindowsExplorerContextMenuService(
            @"C:\FluxVault\FluxVault.App.exe",
            compactRegistration);

        var status = explorerContextMenu.GetStatus();

        Assert.True(status.IsRegistered);
        Assert.False(status.IsClassicRegistered);
        Assert.True(status.IsCompactRegistered);
        Assert.Contains("compact", status.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("unavailable", status.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static FluxVaultServiceStatus StatusWithPolicy(RetentionPolicy policy)
    {
        return new FluxVaultServiceStatus(
            IsServiceRunning: true,
            Configuration: FluxVaultConfiguration.CreateDefault(@"D:\Vault") with
            {
                RetentionPolicy = policy
            },
            LastMessage: "Ready",
            LastCaptureUtc: null,
            WatchedFolders: [],
            RecentVersions: []);
    }

    private sealed class FakeFluxVaultServiceClient(
        FluxVaultServiceStatus status,
        RepositoryRetentionPreview? preview = null,
        RepositoryRetentionResult? result = null) : IFluxVaultServiceClient
    {
        public List<FluxVaultIpcCommand> Commands { get; } = [];

        public List<FluxVaultConfiguration> SavedConfigurations { get; } = [];

        public Task<FluxVaultIpcResponse> SendAsync(FluxVaultIpcRequest request, CancellationToken cancellationToken = default)
        {
            Commands.Add(request.Command);
            return request.Command switch
            {
                FluxVaultIpcCommand.GetStatus => Task.FromResult(FluxVaultIpcResponse.WithStatus(status)),
                FluxVaultIpcCommand.SaveConfiguration => SaveConfigurationAsync(request),
                FluxVaultIpcCommand.PreviewRetention => Task.FromResult(FluxVaultIpcResponse.WithRetentionPreview(
                    preview ?? new RepositoryRetentionPreview([], 0, 0, 0, 0))),
                FluxVaultIpcCommand.RunRetentionNow => Task.FromResult(FluxVaultIpcResponse.WithRetentionResult(
                    result ?? new RepositoryRetentionResult([], 0, 0, 0, 0, 0, []))),
                _ => Task.FromResult(FluxVaultIpcResponse.Failure($"Unexpected command {request.Command}"))
            };
        }

        private Task<FluxVaultIpcResponse> SaveConfigurationAsync(FluxVaultIpcRequest request)
        {
            SavedConfigurations.Add(request.Configuration ?? throw new InvalidOperationException("Missing configuration."));
            return Task.FromResult(FluxVaultIpcResponse.Ok());
        }
    }

    private sealed class FakeExplorerContextMenuService(ExplorerContextMenuStatus status) : IExplorerContextMenuService
    {
        public int RegisterCount { get; private set; }

        public int UnregisterCount { get; private set; }

        public ExplorerContextMenuStatus GetStatus()
        {
            return status;
        }

        public ExplorerContextMenuStatus Register()
        {
            RegisterCount++;
            status = new ExplorerContextMenuStatus(
                IsRegistered: true,
                IsClassicRegistered: true,
                IsCompactRegistered: true,
                Message: "Explorer context menu registered for full and compact menus.");
            return status;
        }

        public ExplorerContextMenuStatus Unregister()
        {
            UnregisterCount++;
            status = new ExplorerContextMenuStatus(
                IsRegistered: false,
                IsClassicRegistered: false,
                IsCompactRegistered: false,
                Message: "Explorer context menu unregistered.");
            return status;
        }
    }

    private sealed class FakeCompactExplorerContextMenuRegistration(bool isRegistered) : ICompactExplorerContextMenuRegistration
    {
        public bool IsRegistered()
        {
            return isRegistered;
        }

        public bool TryRegister(out string message)
        {
            isRegistered = true;
            message = "Windows 11 compact menu registration is active.";
            return true;
        }

        public bool TryUnregister(out string message)
        {
            isRegistered = false;
            message = "Windows 11 compact menu registration is inactive.";
            return true;
        }
    }
}
