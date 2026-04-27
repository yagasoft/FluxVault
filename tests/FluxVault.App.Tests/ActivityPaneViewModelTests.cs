using FluxVault.Abstractions.Ipc;
using FluxVault.App.ViewModels;
using FluxVault.Core.Ipc;

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
        public List<FluxVaultIpcCommand> Commands { get; } = [];

        public Task<FluxVaultIpcResponse> SendAsync(FluxVaultIpcRequest request, CancellationToken cancellationToken = default)
        {
            Commands.Add(request.Command);
            return request.Command switch
            {
                FluxVaultIpcCommand.GetActivity => Task.FromResult(FluxVaultIpcResponse.WithActivity(activity)),
                FluxVaultIpcCommand.ListBlockedFiles => Task.FromResult(FluxVaultIpcResponse.WithBlockedFiles(blockedFiles)),
                _ => Task.FromResult(FluxVaultIpcResponse.Failure($"Unexpected command {request.Command}"))
            };
        }
    }
}
