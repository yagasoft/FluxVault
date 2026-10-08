using System.IO;
using FluxVault.Abstractions.Configuration;
using FluxVault.Abstractions.Ipc;
using FluxVault.Abstractions.Security;
using FluxVault.Abstractions.Storage;
using FluxVault.App.Services;
using FluxVault.App.ViewModels;
using FluxVault.Core.Ipc;

namespace FluxVault.App.Tests;

public sealed class SelectionRecoveryContractTests
{
    [Fact]
    public async Task New_file_destination_does_not_authorise_overwriting_a_late_entry()
    {
        using var fixture = new Fixture(); await fixture.View.RefreshAsync(); fixture.Select();
        await fixture.View.RestoreSelectedBrowserItemElsewhereCommand.ExecuteAsync(null);
        var request = Assert.Single(fixture.Client.Requests, request => request.Command == FluxVaultIpcCommand.RunRestoreSelection);
        Assert.False(request.OverwriteConfirmed); Assert.Equal(fixture.Client.Id, request.VaultId); Assert.Equal(1, request.ExpectedVaultRevision);
        Assert.Contains("Restored and verified", fixture.View.ServiceStatus);
        Assert.Contains("Publication warning", fixture.View.ServiceStatus); Assert.NotEmpty(fixture.View.FileBrowser.PendingChanges);
    }

    [Theory]
    [InlineData(Fault.NoVerification)]
    [InlineData(Fault.WrongSource)]
    [InlineData(Fault.WrongDestination)]
    [InlineData(Fault.WrongResultDestination)]
    [InlineData(Fault.WrongCount)]
    [InlineData(Fault.EmptyFileCount)]
    [InlineData(Fault.NegativeBytes)]
    [InlineData(Fault.NullFailures)]
    [InlineData(Fault.NullWarnings)]
    [InlineData(Fault.Cancelled)]
    [InlineData(Fault.Lost)]
    public async Task Unverified_or_uncertain_result_retains_drafts_without_false_verified_success(Fault fault)
    {
        using var fixture = new Fixture(); fixture.Client.Failure = fault; await fixture.View.RefreshAsync(); fixture.Select();
        await fixture.View.RestoreSelectedBrowserItemElsewhereCommand.ExecuteAsync(null);
        Assert.DoesNotContain("Restored and verified", fixture.View.ServiceStatus);
        Assert.Contains("could not be confirmed", fixture.View.ServiceStatus);
        var request = Assert.Single(fixture.Client.Requests, request => request.Command == FluxVaultIpcCommand.RunRestoreSelection);
        Assert.Contains(request.OperationId!.Value.ToString("D"), fixture.View.ServiceStatus);
        Assert.NotEmpty(fixture.View.FileBrowser.PendingChanges);
        Assert.DoesNotContain(fixture.Client.Requests, request => request.Command is FluxVaultIpcCommand.SaveConfiguration or FluxVaultIpcCommand.RunBackupNow);
    }

    [Fact]
    public async Task Refresh_failure_preserves_the_verified_result_and_publication_warning()
    {
        using var fixture = new Fixture(); await fixture.View.RefreshAsync(); fixture.Select(); fixture.Client.FailRefresh = true;
        await fixture.View.RestoreSelectedBrowserItemElsewhereCommand.ExecuteAsync(null);
        Assert.Contains("Restored and verified", fixture.View.ServiceStatus); Assert.Contains("Publication warning", fixture.View.ServiceStatus);
        Assert.Contains("Status refresh failed", fixture.View.ServiceStatus);
    }

