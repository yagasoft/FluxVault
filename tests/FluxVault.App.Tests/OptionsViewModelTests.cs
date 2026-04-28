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
    public async Task Options_can_add_and_save_exclusion_regex_rules()
    {
        var client = new FakeFluxVaultServiceClient(StatusWithPolicy(RetentionPolicy.CreateDefault()));
        var viewModel = new OptionsViewModel(client);
        await viewModel.InitialiseAsync();
        viewModel.NewExclusionLabel = "Build output";
        viewModel.NewExclusionPattern = @"\\bin(\\|$)";
        viewModel.NewExclusionTarget = ProtectionExclusionTarget.Folder;

        viewModel.AddExclusionRuleCommand.Execute(null);
        await viewModel.SaveAsync();

        var saved = Assert.Single(client.SavedConfigurations);
        var rule = Assert.Single(saved.ExclusionRules);
        Assert.Equal("Build output", rule.Label);
        Assert.Equal(ProtectionExclusionTarget.Folder, rule.Target);
        Assert.True(rule.IsEnabled);
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
        var explorerContextMenu = new FakeExplorerContextMenuService(false, "Explorer context menu is not registered.");
        var viewModel = new OptionsViewModel(client, explorerContextMenu);

        await viewModel.InitialiseAsync();
        viewModel.RegisterExplorerContextMenuCommand.Execute(null);
        viewModel.UnregisterExplorerContextMenuCommand.Execute(null);

        Assert.Equal(1, explorerContextMenu.RegisterCount);
        Assert.Equal(1, explorerContextMenu.UnregisterCount);
        Assert.False(viewModel.IsExplorerContextMenuRegistered);
        Assert.Contains("unregistered", viewModel.ExplorerContextMenuStatus, StringComparison.OrdinalIgnoreCase);
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

    private sealed class FakeExplorerContextMenuService(bool isRegistered, string message) : IExplorerContextMenuService
    {
        private ExplorerContextMenuStatus status = new(isRegistered, message);

        public int RegisterCount { get; private set; }

        public int UnregisterCount { get; private set; }

        public ExplorerContextMenuStatus GetStatus()
        {
            return status;
        }

        public ExplorerContextMenuStatus Register()
        {
            RegisterCount++;
            status = new ExplorerContextMenuStatus(true, "Explorer context menu registered.");
            return status;
        }

        public ExplorerContextMenuStatus Unregister()
        {
            UnregisterCount++;
            status = new ExplorerContextMenuStatus(false, "Explorer context menu unregistered.");
            return status;
        }
    }
}
