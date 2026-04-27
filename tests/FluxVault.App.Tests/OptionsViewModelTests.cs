using FluxVault.Abstractions.Configuration;
using FluxVault.Abstractions.Ipc;
using FluxVault.Abstractions.Policies;
using FluxVault.Abstractions.Storage;
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
}
