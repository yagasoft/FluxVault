using System.Diagnostics;
using FluxVault.Abstractions.Configuration;
using FluxVault.Abstractions.Ipc;
using FluxVault.Core.Ipc;

namespace FluxVault.Core.Tests;

public sealed class NamedPipeFluxVaultServerConcurrencyTests
{
    [Fact]
    public async Task Get_status_returns_while_backup_request_is_running()
    {
        var pipeName = $"FluxVault.Tests.{Guid.NewGuid():N}";
        var handler = new BlockingBackupHandler();
        var server = new NamedPipeFluxVaultServer(handler, pipeName);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var serverTask = server.RunAsync(cancellation.Token);
        var client = new NamedPipeFluxVaultClient(pipeName);

        var backupTask = client.SendAsync(FluxVaultIpcRequest.RunBackupNow(), cancellation.Token);
        await handler.BackupStarted.Task.WaitAsync(TimeSpan.FromSeconds(2), cancellation.Token);
        if (serverTask.IsFaulted)
        {
            throw serverTask.Exception;
        }

        var stopwatch = Stopwatch.StartNew();
        var status = await client.SendAsync(FluxVaultIpcRequest.GetStatus(), cancellation.Token)
            .WaitAsync(TimeSpan.FromSeconds(1), cancellation.Token);

        handler.ReleaseBackup();
        await backupTask.WaitAsync(TimeSpan.FromSeconds(2), cancellation.Token);
        await cancellation.CancelAsync();
        await serverTask.WaitAsync(TimeSpan.FromSeconds(2), CancellationToken.None);

        Assert.True(status.Success);
        Assert.NotNull(status.Status);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task Unexpected_handler_exception_returns_failure_response()
    {
        var pipeName = $"FluxVault.Tests.{Guid.NewGuid():N}";
        var server = new NamedPipeFluxVaultServer(new ThrowingHandler(), pipeName);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var serverTask = server.RunAsync(cancellation.Token);
        var client = new NamedPipeFluxVaultClient(pipeName);

        var response = await client.SendAsync(FluxVaultIpcRequest.GetStatus(), cancellation.Token)
            .WaitAsync(TimeSpan.FromSeconds(2), cancellation.Token);

        await cancellation.CancelAsync();
        await serverTask.WaitAsync(TimeSpan.FromSeconds(2), CancellationToken.None);

        Assert.False(response.Success);
        Assert.Contains("metadata offline", response.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    private sealed class ThrowingHandler : IFluxVaultRequestHandler
    {
        public Task<FluxVaultIpcResponse> HandleAsync(
            FluxVaultIpcRequest request,
            CancellationToken cancellationToken = default)
        {
            throw new InvalidOperationException("metadata offline");
        }
    }

    private sealed class BlockingBackupHandler : IFluxVaultRequestHandler
    {
        private readonly TaskCompletionSource releaseBackup = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource BackupStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<FluxVaultIpcResponse> HandleAsync(
            FluxVaultIpcRequest request,
            CancellationToken cancellationToken = default)
        {
            if (request.Command == FluxVaultIpcCommand.RunBackupNow)
            {
                BackupStarted.SetResult();
                await releaseBackup.Task.WaitAsync(cancellationToken);
                return FluxVaultIpcResponse.WithBackup(new BackupRunSummary(
                    Success: true,
                    Message: "Backup completed.",
                    CapturedFileCount: 0,
                    FailedFileCount: 0,
                    CompletedAtUtc: DateTimeOffset.UtcNow));
            }

            return FluxVaultIpcResponse.WithStatus(new FluxVaultServiceStatus(
                IsServiceRunning: true,
                Configuration: FluxVaultConfiguration.CreateDefault(@"D:\Vault"),
                LastMessage: "Running",
                LastCaptureUtc: null,
                WatchedFolders: [],
                RecentVersions: []));
        }

        public void ReleaseBackup()
        {
            releaseBackup.SetResult();
        }
    }
}