    [Fact]
    public async Task Concurrent_selection_command_is_refused_while_recovery_is_running()
    {
        using var fixture = new Fixture(); fixture.Client.Hold = true; await fixture.View.RefreshAsync(); fixture.Select();
        var recovery = fixture.View.RestoreSelectedBrowserItemElsewhereCommand.ExecuteAsync(null);
        await fixture.Client.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            Assert.False(fixture.View.RestoreSelectedBrowserItemToOriginalCommand.CanExecute(null));
            await fixture.View.RestoreSelectedBrowserItemToOriginalCommand.ExecuteAsync(null);
            Assert.Single(fixture.Client.Requests, request => request.Command == FluxVaultIpcCommand.RunRestoreSelection);
        }
        finally { fixture.Client.Release.TrySetResult(); await recovery.WaitAsync(TimeSpan.FromSeconds(5)); }
        Assert.True(fixture.View.RestoreSelectedBrowserItemToOriginalCommand.CanExecute(null));
    }

    public enum Fault { None, NoVerification, WrongSource, WrongDestination, WrongResultDestination, WrongCount, EmptyFileCount, NegativeBytes, NullFailures, NullWarnings, Cancelled, Lost }
    private sealed class Fixture : IDisposable
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), "FluxVault.SelectionRecoveryTests", Guid.NewGuid().ToString("N"));
        internal Client Client { get; }
        internal MainWindowViewModel View { get; }
        internal Fixture()
        {
            Directory.CreateDirectory(root); Client = new(root);
            View = new(Client, TimeSpan.FromHours(1), new FileBrowserViewModel(new WindowsFileBrowserFileSystem()), new Controller(),
                new Picker(Client.Destination), new Confirmation());
        }
        internal void Select()
        {
            View.FileBrowser.SelectedFile = new(Client.Source, "working.docx", 10, true);
            View.FileBrowser.ReplaceSelectionRule(new("work", Path.Combine(root, "pending-work"), ProtectionSelectionMode.RecursiveFolder,
                FluxVault.Abstractions.Policies.CompressionPreference.Off, FluxVault.Abstractions.Policies.ResourceProfile.Balanced, true));
        }
        public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
    private sealed class Picker(string path) : IRestoreDestinationPicker { public string? PickDestination(VersionRow version) => path; }
    private sealed class Confirmation : IRestoreOverwriteConfirmation { public bool ConfirmOverwrite(string path) => true; }
    private sealed class Controller : IFluxVaultWindowsServiceController
    {
        public Task<FluxVaultWindowsServiceStatus> GetStatusAsync(CancellationToken cancellationToken = default) => Task.FromResult(new FluxVaultWindowsServiceStatus("fixture", FluxVaultWindowsServiceState.Running, "running"));
        public Task<FluxVaultWindowsServiceActionResult> StartAsync(CancellationToken cancellationToken = default) => throw new InvalidOperationException();
        public Task<FluxVaultWindowsServiceActionResult> StopAsync(CancellationToken cancellationToken = default) => throw new InvalidOperationException();
    }
    private sealed class Client(string root) : IFluxVaultServiceClient
    {
        internal string Source => Path.Combine(root, "working.docx"); internal string Destination => Path.Combine(root, "recovered.docx");
        internal VaultId Id { get; } = VaultId.New(); internal Fault Failure; internal bool Hold, FailRefresh;
        internal List<FluxVaultIpcRequest> Requests { get; } = [];
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<FluxVaultIpcResponse> SendAsync(FluxVaultIpcRequest request, CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            if (request.Command == FluxVaultIpcCommand.GetStatus)
            {
                if (FailRefresh && Requests.Any(item => item.Command == FluxVaultIpcCommand.RunRestoreSelection)) throw new IOException("Refresh unavailable");
                return FluxVaultIpcResponse.WithStatus(new(true, FluxVaultConfiguration.CreateDefault(root), "Fixture", null, [], [])) with { VaultId = Id, VaultRevision = 1 };
            }
            Assert.Equal(FluxVaultIpcCommand.RunRestoreSelection, request.Command);
            Entered.TrySetResult(); if (Hold) await Release.Task.WaitAsync(cancellationToken);
            if (Failure == Fault.Cancelled) throw new OperationCanceledException(); if (Failure == Fault.Lost) throw new IOException("Acknowledgement lost");
            var destination = request.DestinationPath!;
            return FluxVaultIpcResponse.WithRestoreSelection(new(Failure == Fault.WrongSource ? Path.Combine(root, "wrong.docx") : request.SourcePath!, request.IsDirectory,
                request.DestinationMode!.Value, Failure == Fault.WrongDestination ? root : destination, Failure == Fault.EmptyFileCount ? 0 : 1, 0, Failure == Fault.EmptyFileCount ? 0 : 1,
                Failure == Fault.NullFailures ? null! : [], Failure == Fault.NullWarnings ? null! : ["Publication warning"])) with
            {
                OutputPath = destination, VaultId = Id, VaultRevision = 1, OperationId = request.OperationId,
                RestoreResult = Failure == Fault.NoVerification ? null : new(Failure == Fault.WrongResultDestination ? root : destination,
                    Failure == Fault.NegativeBytes ? -1 : Failure == Fault.EmptyFileCount ? 0 : 10,
                    Failure == Fault.WrongCount ? 99 : Failure == Fault.EmptyFileCount ? 0 : 1, ["Publication warning"])
            };
        }
    }
}
