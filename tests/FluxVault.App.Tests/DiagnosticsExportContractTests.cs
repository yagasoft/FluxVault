using System.IO;
using FluxVault.Abstractions.Configuration;
using FluxVault.Abstractions.Ipc;
using FluxVault.Abstractions.Security;
using FluxVault.App.Services;
using FluxVault.App.ViewModels;
using FluxVault.Core.Ipc;

namespace FluxVault.App.Tests;

public sealed class DiagnosticsExportContractTests
{
    [Fact]
    public async Task Cancelled_picker_dispatches_nothing_and_retains_pending_protection_edits()
    {
        var client = new Client(); var view = Create(client, new Picker(null));
        await view.RefreshAsync(); Edit(view);
        await view.ExportDiagnosticsCommand.ExecuteAsync(null);
        Assert.All(client.Requests, request => Assert.Equal(FluxVaultIpcCommand.GetStatus, request.Command));
        Assert.NotEmpty(view.FileBrowser.PendingChanges);
    }

    [Fact]
    public async Task Confirmed_export_uses_captured_binding_and_reports_publication_warning_without_saving_drafts()
    {
        var client = new Client(); var view = Create(client, new Picker(@"C:\owned"));
        await view.RefreshAsync(); Edit(view);
        await view.ExportDiagnosticsCommand.ExecuteAsync(null);
        var request = Assert.Single(client.Requests, request => request.Command == FluxVaultIpcCommand.ExportDiagnostics);
        Assert.Equal(client.Id, request.VaultId); Assert.Equal(1, request.ExpectedVaultRevision); Assert.NotEqual(Guid.Empty, request.OperationId);
        Assert.Contains($"fluxvault-diagnostics-{request.OperationId:N}.json", view.DiagnosticsText);
        Assert.Contains("Permissions need review.", view.DiagnosticsText);
        Assert.NotEmpty(view.FileBrowser.PendingChanges);
        Assert.DoesNotContain(client.Requests, request => request.Command is FluxVaultIpcCommand.SaveConfiguration or FluxVaultIpcCommand.RunBackupNow);
    }

    [Theory]
    [InlineData(Fault.WrongOuterPath)]
    [InlineData(Fault.WrongResultPath)]
    [InlineData(Fault.NullWarnings)]
    [InlineData(Fault.WrongOperation)]
    [InlineData(Fault.WrongRevision)]
    [InlineData(Fault.Unknown)]
    [InlineData(Fault.Io)]
    [InlineData(Fault.Cancelled)]
    public async Task Malformed_or_lost_acknowledgement_never_claims_success_or_dispatches_a_dependent_command(Fault fault)
    {
        var client = new Client { Failure = fault }; var view = Create(client, new Picker(@"C:\owned"));
        await view.RefreshAsync(); Edit(view);
        await view.ExportDiagnosticsCommand.ExecuteAsync(null);
        Assert.DoesNotContain("Diagnostics exported to", view.DiagnosticsText);
        Assert.Contains("could not be confirmed", view.DiagnosticsText);
        var request = Assert.Single(client.Requests, request => request.Command == FluxVaultIpcCommand.ExportDiagnostics);
        Assert.Contains(request.OperationId!.Value.ToString("D"), view.DiagnosticsText);
        Assert.NotEmpty(view.FileBrowser.PendingChanges);
        Assert.DoesNotContain(client.Requests, request => request.Command is FluxVaultIpcCommand.SaveConfiguration or FluxVaultIpcCommand.RunBackupNow);
    }

    [Fact]
    public async Task Delayed_acknowledgement_is_checked_against_original_request_despite_newer_status()
    {
        var client = new Client { Hold = true }; var view = Create(client, new Picker(@"C:\owned"));
        await view.RefreshAsync();
        var export = view.ExportDiagnosticsCommand.ExecuteAsync(null);
        await client.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try { client.Revision = 2; await view.RefreshAsync(); }
        finally { client.Release.TrySetResult(); await export.WaitAsync(TimeSpan.FromSeconds(5)); }
        Assert.Contains("Diagnostics exported to", view.DiagnosticsText);
        Assert.Equal(1, Assert.Single(client.Requests, request => request.Command == FluxVaultIpcCommand.ExportDiagnostics).ExpectedVaultRevision);
    }

