using FluxVault.Abstractions.Ipc;
using FluxVault.App.ViewModels;
using FluxVault.Core.Ipc;
using FluxVault.Abstractions.Security;
using FluxVault.Abstractions.Configuration;

namespace FluxVault.App.Tests;

public sealed class ActivityPaneViewModelTests
{
    [Fact]
    public async Task Refresh_loads_activity_and_blocked_files()
    {
        var activity = new FluxVaultActivityEvent(
            DateTimeOffset.UtcNow,
            FluxVaultActivityKind.Capturing,
            "Capturing",
            "draft.txt",
            @"D:\Work\draft.txt");
        var blocked = new CaptureRuntimeStatus(
            @"D:\Work\blocked.txt",
            "docs",
            CaptureRuntimeState.Blocked,
            DateTimeOffset.UtcNow,
            null,
            DateTimeOffset.UtcNow,
            null,
            "File is locked.",
            null,
            1);
        var client = new FakeFluxVaultServiceClient([activity], [blocked]);
        var viewModel = new ActivityPaneViewModel(client);

        await viewModel.RefreshAsync();

        Assert.Equal("Capturing", Assert.Single(viewModel.Events).Title);
        Assert.Equal(@"D:\Work\blocked.txt", Assert.Single(viewModel.BlockedFiles).SourcePath);
        Assert.Contains(FluxVaultIpcCommand.GetActivity, client.Commands);
        Assert.Contains(FluxVaultIpcCommand.ListBlockedFiles, client.Commands);
    }

    private sealed class FakeFluxVaultServiceClient(
        IReadOnlyList<FluxVaultActivityEvent> activity,
        IReadOnlyList<CaptureRuntimeStatus> blockedFiles) : IFluxVaultServiceClient
    {
        private readonly VaultId identity = VaultId.New();
        public List<FluxVaultIpcCommand> Commands { get; } = [];

        public Task<FluxVaultIpcResponse> SendAsync(FluxVaultIpcRequest request, CancellationToken cancellationToken = default)
        {
            Commands.Add(request.Command);
            var response = request.Command switch
            {
                FluxVaultIpcCommand.GetStatus => FluxVaultIpcResponse.WithStatus(new(true, FluxVaultConfiguration.CreateDefault(@"C:\Fixture"), "Ready", null, [], [])),
                FluxVaultIpcCommand.GetActivity => FluxVaultIpcResponse.WithActivity(activity),
                FluxVaultIpcCommand.ListBlockedFiles => FluxVaultIpcResponse.WithBlockedFiles(blockedFiles),
                _ => FluxVaultIpcResponse.Failure($"Unexpected command {request.Command}")
            };
            return Task.FromResult(response with { VaultId = identity, VaultRevision = 1 });
        }
    }
}