    [Fact]
    public async Task Busy_export_refuses_concurrent_command_and_restores_availability_after_completion()
    {
        var client = new Client { Hold = true }; var view = Create(client, new Picker(@"C:\owned"));
        await view.RefreshAsync();
        var export = view.ExportDiagnosticsCommand.ExecuteAsync(null);
        await client.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            Assert.False(view.CanExportDiagnostics);
            Assert.False(view.ExportDiagnosticsCommand.CanExecute(null));
            await view.ExportDiagnosticsCommand.ExecuteAsync(null);
            Assert.Single(client.Requests, request => request.Command == FluxVaultIpcCommand.ExportDiagnostics);
        }
        finally { client.Release.TrySetResult(); await export.WaitAsync(TimeSpan.FromSeconds(5)); }
        Assert.True(view.CanExportDiagnostics);
    }

    public enum Fault { None, WrongOuterPath, WrongResultPath, NullWarnings, WrongOperation, WrongRevision, Unknown, Io, Cancelled }
    private static void Edit(MainWindowViewModel view) => view.FileBrowser.ReplaceSelectionRule(new("work", @"C:\pending-work",
        ProtectionSelectionMode.RecursiveFolder,
        FluxVault.Abstractions.Policies.CompressionPreference.Off, FluxVault.Abstractions.Policies.ResourceProfile.Balanced, true));
    private static MainWindowViewModel Create(Client client, Picker picker) => new(client, TimeSpan.FromHours(1),
        new FileBrowserViewModel(new WindowsFileBrowserFileSystem()), new Controller(), new Destination(), new Confirmation(), diagnosticsExportFolderPicker: picker);
    private sealed class Picker(string? path) : IDiagnosticsExportFolderPicker { public string? PickFolder() => path; }
    private sealed class Destination : IRestoreDestinationPicker { public string? PickDestination(VersionRow version) => throw new InvalidOperationException(); }
    private sealed class Confirmation : IRestoreOverwriteConfirmation { public bool ConfirmOverwrite(string path) => throw new InvalidOperationException(); }
    private sealed class Controller : IFluxVaultWindowsServiceController
    {
        public Task<FluxVaultWindowsServiceStatus> GetStatusAsync(CancellationToken cancellationToken = default) => Task.FromResult(new FluxVaultWindowsServiceStatus("fixture", FluxVaultWindowsServiceState.Running, "running"));
        public Task<FluxVaultWindowsServiceActionResult> StartAsync(CancellationToken cancellationToken = default) => throw new InvalidOperationException();
        public Task<FluxVaultWindowsServiceActionResult> StopAsync(CancellationToken cancellationToken = default) => throw new InvalidOperationException();
    }
    private sealed class Client : IFluxVaultServiceClient
    {
        internal VaultId Id { get; } = VaultId.New(); internal long Revision = 1; internal Fault Failure; internal bool Hold;
        internal List<FluxVaultIpcRequest> Requests { get; } = [];
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<FluxVaultIpcResponse> SendAsync(FluxVaultIpcRequest request, CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            if (request.Command == FluxVaultIpcCommand.GetStatus) return FluxVaultIpcResponse.WithStatus(new(true,
                FluxVaultConfiguration.CreateDefault(@"C:\fixture"), "Fixture", null, [], [])) with { VaultId = Id, VaultRevision = Revision };
            Assert.Equal(FluxVaultIpcCommand.ExportDiagnostics, request.Command);
            Entered.TrySetResult(); if (Hold) await Release.Task.WaitAsync(cancellationToken);
            if (Failure == Fault.Io) throw new IOException("Acknowledgement lost");
            if (Failure == Fault.Cancelled) throw new OperationCanceledException();
            if (Failure == Fault.Unknown) return FluxVaultIpcResponse.Failure("Outcome could not be confirmed.") with { ErrorCode = FluxVaultIpcErrorCode.OutcomeUnknown };
            var path = Path.Combine(request.ExportPath!, $"fluxvault-diagnostics-{request.OperationId:N}.json");
            return FluxVaultIpcResponse.WithOutputPath(Failure == Fault.WrongOuterPath ? @"C:\elsewhere\wrong.json" : path) with
            {
                DiagnosticsExport = new(Failure == Fault.WrongResultPath ? @"C:\elsewhere\wrong.json" : path, Failure == Fault.NullWarnings ? null! : ["Permissions need review."]),
                VaultId = Id, VaultRevision = Failure == Fault.WrongRevision ? 99 : request.ExpectedVaultRevision,
                OperationId = Failure == Fault.WrongOperation ? Guid.NewGuid() : request.OperationId
            };
        }
    }
}
