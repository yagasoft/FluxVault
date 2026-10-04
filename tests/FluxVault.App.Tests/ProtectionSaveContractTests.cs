using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using FluxVault.App;
using FluxVault.Abstractions.Configuration;
using FluxVault.Abstractions.Ipc;
using FluxVault.Abstractions.Security;
using FluxVault.Abstractions.Policies;
using FluxVault.Abstractions.Storage;
using FluxVault.App.Services;
using FluxVault.App.ViewModels;
using FluxVault.Core.Configuration;
using FluxVault.Core.Ipc;
using FluxVault.Core.Security;

namespace FluxVault.App.Tests;

// Exercises public view-model commands through the production configuration stores.
public sealed class ProtectionSaveContractTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Unsupported_save_through_actual_dispatcher_keeps_real_store_and_drafts_without_false_uncertainty(bool profileStore, bool optionsFlow)
    {
        using var fixture = new StoreFixture(profileStore);
        await fixture.Store.SaveAsync(NonDefaultConfiguration(fixture.Root));
        var original = await fixture.Store.LoadAsync();
        var boundary = new RefusedCommandBoundary();
        var handler = new AuthenticatedFluxVaultRequestHandler(boundary, boundary);
        var client = new StoreClient(fixture.Store)
        {
            SaveDispatchHandler = async (request, token) =>
            {
                using var caller = new BoundaryCaller();
                return await handler.HandleAsync(caller, request, token);
            }
        };
        var store = new FileConfigurationSaveOperationStore(Path.Combine(fixture.Root, "pending-protection-save.json"));
        if (optionsFlow)
        {
            var options = new OptionsViewModel(client, store);
            await options.InitialiseAsync(); options.MinimumVersionsPerFile = 31;
            await options.SaveCommand.ExecuteAsync(null);
            Assert.False(options.HasUnconfirmedSave);
            Assert.Equal(31, options.MinimumVersionsPerFile);
            Assert.Contains("failed", options.StatusText);
            Assert.True(options.CanSaveOptions);
        }
        else
        {
            var dashboard = CreateViewModel(client, saveStore: store);
            await dashboard.RefreshAsync();
            dashboard.FileBrowser.ReplaceSelectionRule(Selection(fixture.Root, "unsubmitted-folder"));
            await dashboard.RunBackupNowCommand.ExecuteAsync(null);
            Assert.False(dashboard.HasUnconfirmedProtectionSave);
            Assert.NotEmpty(dashboard.FileBrowser.PendingChanges);
            Assert.Equal(ProtectionSaveState.Failed, dashboard.ProtectionSaveState);
            Assert.True(dashboard.CanDiscardConfigurationChanges);
        }
        Assert.Equal(0, boundary.Calls);
        Assert.Null(store.Read());
        Assert.DoesNotContain(FluxVaultIpcCommand.RunBackupNow, client.Commands);
        Assert.Equal(JsonSerializer.Serialize(original), JsonSerializer.Serialize(await fixture.Reopen().LoadAsync()));
    }

    private sealed class BoundaryCaller : FluxVaultCallerContext
    {
        public override string UserSid => "S-1-5-21-1-2-3-1001";
        public override IReadOnlySet<string> EnabledGroupSids => new HashSet<string>();
        public override bool IsElevated => false;
        public override bool ImpersonationPermitted => true;
        public override Task<T> RunAsCallerAsync<T>(Func<Task<T>> action) => throw new InvalidOperationException("A refused command must not access caller files.");
        public override void Dispose() { }
    }

    private sealed class RefusedCommandBoundary : IVaultCatalogue, IAuthorisedVaultCommandExecutor
    {
        internal int Calls;
        public bool CanExecute(FluxVaultIpcRequest request) => false;
        private Task<T> Unexpected<T>() { Calls++; throw new InvalidOperationException("A refused command must not reach admission or execution."); }
        public Task<VaultAdmission> AdmitAsync(FluxVaultCallerContext caller, FluxVaultIpcRequest request, CancellationToken cancellationToken = default) => Unexpected<VaultAdmission>();
        public Task<VaultAdmission> SaveConfigurationAsync(FluxVaultCallerContext caller, FluxVaultIpcRequest request, CancellationToken cancellationToken = default) => Unexpected<VaultAdmission>();
        public Task<VaultAdmission> SetAccessAsync(FluxVaultCallerContext caller, FluxVaultIpcRequest request, CancellationToken cancellationToken = default) => Unexpected<VaultAdmission>();
        public Task<VaultOperationReceipt?> GetReceiptAsync(FluxVaultCallerContext caller, VaultId id, Guid operation, CancellationToken cancellationToken = default) => Unexpected<VaultOperationReceipt?>();
        public Task CompleteAsync(VaultOperationReceipt receipt, FluxVaultIpcResponse response, CancellationToken cancellationToken = default) => Unexpected<object>();
        public Task<FluxVaultIpcResponse> ExecuteAsync(FluxVaultCallerContext caller, VaultAdmission admission, FluxVaultIpcRequest request, CancellationToken cancellationToken = default) => Unexpected<FluxVaultIpcResponse>();
    }
    [Fact]
    public Task Controlled_discard_completes_in_the_performance_workspace_without_an_optional_telemetry_request() => RunOnStaAsync(async () =>
    {
        using var fixture = new StoreFixture(false);
        var selection = Selection(fixture.Root, "existing-project");
        await fixture.Store.SaveAsync(NonDefaultConfiguration(fixture.Root) with
        { SelectionRules = [selection], WatchedFolders = ProtectionSelectionCompiler.Compile([selection]) });
        var path = Path.Combine(fixture.Root, "pending-save.json");
        var client = new StoreClient(fixture.Store) { PurgeFails = true };
        var dashboard = CreateViewModel(client, new AcceptRemoval(), saveStore: new FileConfigurationSaveOperationStore(path));
        await dashboard.RefreshAsync();
        dashboard.FileBrowser.RemovePathSelection(selection.Path, true);
        await dashboard.SaveConfigurationCommand.ExecuteAsync(null);
        dashboard.FileBrowser.ReplaceSelectionRule(Selection(fixture.Root, "newer-edit"));
        dashboard.SelectedWorkspaceIndex = 5;
        await client.PerformanceRequested.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        var telemetryCount = client.Commands.Count(command => command == FluxVaultIpcCommand.GetPerformance);
        client.PerformanceThrows = true;
        await dashboard.DiscardConfigurationChangesCommand.ExecuteAsync(null);
        Assert.Null(new FileConfigurationSaveOperationStore(path).Read());
        Assert.False(dashboard.HasUnconfirmedProtectionSave);
        Assert.Empty(dashboard.FileBrowser.GetSelectionRules());
        Assert.Equal(telemetryCount, client.Commands.Count(command => command == FluxVaultIpcCommand.GetPerformance));
        Assert.DoesNotContain(FluxVaultIpcCommand.RunBackupNow, client.Commands);
    });
    [Fact]
    public async Task An_unreadable_record_revokes_previously_confirmed_discard_review()
    {
        using var fixture = new StoreFixture(false);
        var selection = Selection(fixture.Root, "existing-project");
        await fixture.Store.SaveAsync(NonDefaultConfiguration(fixture.Root) with
        { SelectionRules = [selection], WatchedFolders = ProtectionSelectionCompiler.Compile([selection]) });
        var path = Path.Combine(fixture.Root, "pending-save.json");
        var client = new StoreClient(fixture.Store) { PurgeFails = true };
        var dashboard = CreateViewModel(client, new AcceptRemoval(), saveStore: new FileConfigurationSaveOperationStore(path));
        await dashboard.RefreshAsync();
        dashboard.FileBrowser.RemovePathSelection(selection.Path, true);
        await dashboard.SaveConfigurationCommand.ExecuteAsync(null);
        Assert.True(dashboard.CanDiscardConfigurationChanges);
        var newer = Selection(fixture.Root, "newer-edit");
        dashboard.FileBrowser.ReplaceSelectionRule(newer);
        File.WriteAllText(path, "{");
        await dashboard.RefreshAsync();
        Assert.False(dashboard.CanDiscardConfigurationChanges);
        await dashboard.DiscardConfigurationChangesCommand.ExecuteAsync(null);
        Assert.Equal("{", File.ReadAllText(path));
        Assert.Contains(dashboard.FileBrowser.GetSelectionRules(), rule => rule.Path == newer.Path);
        Assert.DoesNotContain(FluxVaultIpcCommand.RunBackupNow, client.Commands);
    }
    [Fact]
    public async Task Save_reserved_by_another_session_during_discard_keeps_the_original_draft_and_operation()
    {
        using var fixture = new StoreFixture(false);
        var configuration = NonDefaultConfiguration(fixture.Root);
        await fixture.Store.SaveAsync(configuration);
        var path = Path.Combine(fixture.Root, "pending-save.json");
        var client = new StoreClient(fixture.Store);
        var dashboard = CreateViewModel(client, saveStore: new FileConfigurationSaveOperationStore(path));
        await dashboard.RefreshAsync();
        var draft = Selection(fixture.Root, "local-edit");
        dashboard.FileBrowser.ReplaceSelectionRule(draft);
        client.HoldStatus = true;
        var discard = dashboard.DiscardConfigurationChangesCommand.ExecuteAsync(null);
        var pending = new PendingConfigurationSave(client.Identity.Value, Guid.NewGuid(), 1, configuration, false, [], []);
        try
        {
            await client.StatusEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            new FileConfigurationSaveOperationStore(path).Reserve(pending);
        }
        finally { client.HoldStatus = false; client.ReleaseStatus.TrySetResult(); await discard.WaitAsync(TimeSpan.FromSeconds(10)); }
        Assert.Equal(pending.OperationId, new FileConfigurationSaveOperationStore(path).Read()!.OperationId);
        Assert.Contains(dashboard.FileBrowser.GetSelectionRules(), rule => rule.Path == draft.Path);
        await dashboard.RunBackupNowCommand.ExecuteAsync(null);
        Assert.All(client.Commands, command => Assert.Equal(FluxVaultIpcCommand.GetStatus, command));
        Assert.True(dashboard.HasUnconfirmedProtectionSave);
    }

    [Fact]
    public async Task Options_entry_rechecks_a_save_reserved_since_the_previous_refresh()
    {
        using var fixture = new StoreFixture(false);
        var configuration = NonDefaultConfiguration(fixture.Root);
        await fixture.Store.SaveAsync(configuration);
        var path = Path.Combine(fixture.Root, "pending-save.json");
        var client = new StoreClient(fixture.Store);
        var dashboard = CreateViewModel(client, saveStore: new FileConfigurationSaveOperationStore(path));
        await dashboard.RefreshAsync();
        Assert.True(dashboard.CanOpenOptions);
        new FileConfigurationSaveOperationStore(path).Reserve(new(client.Identity.Value, Guid.NewGuid(), 1, configuration, false, [], []));
        Assert.False(dashboard.TryBeginOptionsEditing());
        Assert.True(dashboard.HasUnconfirmedProtectionSave);
    }
    [Fact]
    public Task Wpf_reopened_save_action_checks_the_original_operation_without_dispatching_backup() => RunOnStaAsync(async () =>
    {
        using var fixture = new StoreFixture(false);
        await fixture.Store.SaveAsync(NonDefaultConfiguration(fixture.Root));
        var path = Path.Combine(fixture.Root, "pending-save.json");
        var client = new StoreClient(fixture.Store) { Failure = SaveFailure.AcknowledgementLost };
        var first = CreateViewModel(client, saveStore: new FileConfigurationSaveOperationStore(path));
        await first.RefreshAsync();
        first.FileBrowser.ReplaceSelectionRule(Selection(fixture.Root, "pending-project"));
        await first.RunBackupNowCommand.ExecuteAsync(null);
        var request = Assert.Single(client.Requests, request => request.Command == FluxVaultIpcCommand.SaveConfiguration);
        var restarted = CreateViewModel(client, saveStore: new FileConfigurationSaveOperationStore(path));
        await restarted.RefreshAsync();
        var window = new MainWindow(new FileDataGridLayoutStore(Path.Combine(fixture.Root, "layout.json"))) { DataContext = restarted };
        try
        {
            window.Show();
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            var button = Assert.IsType<Button>(window.FindName("CheckProtectionSaveOutcomeButton"));
            Assert.Equal(Visibility.Visible, button.Visibility);
            Assert.True(button.IsEnabled);
            await Assert.IsAssignableFrom<CommunityToolkit.Mvvm.Input.IAsyncRelayCommand>(button.Command).ExecuteAsync(null);
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Assert.Equal(Visibility.Collapsed, button.Visibility);
            Assert.Equal(request.OperationId, Assert.Single(client.Requests, request => request.Command == FluxVaultIpcCommand.GetOperationStatus).OperationId);
            Assert.DoesNotContain(FluxVaultIpcCommand.RunBackupNow, client.Commands);
        }
        finally { window.Close(); }
    });

    [Fact]
    public async Task A_returned_save_receipt_with_unavailable_current_settings_keeps_the_durable_review_gate()
    {
        using var fixture = new StoreFixture(false);
        await fixture.Store.SaveAsync(NonDefaultConfiguration(fixture.Root));
        var path = Path.Combine(fixture.Root, "pending-save.json");
        var client = new StoreClient(fixture.Store) { Failure = SaveFailure.AcknowledgementLost };
        var dashboard = CreateViewModel(client, saveStore: new FileConfigurationSaveOperationStore(path));
        await dashboard.RefreshAsync();
        await dashboard.SaveConfigurationCommand.ExecuteAsync(null);
        client.StatusFails = true;
        await dashboard.CheckProtectionSaveOutcomeCommand.ExecuteAsync(null);
        Assert.NotNull(new FileConfigurationSaveOperationStore(path).Read());
        await dashboard.DiscardConfigurationChangesCommand.ExecuteAsync(null);
        Assert.NotNull(new FileConfigurationSaveOperationStore(path).Read());
        Assert.True(dashboard.HasUnconfirmedProtectionSave);
        Assert.DoesNotContain(FluxVaultIpcCommand.RunBackupNow, client.Commands);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Corrupt_or_busy_protection_save_records_keep_local_edits_and_refuse_dispatch(bool busy)
    {
        using var fixture = new StoreFixture(false);
        await fixture.Store.SaveAsync(NonDefaultConfiguration(fixture.Root));
        var path = Path.Combine(fixture.Root, "pending-save.json");
        var client = new StoreClient(fixture.Store);
        var dashboard = CreateViewModel(client, saveStore: new FileConfigurationSaveOperationStore(path));
        await dashboard.RefreshAsync();
        dashboard.FileBrowser.ReplaceSelectionRule(Selection(fixture.Root, "local-edit"));
        FileStream? gate = null;
        if (busy) gate = new FileStream(path + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        else File.WriteAllText(path, "{");
        try
        {
            await dashboard.RunBackupNowCommand.ExecuteAsync(null);
            await dashboard.DiscardConfigurationChangesCommand.ExecuteAsync(null);
            Assert.NotEmpty(dashboard.FileBrowser.PendingChanges);
            Assert.All(client.Commands, command => Assert.Equal(FluxVaultIpcCommand.GetStatus, command));
            Assert.True(dashboard.HasUnconfirmedProtectionSave);
            Assert.Contains("could not be read", dashboard.ProtectionSaveMessage);
            if (!busy) Assert.Equal("{", File.ReadAllText(path));
        }
        finally { gate?.Dispose(); }
    }

    [Theory]
    [InlineData(FluxVaultIpcErrorCode.OutcomeUnknown, 2)]
    [InlineData(FluxVaultIpcErrorCode.Unavailable, 2)]
    [InlineData(FluxVaultIpcErrorCode.Denied, 2)]
    [InlineData(null, 900)]
    public async Task Unavailable_or_mismatched_protection_save_receipts_keep_the_snapshot_and_block_backup(FluxVaultIpcErrorCode? error, long revision)
    {
        using var fixture = new StoreFixture(false);
        await fixture.Store.SaveAsync(NonDefaultConfiguration(fixture.Root));
        var path = Path.Combine(fixture.Root, "pending-save.json");
        var client = new StoreClient(fixture.Store) { Failure = SaveFailure.AcknowledgementLost, ReceiptErrorCode = error, ReceiptRevisionOverride = revision };
        var first = CreateViewModel(client, saveStore: new FileConfigurationSaveOperationStore(path));
        await first.RefreshAsync();
        first.FileBrowser.ReplaceSelectionRule(Selection(fixture.Root, "pending-project"));
        await first.SaveConfigurationCommand.ExecuteAsync(null);
        var snapshot = File.ReadAllText(path);
        var restarted = CreateViewModel(client, saveStore: new FileConfigurationSaveOperationStore(path));
        await restarted.RefreshAsync();
        await restarted.CheckProtectionSaveOutcomeCommand.ExecuteAsync(null);
        await restarted.RunBackupNowCommand.ExecuteAsync(null);
        Assert.True(restarted.HasUnconfirmedProtectionSave);
        Assert.False(restarted.CanDiscardConfigurationChanges);
        Assert.Equal(snapshot, File.ReadAllText(path));
        Assert.Single(client.Requests, request => request.Command == FluxVaultIpcCommand.SaveConfiguration);
        Assert.DoesNotContain(FluxVaultIpcCommand.RunBackupNow, client.Commands);
    }

    [Fact]
    public async Task Historical_save_receipt_never_replaces_a_newer_baseline_and_requires_explicit_review()
    {
        using var fixture = new StoreFixture(false);
        await fixture.Store.SaveAsync(NonDefaultConfiguration(fixture.Root));
        var path = Path.Combine(fixture.Root, "pending-save.json");
        var client = new StoreClient(fixture.Store) { Failure = SaveFailure.AcknowledgementLost, ReceiptRevisionOverride = 2, RequireBoundRequests = true };
        var first = CreateViewModel(client, saveStore: new FileConfigurationSaveOperationStore(path));
        await first.RefreshAsync();
        first.FileBrowser.ReplaceSelectionRule(Selection(fixture.Root, "pending-project"));
        await first.SaveConfigurationCommand.ExecuteAsync(null);
        client.Failure = SaveFailure.None;
        var newer = (await fixture.Reopen().LoadAsync()) with { VersionPreview = new(33) };
        await client.SendAsync(FluxVaultIpcRequest.SaveConfiguration(newer) with { VaultId = client.Identity, ExpectedVaultRevision = 2, OperationId = Guid.NewGuid() });
        var restarted = CreateViewModel(client, saveStore: new FileConfigurationSaveOperationStore(path));
        await restarted.RefreshAsync();
        await restarted.CheckProtectionSaveOutcomeCommand.ExecuteAsync(null);
        await restarted.RunBackupNowCommand.ExecuteAsync(null);
        Assert.NotNull(new FileConfigurationSaveOperationStore(path).Read());
        Assert.Contains("historical", restarted.ProtectionSaveMessage);
        Assert.Equal(33, (await fixture.Reopen().LoadAsync()).VersionPreview.RetentionDays);
        Assert.DoesNotContain(FluxVaultIpcCommand.RunBackupNow, client.Commands);
        await restarted.DiscardConfigurationChangesCommand.ExecuteAsync(null);
        Assert.Null(new FileConfigurationSaveOperationStore(path).Read());
        Assert.False(restarted.HasUnconfirmedProtectionSave);
        Assert.Equal(33, (await fixture.Reopen().LoadAsync()).VersionPreview.RetentionDays);
    }

    [Fact]
    public async Task Confirmed_failed_purge_keeps_its_durable_gate_across_restart_until_explicit_review()
    {
        using var fixture = new StoreFixture(false);
        var selection = Selection(fixture.Root, "existing-project");
        await fixture.Store.SaveAsync(NonDefaultConfiguration(fixture.Root) with
        { SelectionRules = [selection], WatchedFolders = ProtectionSelectionCompiler.Compile([selection]) });
        var path = Path.Combine(fixture.Root, "pending-save.json");
        var client = new StoreClient(fixture.Store)
        {
            PurgeFails = true,
            ReceiptResponseOverride = FluxVaultIpcResponse.WithPurge(new RepositoryPurgeResult(0, 0, 0, [], Success: false, ErrorMessage: "Fixture purge denied"))
        };
        var first = CreateViewModel(client, new AcceptRemoval(), saveStore: new FileConfigurationSaveOperationStore(path));
        await first.RefreshAsync();
        first.FileBrowser.RemovePathSelection(selection.Path, true);
        await first.RunBackupNowCommand.ExecuteAsync(null);
        Assert.NotNull(new FileConfigurationSaveOperationStore(path).Read());
        var restarted = CreateViewModel(client, saveStore: new FileConfigurationSaveOperationStore(path));
        await restarted.RefreshAsync();
        Assert.False(restarted.CanDiscardConfigurationChanges);
        await restarted.CheckProtectionSaveOutcomeCommand.ExecuteAsync(null);
        Assert.Contains("purge failed", restarted.ProtectionSaveMessage);
        Assert.NotNull(new FileConfigurationSaveOperationStore(path).Read());
        Assert.True(restarted.CanDiscardConfigurationChanges);
        await restarted.RunBackupNowCommand.ExecuteAsync(null);
        Assert.DoesNotContain(FluxVaultIpcCommand.RunBackupNow, client.Commands);
        await restarted.DiscardConfigurationChangesCommand.ExecuteAsync(null);
        Assert.Null(new FileConfigurationSaveOperationStore(path).Read());
        Assert.False(restarted.HasUnconfirmedProtectionSave);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Protection_save_reopens_the_full_dispatched_snapshot_and_checks_its_original_receipt(bool profileStore)
    {
        using var fixture = new StoreFixture(profileStore);
        await fixture.Store.SaveAsync(NonDefaultConfiguration(fixture.Root));
        var path = Path.Combine(fixture.Root, "pending-save.json");
        var client = new StoreClient(fixture.Store) { Failure = SaveFailure.AcknowledgementLost, RequireBoundRequests = true };
        client.SaveDispatchCheck = request =>
        {
            var pending = Assert.IsType<PendingConfigurationSave>(new FileConfigurationSaveOperationStore(path).Read());
            Assert.Equal(request.OperationId, pending.OperationId);
            Assert.Equal(request.ExpectedVaultRevision, pending.Revision);
            Assert.Equal(JsonSerializer.Serialize(request.Configuration), JsonSerializer.Serialize(pending.Configuration));
        };
        var first = CreateViewModel(client, saveStore: new FileConfigurationSaveOperationStore(path));
        await first.RefreshAsync();
        first.FileBrowser.ReplaceSelectionRule(Selection(fixture.Root, "pending-project"));
        await first.RunBackupNowCommand.ExecuteAsync(null);
        var request = Assert.Single(client.Requests, request => request.Command == FluxVaultIpcCommand.SaveConfiguration);
        var restarted = CreateViewModel(client, saveStore: new FileConfigurationSaveOperationStore(path));
        await restarted.RefreshAsync();
        Assert.True(restarted.HasUnconfirmedProtectionSave);
        Assert.Contains(restarted.FileBrowser.GetSelectionRules(), rule => rule.Path.EndsWith("pending-project"));
        await restarted.RunBackupNowCommand.ExecuteAsync(null);
        await restarted.DiscardConfigurationChangesCommand.ExecuteAsync(null);
        Assert.Single(client.Requests, request => request.Command == FluxVaultIpcCommand.SaveConfiguration);
        Assert.DoesNotContain(FluxVaultIpcCommand.RunBackupNow, client.Commands);
        client.ReceiptRevisionOverride = request.ExpectedVaultRevision + 1;
        await restarted.CheckProtectionSaveOutcomeCommand.ExecuteAsync(null);
        Assert.Equal(request.OperationId, Assert.Single(client.Requests, request => request.Command == FluxVaultIpcCommand.GetOperationStatus).OperationId);
        Assert.Null(new FileConfigurationSaveOperationStore(path).Read());
        Assert.False(restarted.HasUnconfirmedProtectionSave);
        Assert.DoesNotContain(FluxVaultIpcCommand.RunBackupNow, client.Commands);
    }

    [Fact]
    public async Task A_pending_save_for_another_repository_never_restores_its_draft_or_sends_a_command()
    {
        using var fixture = new StoreFixture(false);
        var configuration = NonDefaultConfiguration(fixture.Root);
        await fixture.Store.SaveAsync(configuration);
        var path = Path.Combine(fixture.Root, "pending-save.json");
        var foreign = configuration with { RepositoryPath = Path.Combine(fixture.Root, "foreign-private-repository") };
        new FileConfigurationSaveOperationStore(path).Reserve(new(Guid.NewGuid(), Guid.NewGuid(), 1, foreign, false, [], []));
        var client = new StoreClient(fixture.Store);
        var dashboard = CreateViewModel(client, saveStore: new FileConfigurationSaveOperationStore(path));
        await dashboard.RefreshAsync();
        await dashboard.RunBackupNowCommand.ExecuteAsync(null);
        await dashboard.CheckProtectionSaveOutcomeCommand.ExecuteAsync(null);
        Assert.Equal(configuration.RepositoryPath, dashboard.RepositoryPath);
        Assert.All(client.Commands, command => Assert.Equal(FluxVaultIpcCommand.GetStatus, command));
        Assert.NotNull(new FileConfigurationSaveOperationStore(path).Read());
        Assert.Contains("binding", dashboard.ProtectionSaveMessage);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Preview_retention_options_load_and_save_through_the_real_store(bool profileStore)
    {
        using var fixture = new StoreFixture(profileStore);
        var original = NonDefaultConfiguration(fixture.Root) with { VersionPreview = new(11) };
        await fixture.Store.SaveAsync(original);
        var options = new OptionsViewModel(new StoreClient(fixture.Store));
        await options.InitialiseAsync();
        Assert.Equal(11, options.PreviewRetentionDays);
        options.PreviewRetentionDays = 5;
        await options.SaveAsync();
        Assert.Equal(5, (await fixture.Reopen().LoadAsync()).VersionPreview.RetentionDays);
        Assert.Equal(original.MetadataStore, (await fixture.Reopen().LoadAsync()).MetadataStore);
    }

    [Fact]
    public Task Preview_retention_control_renders_and_edits_the_loaded_policy() => RunOnStaAsync(async () =>
    {
        using var fixture = new StoreFixture(false);
        await fixture.Store.SaveAsync(NonDefaultConfiguration(fixture.Root) with { VersionPreview = new(11) });
        var viewModel = new OptionsViewModel(new StoreClient(fixture.Store));
        var window = new OptionsWindow(viewModel);
        try
        {
            window.Show();
            await viewModel.InitialiseAsync();
            var shell = Assert.IsType<Grid>(window.FindName("OptionsShell"));
            shell.Children.OfType<TabControl>().Single().SelectedIndex = 2;
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            window.UpdateLayout();
            var control = Descendants(shell).OfType<TextBox>().Single(text =>
                System.Windows.Automation.AutomationProperties.GetName(text) == "Keep preview copies in days");
            control.BringIntoView();
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Assert.Equal("11", control.Text);
            Assert.True(control.ActualWidth > 40 && control.ActualHeight > 20);
            Assert.True(control.Focusable);
            control.Text = "5";
            Assert.Equal(5, viewModel.PreviewRetentionDays);
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            window.UpdateLayout();
            var renderPath = Environment.GetEnvironmentVariable("FLUXVAULT_PREVIEW_OPTIONS_RENDER");
            if (!string.IsNullOrEmpty(renderPath))
            {
                var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(
                    (int)Math.Ceiling(shell.ActualWidth), (int)Math.Ceiling(shell.ActualHeight), 96, 96,
                    System.Windows.Media.PixelFormats.Pbgra32);
                var background = new System.Windows.Media.DrawingVisual();
                using (var drawing = background.RenderOpen())
                    drawing.DrawRectangle(window.Background, null, new Rect(0, 0, shell.ActualWidth, shell.ActualHeight));
                bitmap.Render(background);
                bitmap.Render(shell);
                var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
                encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
                using var output = File.Create(renderPath);
                encoder.Save(output);
            }
        }
        finally { window.Close(); }
    });

    private static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
    {
        for (var index = 0; index < System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(parent, index);
            yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Preview_uses_a_fresh_user_destination_and_retains_publication_warnings(bool wrongOutput)
    {
        using var fixture = new StoreFixture(false);
        await fixture.Store.SaveAsync(NonDefaultConfiguration(fixture.Root));
        var client = new StoreClient(fixture.Store) { WrongPreviewOutput = wrongOutput };
        var launcher = new PreviewLauncher();
        var dashboard = new MainWindowViewModel(client, TimeSpan.FromHours(1),
            new FileBrowserViewModel(new WindowsFileBrowserFileSystem()), new FixtureServiceController(),
            new UnusedDestinationPicker(), new UnusedOverwriteConfirmation(), versionPreviewLauncher: launcher,
            previewCache: new FluxVault.Windows.Security.WindowsUserPreviewCache(Path.Combine(fixture.Root, "previews")));
        await dashboard.RefreshAsync();
        await dashboard.OpenVersionPreviewAsync(new VersionRow("fixture-version", @"C:\work\document.docx", "today", CaptureConsistency.BestEffort, 1));
        var dispatched = Assert.Single(client.Requests, request => request.Command == FluxVaultIpcCommand.RestoreVersionPreview);
        Assert.NotNull(dispatched.OutputPath);
        Assert.Equal(".docx", Path.GetExtension(dispatched.OutputPath));
        if (wrongOutput) Assert.Empty(launcher.Opened);
        else
        {
            Assert.Equal([dispatched.OutputPath], launcher.Opened);
            Assert.True(File.GetAttributes(dispatched.OutputPath).HasFlag(FileAttributes.ReadOnly));
            Assert.Contains("permissions warning", dashboard.ServiceStatus);
        }
        File.SetAttributes(dispatched.OutputPath, FileAttributes.Normal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Published_preview_survives_a_failed_viewer_launch_and_keeps_its_warnings(bool cancelled)
    {
        using var fixture = new StoreFixture(false);
        await fixture.Store.SaveAsync(NonDefaultConfiguration(fixture.Root));
        var client = new StoreClient(fixture.Store);
        var launcher = new PreviewLauncher
        {
            Failure = cancelled ? new OperationCanceledException("Viewer cancelled")
                : new System.ComponentModel.Win32Exception("No viewer is registered")
        };
        var dashboard = new MainWindowViewModel(client, TimeSpan.FromHours(1),
            new FileBrowserViewModel(new WindowsFileBrowserFileSystem()), new FixtureServiceController(),
            new UnusedDestinationPicker(), new UnusedOverwriteConfirmation(), versionPreviewLauncher: launcher,
            previewCache: new FluxVault.Windows.Security.WindowsUserPreviewCache(Path.Combine(fixture.Root, "previews")));
        await dashboard.RefreshAsync();
        await dashboard.OpenVersionPreviewAsync(new VersionRow("fixture-version", @"C:\work\document.docx", "today", CaptureConsistency.BestEffort, 1));
        var request = Assert.Single(client.Requests, request => request.Command == FluxVaultIpcCommand.RestoreVersionPreview);
        try
        {
            Assert.Equal("verified preview bytes", File.ReadAllText(request.OutputPath!));
            Assert.True(File.GetAttributes(request.OutputPath!).HasFlag(FileAttributes.ReadOnly));
            Assert.Empty(launcher.Opened);
            Assert.Contains("copy was created, but opening failed", dashboard.ServiceStatus);
            Assert.Contains("Fixture permissions warning", dashboard.ServiceStatus);
            Assert.Contains(launcher.Failure.Message, dashboard.ServiceStatus);
            Assert.False(dashboard.IsPreviewBusy);
            Assert.Empty(dashboard.PreviewStatus);
        }
        finally { File.SetAttributes(request.OutputPath!, FileAttributes.Normal); }
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Restarted_dashboard_retains_the_durable_backup_identity_and_checks_it_without_a_second_backup(bool profileStore)
    {
        using var fixture = new StoreFixture(profileStore);
        await fixture.Store.SaveAsync(NonDefaultConfiguration(fixture.Root));
        var path = Path.Combine(fixture.Root, "pending-backup.json");
        var client = new StoreClient(fixture.Store) { BackupFailure = SaveFailure.Timeout, RequireBoundRequests = true };
        client.BackupDispatchCheck = request =>
        {
            var persisted = Assert.IsType<PendingBackupOperation>(new FileBackupOperationStore(path).Read());
            Assert.Equal(request.VaultId!.Value.Value, persisted.RepositoryId);
            Assert.Equal(request.OperationId, persisted.OperationId);
            Assert.Equal(request.ExpectedVaultRevision, persisted.Revision);
        };
        var first = CreateViewModel(client, backupStore: new FileBackupOperationStore(path));
        await first.RefreshAsync();
        first.FileBrowser.ReplaceSelectionRule(Selection(fixture.Root, "pending-project"));
        await first.RunBackupNowCommand.ExecuteAsync(null);
        var operation = first.UnconfirmedBackupOperationId;
        Assert.NotNull(operation);
        var restarted = CreateViewModel(client, backupStore: new FileBackupOperationStore(path));
        await restarted.RefreshAsync();
        Assert.Equal(operation, restarted.UnconfirmedBackupOperationId);
        await restarted.RunBackupNowCommand.ExecuteAsync(null);
        Assert.Single(client.Requests, request => request.Command == FluxVaultIpcCommand.RunBackupNow);
        await restarted.CheckBackupOutcomeCommand.ExecuteAsync(null);
        Assert.Equal(operation, Assert.Single(client.Requests, request => request.Command == FluxVaultIpcCommand.GetOperationStatus).OperationId);
        Assert.Null(restarted.UnconfirmedBackupOperationId);
        Assert.Null(new FileBackupOperationStore(path).Read());
    }

    [Fact]
    public async Task Pending_backup_from_a_different_repository_blocks_dispatch_and_is_preserved()
    {
        using var fixture = new StoreFixture(false);
        await fixture.Store.SaveAsync(NonDefaultConfiguration(fixture.Root));
        var path = Path.Combine(fixture.Root, "pending-backup.json");
        var store = new FileBackupOperationStore(path);
        var pending = new PendingBackupOperation(Guid.NewGuid(), Guid.NewGuid(), 1);
        store.Reserve(pending);
        var client = new StoreClient(fixture.Store);
        var dashboard = CreateViewModel(client, backupStore: store);
        await dashboard.RefreshAsync();
        await dashboard.RunBackupNowCommand.ExecuteAsync(null);
        await dashboard.CheckBackupOutcomeCommand.ExecuteAsync(null);
        Assert.DoesNotContain(client.Requests, request => request.Command is FluxVaultIpcCommand.RunBackupNow or FluxVaultIpcCommand.GetOperationStatus);
        Assert.Equal(pending, store.Read());
        Assert.Contains("binding", dashboard.BackupOutcomeMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Unreadable_or_busy_client_record_prevents_backup_dispatch_and_keeps_the_draft(bool corrupt)
    {
        using var fixture = new StoreFixture(false);
        await fixture.Store.SaveAsync(NonDefaultConfiguration(fixture.Root));
        var path = Path.Combine(fixture.Root, "pending-backup.json");
        if(corrupt) File.WriteAllText(path, "not-json");
        using var held = corrupt ? null : new FileStream(path + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var client = new StoreClient(fixture.Store);
        var dashboard = CreateViewModel(client, backupStore: new FileBackupOperationStore(path));
        await dashboard.RefreshAsync();
        dashboard.FileBrowser.ReplaceSelectionRule(Selection(fixture.Root, "pending-project"));
        Assert.Null(await Record.ExceptionAsync(() => dashboard.RunBackupNowCommand.ExecuteAsync(null)));
        Assert.DoesNotContain(client.Requests, request => request.Command is FluxVaultIpcCommand.SaveConfiguration or FluxVaultIpcCommand.RunBackupNow);
        Assert.NotEmpty(dashboard.FileBrowser.PendingChanges);
        Assert.Contains("record", dashboard.BackupOutcomeMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Receipt_for_a_different_revision_cannot_clear_the_durable_backup_record()
    {
        using var fixture = new StoreFixture(false);
        await fixture.Store.SaveAsync(NonDefaultConfiguration(fixture.Root));
        var store = new FileBackupOperationStore(Path.Combine(fixture.Root, "pending-backup.json"));
        var client = new StoreClient(fixture.Store) { BackupFailure = SaveFailure.Timeout, ReceiptRevisionOverride = 900 };
        var dashboard = CreateViewModel(client, backupStore: store);
        await dashboard.RefreshAsync();
        await dashboard.RunBackupNowCommand.ExecuteAsync(null);
        var pending = Assert.IsType<PendingBackupOperation>(store.Read());
        await dashboard.CheckBackupOutcomeCommand.ExecuteAsync(null);
        Assert.Equal(pending, store.Read());
        Assert.Equal(pending.OperationId, dashboard.UnconfirmedBackupOperationId);
    }

    [Fact]
    public async Task Confirmed_backup_clears_the_record_and_can_be_requested_again()
    {
        using var fixture = new StoreFixture(false);
        await fixture.Store.SaveAsync(NonDefaultConfiguration(fixture.Root));
        var store = new FileBackupOperationStore(Path.Combine(fixture.Root, "pending-backup.json"));
        var client = new StoreClient(fixture.Store);
        var dashboard = CreateViewModel(client, backupStore: store);
        await dashboard.RefreshAsync();
        await dashboard.RunBackupNowCommand.ExecuteAsync(null);
        Assert.Null(store.Read());
        Assert.False(dashboard.HasUnconfirmedBackup);
        await dashboard.RunBackupNowCommand.ExecuteAsync(null);
        Assert.Equal(2, client.Requests.Count(request => request.Command == FluxVaultIpcCommand.RunBackupNow));
        Assert.Null(store.Read());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Completed_failed_backup_clears_its_durable_record_and_explains_the_failure(bool acknowledgementLost)
    {
        using var fixture = new StoreFixture(false);
        await fixture.Store.SaveAsync(NonDefaultConfiguration(fixture.Root));
        var path = Path.Combine(fixture.Root, "pending-backup.json");
        var client = new StoreClient(fixture.Store)
        {
            BackupFailure = acknowledgementLost ? SaveFailure.Timeout : SaveFailure.None,
            BackupCompletedUnsuccessfully = true
        };
        var dashboard = CreateViewModel(client, backupStore: new FileBackupOperationStore(path));
        await dashboard.RefreshAsync();
        await dashboard.RunBackupNowCommand.ExecuteAsync(null);
        if (acknowledgementLost)
        {
            Assert.True(dashboard.HasUnconfirmedBackup);
            dashboard = CreateViewModel(client, backupStore: new FileBackupOperationStore(path));
            await dashboard.RefreshAsync();
            await dashboard.CheckBackupOutcomeCommand.ExecuteAsync(null);
        }
        Assert.False(dashboard.HasUnconfirmedBackup);
        Assert.Null(new FileBackupOperationStore(path).Read());
        Assert.Contains("failed", dashboard.BackupOutcomeMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("capture unavailable", dashboard.BackupOutcomeMessage, StringComparison.OrdinalIgnoreCase);
        await dashboard.RunBackupNowCommand.ExecuteAsync(null);
        Assert.Equal(2, client.Requests.Count(request => request.Command == FluxVaultIpcCommand.RunBackupNow));
    }

    [Fact]
    public async Task A_failed_record_clear_keeps_the_confirmed_operation_pending_until_it_can_be_cleared()
    {
        using var fixture = new StoreFixture(false);
        await fixture.Store.SaveAsync(NonDefaultConfiguration(fixture.Root));
        var path = Path.Combine(fixture.Root, "pending-backup.json");
        var store = new FileBackupOperationStore(path);
        FileStream? held = null;
        var client = new StoreClient(fixture.Store)
        {
            BackupDispatchCheck = _ => held = new FileStream(path + ".lock", FileMode.Open, FileAccess.ReadWrite, FileShare.None)
        };
        var dashboard = CreateViewModel(client, backupStore: store);
        try
        {
            await dashboard.RefreshAsync();
            await dashboard.RunBackupNowCommand.ExecuteAsync(null);
            Assert.True(dashboard.HasUnconfirmedBackup);
            Assert.Contains("could not be cleared", dashboard.BackupOutcomeMessage);
        }
        finally { held?.Dispose(); }
        var pending = Assert.IsType<PendingBackupOperation>(store.Read());
        Assert.Equal(pending.OperationId, dashboard.UnconfirmedBackupOperationId);
        await dashboard.CheckBackupOutcomeCommand.ExecuteAsync(null);
        Assert.Null(store.Read());
        Assert.False(dashboard.HasUnconfirmedBackup);
        Assert.Single(client.Requests, request => request.Command == FluxVaultIpcCommand.RunBackupNow);
    }

    [Theory]
    [InlineData(FluxVaultIpcErrorCode.Denied)]
    [InlineData(FluxVaultIpcErrorCode.Unavailable)]
    [InlineData(FluxVaultIpcErrorCode.OutcomeUnknown)]
    public async Task A_refused_or_missing_receipt_keeps_the_original_durable_operation(FluxVaultIpcErrorCode error)
    {
        using var fixture = new StoreFixture(false);
        await fixture.Store.SaveAsync(NonDefaultConfiguration(fixture.Root));
        var store = new FileBackupOperationStore(Path.Combine(fixture.Root, "pending-backup.json"));
        var client = new StoreClient(fixture.Store) { BackupFailure = SaveFailure.Timeout, ReceiptErrorCode = error };
        var dashboard = CreateViewModel(client, backupStore: store);
        await dashboard.RefreshAsync();
        await dashboard.RunBackupNowCommand.ExecuteAsync(null);
        var pending = Assert.IsType<PendingBackupOperation>(store.Read());
        await dashboard.CheckBackupOutcomeCommand.ExecuteAsync(null);
        await dashboard.RunBackupNowCommand.ExecuteAsync(null);
        Assert.Equal(pending, store.Read());
        Assert.Equal(pending.OperationId, dashboard.UnconfirmedBackupOperationId);
        Assert.Single(client.Requests, request => request.Command == FluxVaultIpcCommand.RunBackupNow);
    }

    [Fact]
    public async Task A_confirmed_original_receipt_can_clear_after_configuration_revision_has_advanced()
    {
        using var fixture = new StoreFixture(false);
        await fixture.Store.SaveAsync(NonDefaultConfiguration(fixture.Root));
        var store = new FileBackupOperationStore(Path.Combine(fixture.Root, "pending-backup.json"));
        var client = new StoreClient(fixture.Store) { BackupFailure = SaveFailure.Timeout };
        var dashboard = CreateViewModel(client, backupStore: store);
        await dashboard.RefreshAsync();
        await dashboard.RunBackupNowCommand.ExecuteAsync(null);
        var pending = Assert.IsType<PendingBackupOperation>(store.Read());
        client.ReceiptRevisionOverride = pending.Revision;
        dashboard.FileBrowser.ReplaceSelectionRule(Selection(fixture.Root, "later-edit"));
        await dashboard.SaveConfigurationCommand.ExecuteAsync(null);
        Assert.True(Assert.Single(client.Requests.Where(request => request.Command == FluxVaultIpcCommand.SaveConfiguration).TakeLast(1)).ExpectedVaultRevision >= pending.Revision);
        await dashboard.CheckBackupOutcomeCommand.ExecuteAsync(null);
        Assert.Null(store.Read());
        Assert.False(dashboard.HasUnconfirmedBackup);
        Assert.Single(client.Requests, request => request.Command == FluxVaultIpcCommand.RunBackupNow);
    }

    [Fact]
    public async Task Receipt_clear_cannot_remove_a_newer_operation_from_another_session()
    {
        using var fixture = new StoreFixture(false);
        await fixture.Store.SaveAsync(NonDefaultConfiguration(fixture.Root));
        var store = new FileBackupOperationStore(Path.Combine(fixture.Root, "pending-backup.json"));
        var client = new StoreClient(fixture.Store) { BackupFailure = SaveFailure.Timeout };
        var dashboard = CreateViewModel(client, backupStore: store);
        await dashboard.RefreshAsync();
        await dashboard.RunBackupNowCommand.ExecuteAsync(null);
        var first = Assert.IsType<PendingBackupOperation>(store.Read());
        var newer = first with { OperationId = Guid.NewGuid() };
        client.ReceiptDispatchCheck = _ => { store.Clear(first); store.Reserve(newer); };
        await dashboard.CheckBackupOutcomeCommand.ExecuteAsync(null);
        Assert.Equal(newer, store.Read());
        await dashboard.RefreshAsync();
        Assert.Equal(newer.OperationId, dashboard.UnconfirmedBackupOperationId);
        Assert.Single(client.Requests, request => request.Command == FluxVaultIpcCommand.RunBackupNow);
    }

    [Theory]
    [InlineData(false, SaveFailure.Io)]
    [InlineData(false, SaveFailure.Timeout)]
    [InlineData(false, SaveFailure.Cancelled)]
    [InlineData(false, SaveFailure.Denied)]
    [InlineData(true, SaveFailure.Io)]
    [InlineData(true, SaveFailure.Timeout)]
    [InlineData(true, SaveFailure.Cancelled)]
    [InlineData(true, SaveFailure.Denied)]
    public async Task Receipt_check_failure_is_handled_and_retains_the_original_uncertain_operation(bool optionsFlow, SaveFailure failure)
    {
        using var fixture = new StoreFixture(false);
        await fixture.Store.SaveAsync(NonDefaultConfiguration(fixture.Root));
        var client = new StoreClient(fixture.Store) { Failure = optionsFlow ? SaveFailure.AcknowledgementLost : SaveFailure.None,
            BackupFailure = optionsFlow ? SaveFailure.None : SaveFailure.Timeout, ReceiptFailure = failure };
        if (optionsFlow)
        {
            var options = new OptionsViewModel(client);
            await options.InitialiseAsync(); options.MinimumVersionsPerFile = 31;
            await options.SaveAsync();
            var dispatched = Assert.Single(client.Requests, r => r.Command == FluxVaultIpcCommand.SaveConfiguration);
            Assert.Null(await Record.ExceptionAsync(() => options.CheckSaveOutcomeAsync()));
            Assert.True(options.HasUnconfirmedSave);
            Assert.Equal(31, options.MinimumVersionsPerFile);
            Assert.Equal(dispatched.OperationId, Assert.Single(client.Requests, r => r.Command == FluxVaultIpcCommand.GetOperationStatus).OperationId);
        }
        else
        {
            var dashboard = CreateViewModel(client); await dashboard.RefreshAsync();
            dashboard.FileBrowser.ReplaceSelectionRule(Selection(fixture.Root, "pending"));
            await dashboard.RunBackupNowCommand.ExecuteAsync(null);
            var operation = dashboard.UnconfirmedBackupOperationId;
            Assert.NotNull(operation);
            Assert.Null(await Record.ExceptionAsync(() => dashboard.CheckBackupOutcomeCommand.ExecuteAsync(null)));
            Assert.Equal(operation, dashboard.UnconfirmedBackupOperationId);
        }
    }

    [Theory]
    [InlineData(EnvelopeFault.WrongIdentity)]
    [InlineData(EnvelopeFault.MissingOperation)]
    [InlineData(EnvelopeFault.WrongRevision)]
    public async Task Options_mismatched_save_acknowledgement_keeps_edits_and_blocks_blind_retry(EnvelopeFault fault)
    {
        using var fixture = new StoreFixture(false);
        await fixture.Store.SaveAsync(NonDefaultConfiguration(fixture.Root));
        var client = new StoreClient(fixture.Store) { SaveEnvelopeFault = fault, RequireBoundRequests = true };
        var options = new OptionsViewModel(client);
        await options.InitialiseAsync();
        options.MinimumVersionsPerFile = 31;
        await options.SaveAsync();
        Assert.Equal(31, options.MinimumVersionsPerFile);
        Assert.Contains("kept", options.StatusText);
        await options.SaveAsync();
        Assert.Single(client.Requests, r => r.Command == FluxVaultIpcCommand.SaveConfiguration);
        Assert.True(options.HasUnconfirmedSave);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Options_restart_keeps_the_full_dispatched_snapshot_and_original_receipt(bool profileStore)
    {
        using var fixture = new StoreFixture(profileStore);
        await fixture.Store.SaveAsync(NonDefaultConfiguration(fixture.Root));
        var path = Path.Combine(fixture.Root, "pending-protection-save.json");
        var client = new StoreClient(fixture.Store) { Failure = SaveFailure.AcknowledgementLost, RequireBoundRequests = true };
        var first = new OptionsViewModel(client, new FileConfigurationSaveOperationStore(path));
        await first.InitialiseAsync();
        first.MinimumVersionsPerFile = 31;
        client.SaveDispatchCheck = request =>
        {
            var record = Assert.IsType<PendingConfigurationSave>(new FileConfigurationSaveOperationStore(path).Read());
            Assert.Equal(ConfigurationSaveOrigin.Options, record.Origin);
            Assert.Equal(request.OperationId, record.OperationId);
            Assert.Equal(request.ExpectedVaultRevision, record.Revision);
            Assert.Equal(JsonSerializer.Serialize(request.Configuration), JsonSerializer.Serialize(record.Configuration));
            Assert.False(record.PurgeRemovedSelections);
            Assert.Empty(record.RemovedSelections); Assert.Empty(record.PreservedSelections);
        };
        await first.SaveAsync();
        var dispatched = Assert.Single(client.Requests, r => r.Command == FluxVaultIpcCommand.SaveConfiguration);
        var restarted = new OptionsViewModel(client, new FileConfigurationSaveOperationStore(path));
        await restarted.InitialiseAsync();
        Assert.True(restarted.HasUnconfirmedSave);
        Assert.Equal(31, restarted.MinimumVersionsPerFile);
        restarted.MinimumVersionsPerFile = 40;
        await restarted.InitialiseAsync(); // Loaded/explicit initialisation must not erase newer edits.
        Assert.Equal(40, restarted.MinimumVersionsPerFile);
        await restarted.SaveAsync();
        Assert.Single(client.Requests, r => r.Command == FluxVaultIpcCommand.SaveConfiguration);
        await restarted.CheckSaveOutcomeAsync();
        Assert.Equal(dispatched.OperationId, Assert.Single(client.Requests, r => r.Command == FluxVaultIpcCommand.GetOperationStatus).OperationId);
        Assert.False(restarted.HasUnconfirmedSave);
        Assert.False(File.Exists(path));
        Assert.Equal(40, restarted.MinimumVersionsPerFile);
        client.Failure = SaveFailure.None;
        await restarted.SaveAsync();
        Assert.Equal(40, (await fixture.Reopen().LoadAsync()).RetentionPolicy.MinimumVersionsPerFile);
        Assert.DoesNotContain(FluxVaultIpcCommand.RunBackupNow, client.Commands);
    }

    [Theory]
    [InlineData(FluxVaultIpcErrorCode.Denied)]
    [InlineData(FluxVaultIpcErrorCode.Unavailable)]
    [InlineData(FluxVaultIpcErrorCode.OutcomeUnknown)]
    public async Task Options_failed_receipt_never_releases_the_pending_snapshot(FluxVaultIpcErrorCode error)
    {
        using var fixture = new StoreFixture(false);
        await fixture.Store.SaveAsync(NonDefaultConfiguration(fixture.Root));
        var client = new StoreClient(fixture.Store) { Failure = SaveFailure.AcknowledgementLost, ReceiptErrorCode = error };
        var options = new OptionsViewModel(client);
        await options.InitialiseAsync(); options.MinimumVersionsPerFile = 31;
        await options.SaveAsync();
        options.MinimumVersionsPerFile = 40;
        await options.CheckSaveOutcomeAsync();
        Assert.True(options.HasUnconfirmedSave);
        Assert.Equal(40, options.MinimumVersionsPerFile);
        await options.SaveAsync();
        Assert.Single(client.Requests, r => r.Command == FluxVaultIpcCommand.SaveConfiguration);
        Assert.Contains("kept", options.StatusText);
    }

    [Fact]
    public async Task Options_historical_receipt_requires_review_when_current_settings_differ()
    {
        using var fixture = new StoreFixture(false);
        await fixture.Store.SaveAsync(NonDefaultConfiguration(fixture.Root));
        var client = new StoreClient(fixture.Store) { Failure = SaveFailure.AcknowledgementLost };
        var path = Path.Combine(fixture.Root, "pending-protection-save.json");
        var options = new OptionsViewModel(client, new FileConfigurationSaveOperationStore(path));
        await options.InitialiseAsync(); options.MinimumVersionsPerFile = 31;
        await options.SaveAsync();
        var saved = await fixture.Store.LoadAsync();
        await fixture.Store.SaveAsync(saved with { DiagnosticsPolicy = saved.DiagnosticsPolicy with { RetainedLogFileCount = 6 } });
        options.MinimumVersionsPerFile = 40;
        await options.CheckSaveOutcomeAsync();
        Assert.True(options.HasUnconfirmedSave);
        Assert.True(File.Exists(path));
        Assert.Equal(40, options.MinimumVersionsPerFile);
        Assert.Contains("review", options.StatusText, StringComparison.OrdinalIgnoreCase);
        await options.SaveAsync();
        Assert.Single(client.Requests, r => r.Command == FluxVaultIpcCommand.SaveConfiguration);
        Assert.Equal(6, (await fixture.Reopen().LoadAsync()).DiagnosticsPolicy.RetainedLogFileCount);
    }

    [Fact]
    public async Task Options_reload_preserves_newer_edits_and_pending_record_while_status_is_in_flight()
    {
        using var fixture = new StoreFixture(false);
        await fixture.Store.SaveAsync(NonDefaultConfiguration(fixture.Root));
        var client = new StoreClient(fixture.Store) { Failure = SaveFailure.AcknowledgementLost };
        var store = new FileConfigurationSaveOperationStore(Path.Combine(fixture.Root, "pending-protection-save.json"));
        var options = new OptionsViewModel(client, store);
        await options.InitialiseAsync(); options.MinimumVersionsPerFile = 31;
        await options.SaveAsync();
        var saved = await fixture.Store.LoadAsync();
        await fixture.Store.SaveAsync(saved with { DiagnosticsPolicy = saved.DiagnosticsPolicy with { RetainedLogFileCount = 6 } });
        await options.CheckSaveOutcomeAsync();
        Assert.True(options.CanReloadSavedOptions);
        options.MinimumVersionsPerFile = 40;
        client.HoldStatus = true;
        var reload = options.ReloadSavedOptionsAsync();
        try
        {
            await client.StatusEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            options.MinimumVersionsPerFile = 42;
            client.ReleaseStatus.TrySetResult();
            await reload.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(42, options.MinimumVersionsPerFile);
            Assert.True(options.HasUnconfirmedSave);
            Assert.NotNull(store.Read());
            Assert.Contains("newer edits", options.StatusText, StringComparison.OrdinalIgnoreCase);
        }
        finally { client.ReleaseStatus.TrySetResult(); await reload.WaitAsync(TimeSpan.FromSeconds(10)); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Options_reviewed_reload_clears_only_its_record_and_loads_current_real_settings(bool profileStore)
    {
        using var fixture = new StoreFixture(profileStore);
        await fixture.Store.SaveAsync(NonDefaultConfiguration(fixture.Root));
        var client = new StoreClient(fixture.Store) { Failure = SaveFailure.AcknowledgementLost };
        var store = new FileConfigurationSaveOperationStore(Path.Combine(fixture.Root, "pending-protection-save.json"));
        var options = new OptionsViewModel(client, store);
        await options.InitialiseAsync(); options.MinimumVersionsPerFile = 31;
        await options.SaveAsync();
        var saved = await fixture.Store.LoadAsync();
        await fixture.Store.SaveAsync(saved with { DiagnosticsPolicy = saved.DiagnosticsPolicy with { RetainedLogFileCount = 6 } });
        options.MinimumVersionsPerFile = 40;
        await options.CheckSaveOutcomeAsync();
        Assert.True(options.CanReloadSavedOptions);
        await options.ReloadSavedOptionsAsync();
        Assert.False(options.HasUnconfirmedSave);
        Assert.Null(store.Read());
        Assert.Equal(31, options.MinimumVersionsPerFile);
        Assert.Equal(6, options.DiagnosticsRetainedLogFileCount);
        Assert.DoesNotContain(FluxVaultIpcCommand.RunBackupNow, client.Commands);
    }

    [Fact]
    public async Task Options_reload_never_clears_a_changed_cross_session_record()
    {
        using var fixture = new StoreFixture(false);
        await fixture.Store.SaveAsync(NonDefaultConfiguration(fixture.Root));
        var client = new StoreClient(fixture.Store) { Failure = SaveFailure.AcknowledgementLost };
        var store = new FileConfigurationSaveOperationStore(Path.Combine(fixture.Root, "pending-protection-save.json"));
        var options = new OptionsViewModel(client, store);
        await options.InitialiseAsync(); options.MinimumVersionsPerFile = 31;
        await options.SaveAsync();
        var reviewed = store.Read()!;
        var saved = await fixture.Store.LoadAsync();
        await fixture.Store.SaveAsync(saved with { DiagnosticsPolicy = saved.DiagnosticsPolicy with { RetainedLogFileCount = 6 } });
        await options.CheckSaveOutcomeAsync();
        options.MinimumVersionsPerFile = 40;
        client.HoldStatus = true;
        var reload = options.ReloadSavedOptionsAsync();
        try
        {
            await client.StatusEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            store.Clear(reviewed);
            var newer = reviewed with { OperationId = Guid.NewGuid() };
            store.Reserve(newer);
            client.ReleaseStatus.TrySetResult(); await reload.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(newer.OperationId, store.Read()!.OperationId);
            Assert.Equal(40, options.MinimumVersionsPerFile);
            Assert.True(options.HasUnconfirmedSave);
            Assert.False(options.CanReloadSavedOptions);
        }
        finally { client.ReleaseStatus.TrySetResult(); await reload.WaitAsync(TimeSpan.FromSeconds(10)); }
    }

    [Fact]
    public async Task Options_never_adopts_or_clears_a_protection_origin_record()
    {
        using var fixture = new StoreFixture(false);
        var configuration = NonDefaultConfiguration(fixture.Root);
        await fixture.Store.SaveAsync(configuration);
        var client = new StoreClient(fixture.Store);
        var path = Path.Combine(fixture.Root, "pending-protection-save.json");
        var store = new FileConfigurationSaveOperationStore(path);
        var pending = new PendingConfigurationSave(client.Identity.Value, Guid.NewGuid(), 1,
            configuration with { RetentionPolicy = configuration.RetentionPolicy with { MinimumVersionsPerFile = 31 } }, false, [], []);
        store.Reserve(pending);
        var options = new OptionsViewModel(client, store);
        await options.InitialiseAsync();
        Assert.Equal(20, options.MinimumVersionsPerFile);
        Assert.True(options.HasUnconfirmedSave);
        await options.CheckSaveOutcomeAsync(); await options.SaveAsync();
        Assert.DoesNotContain(FluxVaultIpcCommand.GetOperationStatus, client.Commands);
        Assert.DoesNotContain(FluxVaultIpcCommand.SaveConfiguration, client.Commands);
        Assert.Equal(pending.OperationId, store.Read()!.OperationId);
    }

    [Fact]
    public async Task Pending_Options_recovery_opens_without_erasing_protection_edits_on_close()
    {
        using var fixture = new StoreFixture(false);
        var configuration = NonDefaultConfiguration(fixture.Root);
        await fixture.Store.SaveAsync(configuration);
        var client = new StoreClient(fixture.Store);
        var store = new FileConfigurationSaveOperationStore(Path.Combine(fixture.Root, "pending-protection-save.json"));
        var dashboard = CreateViewModel(client, saveStore: store);
        await dashboard.RefreshAsync();
        var selection = Selection(fixture.Root, "pending-protection-folder");
        dashboard.FileBrowser.ReplaceSelectionRule(selection);
        store.Reserve(new(client.Identity.Value, Guid.NewGuid(), 1, configuration, false, [], [], ConfigurationSaveOrigin.Options));
        Assert.True(dashboard.TryBeginOptionsEditing());
        Assert.False(dashboard.CanSaveProtection);
        await dashboard.EndOptionsEditingAsync();
        Assert.Equal(selection.Path, Assert.Single(dashboard.FileBrowser.GetSelectionRules()).Path);
        Assert.NotEmpty(dashboard.FileBrowser.PendingChanges);
        await dashboard.RunBackupNowCommand.ExecuteAsync(null);
        Assert.DoesNotContain(FluxVaultIpcCommand.RunBackupNow, client.Commands);
        Assert.DoesNotContain(FluxVaultIpcCommand.SaveConfiguration, client.Commands);
        Assert.NotNull(store.Read());
    }

    [Fact]
    public async Task Options_recovery_close_preserves_protection_draft_without_rebasing_onto_changed_options()
    {
        using var fixture = new StoreFixture(false);
        var configuration = NonDefaultConfiguration(fixture.Root);
        await fixture.Store.SaveAsync(configuration);
        var client = new StoreClient(fixture.Store) { Failure = SaveFailure.AcknowledgementLost };
        var store = new FileConfigurationSaveOperationStore(Path.Combine(fixture.Root, "pending-protection-save.json"));
        var dashboard = CreateViewModel(client, saveStore: store);
        await dashboard.RefreshAsync();
        var options = new OptionsViewModel(client, store);
        await options.InitialiseAsync(); options.MinimumVersionsPerFile = 31;
        await options.SaveAsync();
        var selection = Selection(fixture.Root, "pending-protection-folder");
        dashboard.FileBrowser.ReplaceSelectionRule(selection);
        Assert.True(dashboard.TryBeginOptionsEditing());
        await options.CheckSaveOutcomeAsync();
        Assert.Null(store.Read());
        await dashboard.EndOptionsEditingAsync();
        Assert.Equal(selection.Path, Assert.Single(dashboard.FileBrowser.GetSelectionRules()).Path);
        Assert.NotEmpty(dashboard.FileBrowser.PendingChanges);
        Assert.False(dashboard.CanSaveProtection);
        await dashboard.RunBackupNowCommand.ExecuteAsync(null);
        Assert.Single(client.Requests, r => r.Command == FluxVaultIpcCommand.SaveConfiguration);
        Assert.DoesNotContain(FluxVaultIpcCommand.RunBackupNow, client.Commands);
        Assert.Equal(31, (await fixture.Reopen().LoadAsync()).RetentionPolicy.MinimumVersionsPerFile);
        await dashboard.DiscardConfigurationChangesCommand.ExecuteAsync(null);
        Assert.True(dashboard.CanSaveProtection);
    }

    [Theory]
    [InlineData("corrupt")]
    [InlineData("busy")]
    [InlineData("wrong-binding")]
    public async Task Options_unreadable_or_foreign_record_blocks_dispatch_without_losing_edits(string fault)
    {
        using var fixture = new StoreFixture(false);
        var configuration = NonDefaultConfiguration(fixture.Root);
        await fixture.Store.SaveAsync(configuration);
        var client = new StoreClient(fixture.Store);
        var path = Path.Combine(fixture.Root, "pending-protection-save.json");
        var store = new FileConfigurationSaveOperationStore(path);
        if (fault == "corrupt") File.WriteAllText(path, "{");
        else store.Reserve(new(fault == "wrong-binding" ? Guid.NewGuid() : client.Identity.Value, Guid.NewGuid(), 1,
            configuration, false, [], [], ConfigurationSaveOrigin.Options));
        var bytes = File.ReadAllBytes(path);
        using var gate = fault == "busy" ? new FileStream(path + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None) : null;
        var options = new OptionsViewModel(client, store);
        await options.InitialiseAsync(); options.MinimumVersionsPerFile = 40;
        await options.SaveAsync(); await options.CheckSaveOutcomeAsync(); await options.ReloadSavedOptionsAsync();
        Assert.True(options.HasUnconfirmedSave);
        Assert.Equal(40, options.MinimumVersionsPerFile);
        Assert.Equal(bytes, File.ReadAllBytes(path));
        Assert.DoesNotContain(FluxVaultIpcCommand.SaveConfiguration, client.Commands);
        Assert.DoesNotContain(FluxVaultIpcCommand.GetOperationStatus, client.Commands);
        Assert.DoesNotContain(FluxVaultIpcCommand.RunBackupNow, client.Commands);
    }

    [Theory]
    [InlineData(SaveFailure.Io)]
    [InlineData(SaveFailure.Timeout)]
    [InlineData(SaveFailure.Cancelled)]
    [InlineData(SaveFailure.Denied)]
    public async Task Options_transport_failure_keeps_pending_snapshot_across_restart_and_blocks_backup(SaveFailure failure)
    {
        using var fixture = new StoreFixture(false);
        await fixture.Store.SaveAsync(NonDefaultConfiguration(fixture.Root));
        var client = new StoreClient(fixture.Store) { Failure = failure };
        var store = new FileConfigurationSaveOperationStore(Path.Combine(fixture.Root, "pending-protection-save.json"));
        var options = new OptionsViewModel(client, store);
        await options.InitialiseAsync(); options.MinimumVersionsPerFile = 31;
        await options.SaveAsync();
        var dispatched = Assert.Single(client.Requests, r => r.Command == FluxVaultIpcCommand.SaveConfiguration);
        var pending = store.Read()!;
        Assert.Equal(dispatched.OperationId, pending.OperationId);
        var reopened = new OptionsViewModel(client, store);
        await reopened.InitialiseAsync();
        Assert.True(reopened.HasUnconfirmedSave);
        Assert.Equal(31, reopened.MinimumVersionsPerFile);
        var dashboard = CreateViewModel(client, saveStore: store);
        await dashboard.RefreshAsync();
        await dashboard.RunBackupNowCommand.ExecuteAsync(null);
        Assert.Single(client.Requests, r => r.Command == FluxVaultIpcCommand.SaveConfiguration);
        Assert.DoesNotContain(FluxVaultIpcCommand.RunBackupNow, client.Commands);
        Assert.Contains("kept", reopened.StatusText);
        Assert.Equal(20, (await fixture.Reopen().LoadAsync()).RetentionPolicy.MinimumVersionsPerFile);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(3)]
    public async Task Options_receipt_requires_the_original_save_revision_plus_one(long revision)
    {
        using var fixture = new StoreFixture(false);
        await fixture.Store.SaveAsync(NonDefaultConfiguration(fixture.Root));
        var client = new StoreClient(fixture.Store) { Failure = SaveFailure.AcknowledgementLost, ReceiptRevisionOverride = revision };
        var store = new FileConfigurationSaveOperationStore(Path.Combine(fixture.Root, "pending-protection-save.json"));
        var options = new OptionsViewModel(client, store);
        await options.InitialiseAsync(); options.MinimumVersionsPerFile = 31; await options.SaveAsync();
        await options.CheckSaveOutcomeAsync();
        Assert.True(options.HasUnconfirmedSave);
        Assert.False(options.CanReloadSavedOptions);
        Assert.NotNull(store.Read());
        Assert.Equal(31, options.MinimumVersionsPerFile);
    }

    [Fact]
    public Task Options_visible_recovery_actions_use_the_shared_store_and_keep_an_in_flight_reload_owned() => RunOnStaAsync(async () =>
    {
        using var fixture = new StoreFixture(false);
        await fixture.Store.SaveAsync(NonDefaultConfiguration(fixture.Root));
        var client = new StoreClient(fixture.Store) { Failure = SaveFailure.AcknowledgementLost };
        var store = new FileConfigurationSaveOperationStore(Path.Combine(fixture.Root, "pending-protection-save.json"));
        var first = new OptionsViewModel(client, store);
        await first.InitialiseAsync(); first.MinimumVersionsPerFile = 31; await first.SaveAsync();
        var saved = await fixture.Store.LoadAsync();
        await fixture.Store.SaveAsync(saved with { DiagnosticsPolicy = saved.DiagnosticsPolicy with { RetainedLogFileCount = 6 } });
        var reopened = new OptionsViewModel(client, store);
        var window = new OptionsWindow(reopened) { Width = 640, Height = 620 };
        Task? reload = null;
        try
        {
            window.Show(); await reopened.InitialiseAsync(); window.UpdateLayout();
            var check = Assert.IsType<Button>(window.FindName("CheckOptionsSaveOutcomeButton"));
            var reloadButton = Assert.IsType<Button>(window.FindName("ReloadSavedOptionsButton"));
            var saveButton = Assert.IsType<Button>(window.FindName("SaveOptionsButton"));
            Assert.Same(reopened.CheckSaveOutcomeCommand, check.Command);
            Assert.Same(reopened.ReloadSavedOptionsCommand, reloadButton.Command);
            Assert.True(check.IsEnabled); Assert.False(reloadButton.IsEnabled);
            Assert.False(saveButton.IsEnabled);
            await reopened.CheckSaveOutcomeCommand.ExecuteAsync(null);
            window.UpdateLayout(); Assert.True(reloadButton.IsEnabled);
            var footer = Assert.IsType<WrapPanel>(window.FindName("OptionsStickyFooter"));
            Assert.True(footer.ActualWidth <= Assert.IsType<Grid>(window.FindName("OptionsShell")).ActualWidth);
            var optionsShell = Assert.IsType<Grid>(window.FindName("OptionsShell"));
            foreach (Button button in footer.Children)
            {
                Assert.True(button.ActualWidth > 0 && button.ActualHeight > 0);
                var bounds = button.TransformToAncestor(optionsShell).TransformBounds(new Rect(button.RenderSize));
                Assert.True(bounds.Left >= 0 && bounds.Top >= 0 && bounds.Right <= optionsShell.ActualWidth + 1 && bounds.Bottom <= optionsShell.ActualHeight + 1);
            }
            Assert.Equal("Check save outcome", System.Windows.Automation.Peers.UIElementAutomationPeer.CreatePeerForElement(check)!.GetName());
            Assert.Equal("Reload saved options", System.Windows.Automation.Peers.UIElementAutomationPeer.CreatePeerForElement(reloadButton)!.GetName());
            var renderPath = Environment.GetEnvironmentVariable("FLUXVAULT_OPTIONS_RECOVERY_RENDER");
            if (!string.IsNullOrEmpty(renderPath))
            {
                var shell = Assert.IsType<Grid>(window.FindName("OptionsShell"));
                var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap((int)Math.Ceiling(shell.ActualWidth),
                    (int)Math.Ceiling(shell.ActualHeight), 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
                var background = new System.Windows.Media.DrawingVisual();
                using (var drawing = background.RenderOpen())
                {
                    drawing.DrawRectangle(window.Background, null, new Rect(0, 0, shell.ActualWidth, shell.ActualHeight));
                    drawing.DrawRectangle(new System.Windows.Media.VisualBrush(shell), null, new Rect(0, 0, shell.ActualWidth, shell.ActualHeight));
                }
                bitmap.Render(background);
                var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
                encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
                using var output = File.Create(renderPath); encoder.Save(output);
            }
            client.HoldStatus = true;
            reload = reopened.ReloadSavedOptionsCommand.ExecuteAsync(null);
            await client.StatusEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            window.Close(); Assert.True(window.IsVisible);
            client.ReleaseStatus.TrySetResult(); await reload.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Null(store.Read()); Assert.Equal(6, reopened.DiagnosticsRetainedLogFileCount);
            window.Close(); Assert.False(window.IsVisible);
        }
        finally
        {
            client.ReleaseStatus.TrySetResult();
            if (reload is not null) await reload.WaitAsync(TimeSpan.FromSeconds(10));
            window.Close();
        }
    });

    [Fact]
    public async Task Options_lost_save_acknowledgement_reconciles_receipt_without_erasing_newer_edits()
    {
        using var fixture = new StoreFixture(false);
        await fixture.Store.SaveAsync(NonDefaultConfiguration(fixture.Root));
        var client = new StoreClient(fixture.Store) { Failure = SaveFailure.AcknowledgementLost, RequireBoundRequests = true };
        var options = new OptionsViewModel(client);
        await options.InitialiseAsync();
        options.MinimumVersionsPerFile = 31;
        var exception = await Record.ExceptionAsync(() => options.SaveAsync());
        Assert.Null(exception);
        Assert.Equal(31, (await fixture.Reopen().LoadAsync()).RetentionPolicy.MinimumVersionsPerFile);
        var dispatched = Assert.Single(client.Requests, r => r.Command == FluxVaultIpcCommand.SaveConfiguration);
        options.MinimumVersionsPerFile = 40;
        await options.CheckSaveOutcomeAsync();
        Assert.Equal(dispatched.OperationId, Assert.Single(client.Requests, r => r.Command == FluxVaultIpcCommand.GetOperationStatus).OperationId);
        Assert.Equal(40, options.MinimumVersionsPerFile);
        client.Failure = SaveFailure.None;
        await options.SaveAsync();
        Assert.Equal(40, (await fixture.Reopen().LoadAsync()).RetentionPolicy.MinimumVersionsPerFile);
    }

    [Fact]
    public async Task Unknown_save_cannot_rebase_onto_unrelated_newer_settings_and_overwrite_them()
    {
        using var fixture = new StoreFixture(false);
        await fixture.Store.SaveAsync(NonDefaultConfiguration(fixture.Root));
        var client = new StoreClient(fixture.Store) { Failure = SaveFailure.AcknowledgementLost };
        var dashboard = CreateViewModel(client);
        await dashboard.RefreshAsync();
        dashboard.FileBrowser.ReplaceSelectionRule(Selection(fixture.Root, "draft"));
        await dashboard.SaveConfigurationCommand.ExecuteAsync(null);
        var saved = await fixture.Store.LoadAsync();
        await fixture.Store.SaveAsync(saved with { DiagnosticsPolicy = saved.DiagnosticsPolicy with { RetainedLogFileCount = 6 } });
        client.Failure = SaveFailure.None;
        await dashboard.RefreshAsync();
        await dashboard.RunBackupNowCommand.ExecuteAsync(null);
        Assert.NotEmpty(dashboard.FileBrowser.PendingChanges);
        Assert.DoesNotContain(FluxVaultIpcCommand.RunBackupNow, client.Commands);
        Assert.Single(client.Requests, r => r.Command == FluxVaultIpcCommand.SaveConfiguration);
        Assert.Equal(6, (await fixture.Reopen().LoadAsync()).DiagnosticsPolicy.RetainedLogFileCount);
    }

    [Fact]
    public async Task Options_save_uses_its_accepted_snapshot_and_preserves_unedited_settings_through_real_store()
    {
        using var fixture = new StoreFixture(false);
        await fixture.Store.SaveAsync(NonDefaultConfiguration(fixture.Root));
        var before = await fixture.Store.LoadAsync();
        var client = new StoreClient(fixture.Store) { RequireBoundRequests = true };
        var options = new OptionsViewModel(client);
        await options.InitialiseAsync();
        options.MinimumVersionsPerFile = 31;
        await options.SaveAsync();
        var request = Assert.Single(client.Requests, r => r.Command == FluxVaultIpcCommand.SaveConfiguration);
        Assert.Equal(client.Identity, request.VaultId);
        Assert.Equal(1, request.ExpectedVaultRevision);
        Assert.NotNull(request.OperationId);
        var after = await fixture.Reopen().LoadAsync();
        Assert.Equal(31, after.RetentionPolicy.MinimumVersionsPerFile);
        Assert.Equal(JsonSerializer.Serialize(before.Sync), JsonSerializer.Serialize(after.Sync));
        Assert.Equal(before.IsEnabled, after.IsEnabled);
        Assert.Equal(before.SelectionRules, after.SelectionRules);
        Assert.Contains("saved", options.StatusText);
    }

    [Fact]
    public async Task Options_stale_snapshot_cannot_borrow_a_dashboard_revision_to_overwrite_newer_settings()
    {
        using var fixture = new StoreFixture(false);
        await fixture.Store.SaveAsync(NonDefaultConfiguration(fixture.Root));
        var client = new StoreClient(fixture.Store) { RequireBoundRequests = true };
        var options = new OptionsViewModel(client);
        await options.InitialiseAsync();
        options.MinimumVersionsPerFile = 31;
        var dashboard = CreateViewModel(client);
        await dashboard.RefreshAsync();
        dashboard.FileBrowser.ReplaceSelectionRule(Selection(fixture.Root, "newer-selection"));
        await dashboard.SaveConfigurationCommand.ExecuteAsync(null);
        await options.SaveAsync();
        Assert.Equal(31, options.MinimumVersionsPerFile);
        Assert.Contains("failed", options.StatusText);
        Assert.Single((await fixture.Reopen().LoadAsync()).SelectionRules);
        Assert.Equal(20, (await fixture.Store.LoadAsync()).RetentionPolicy.MinimumVersionsPerFile);
    }

    [Theory]
    [InlineData(SaveFailure.Io)]
    [InlineData(SaveFailure.Timeout)]
    [InlineData(SaveFailure.Cancelled)]
    [InlineData(SaveFailure.Denied)]
    public async Task Lost_backup_acknowledgement_is_handled_and_remains_visible_after_refresh(SaveFailure failure)
    {
        using var fixture = new StoreFixture(false);
        await fixture.Store.SaveAsync(NonDefaultConfiguration(fixture.Root));
        var client = new StoreClient(fixture.Store) { BackupFailure = failure };
        var viewModel = CreateViewModel(client);
        await viewModel.RefreshAsync();
        viewModel.FileBrowser.ReplaceSelectionRule(Selection(fixture.Root, "backup-project"));
        var exception = await Record.ExceptionAsync(() => viewModel.RunBackupNowCommand.ExecuteAsync(null));
        Assert.Null(exception);
        Assert.Single((await fixture.Reopen().LoadAsync()).SelectionRules);
        var dispatched = Assert.Single(client.Requests, r => r.Command == FluxVaultIpcCommand.RunBackupNow);
        Assert.NotNull(dispatched.OperationId);
        Assert.Equal(dispatched.OperationId, viewModel.UnconfirmedBackupOperationId);
        await viewModel.RefreshAsync();
        Assert.Contains("backup outcome could not be confirmed", viewModel.ServiceStatus, StringComparison.OrdinalIgnoreCase);
        await viewModel.RunBackupNowCommand.ExecuteAsync(null);
        Assert.Single(client.Requests, r => r.Command == FluxVaultIpcCommand.RunBackupNow);
        await viewModel.CheckBackupOutcomeCommand.ExecuteAsync(null);
        var check = Assert.Single(client.Requests, r => r.Command == FluxVaultIpcCommand.GetOperationStatus);
        Assert.Equal(dispatched.OperationId, check.OperationId);
        Assert.Null(viewModel.UnconfirmedBackupOperationId);
    }

    [Theory]
    [InlineData(EnvelopeFault.WrongIdentity)]
    [InlineData(EnvelopeFault.MissingOperation)]
    [InlineData(EnvelopeFault.WrongRevision)]
    public async Task A_committed_save_with_mismatched_acknowledgement_keeps_draft_and_never_starts_backup(EnvelopeFault fault)
    {
        using var fixture = new StoreFixture(false);
        await fixture.Store.SaveAsync(NonDefaultConfiguration(fixture.Root));
        var client = new StoreClient(fixture.Store) { SaveEnvelopeFault = fault };
        var viewModel = CreateViewModel(client);
        await viewModel.RefreshAsync();
        viewModel.FileBrowser.ReplaceSelectionRule(Selection(fixture.Root, "retained-project"));
        await viewModel.RunBackupNowCommand.ExecuteAsync(null);
        Assert.Single((await fixture.Reopen().LoadAsync()).SelectionRules);
        Assert.NotEmpty(viewModel.FileBrowser.PendingChanges);
        Assert.Equal(ProtectionSaveState.Unknown, viewModel.ProtectionSaveState);
        Assert.Contains("could not be confirmed", viewModel.ProtectionSaveMessage);
        Assert.DoesNotContain(FluxVaultIpcCommand.RunBackupNow, client.Commands);
    }

    public enum EnvelopeFault { None, WrongIdentity, MissingOperation, WrongRevision }
    [Fact]
    public async Task Actual_save_then_backup_uses_installed_identity_and_acknowledged_revision_without_profile_routing()
    {
        using var fixture = new StoreFixture(false);
        await fixture.Store.SaveAsync(NonDefaultConfiguration(fixture.Root) with { IsEnabled = true });
        var client = new StoreClient(fixture.Store);
        var viewModel = CreateViewModel(client);
        await viewModel.RefreshAsync();
        viewModel.FileBrowser.ReplaceSelectionRule(Selection(fixture.Root, "single-vault-project"));
        await viewModel.RunBackupNowCommand.ExecuteAsync(null);
        var save = Assert.Single(client.Requests, request => request.Command == FluxVaultIpcCommand.SaveConfiguration);
        var backup = Assert.Single(client.Requests, request => request.Command == FluxVaultIpcCommand.RunBackupNow);
        Assert.Equal(client.Identity, save.VaultId);
        Assert.Equal(client.Identity, backup.VaultId);
        Assert.Equal(1, save.ExpectedVaultRevision);
        Assert.Equal(2, backup.ExpectedVaultRevision);
        Assert.NotNull(save.OperationId); Assert.NotEqual(Guid.Empty, save.OperationId);
        Assert.NotNull(backup.OperationId); Assert.NotEqual(save.OperationId, backup.OperationId);
        Assert.Null(save.ProfileId); Assert.Null(backup.ProfileId);
        Assert.Single((await fixture.Reopen().LoadAsync()).SelectionRules);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Saving_protection_selection_preserves_every_untouched_setting_through_real_store(bool profileStore)
    {
        using var fixture = new StoreFixture(profileStore);
        await fixture.Store.SaveAsync(NonDefaultConfiguration(fixture.Root));
        var before = await fixture.Store.LoadAsync();
        var client = new StoreClient(fixture.Store);
        var viewModel = CreateViewModel(client);
        await viewModel.RefreshAsync();
        var selection = Selection(fixture.Root, "new-project");
        viewModel.FileBrowser.ReplaceSelectionRule(selection);

        await viewModel.SaveConfigurationCommand.ExecuteAsync(null);

        // Recreate the production store to prove the result survived disk serialisation.
        var after = await fixture.Reopen().LoadAsync();
        Assert.Equal(selection.Path, Assert.Single(after.SelectionRules).Path);
        Assert.Equal(selection.Path, Assert.Single(after.WatchedFolders).Path);
        var expected = JsonSerializer.SerializeToNode(before)!.AsObject();
        var actual = JsonSerializer.SerializeToNode(after)!.AsObject();
        var changedUntouchedSettings = expected
            .Where(pair => pair.Key is not nameof(FluxVaultConfiguration.SelectionRules)
                and not nameof(FluxVaultConfiguration.WatchedFolders))
            .Where(pair => !JsonNode.DeepEquals(pair.Value, actual[pair.Key]))
            .Select(pair => pair.Key).ToArray();
        Assert.Empty(changedUntouchedSettings);
        Assert.Empty(viewModel.FileBrowser.PendingChanges);
    }

    [Theory]
    [InlineData(SaveFailure.Rejected)]
    [InlineData(SaveFailure.Io)]
    [InlineData(SaveFailure.Timeout)]
    [InlineData(SaveFailure.Denied)]
    [InlineData(SaveFailure.Cancelled)]
    public async Task Failed_or_cancelled_save_keeps_draft_explains_failure_and_never_starts_backup(SaveFailure failure)
    {
        using var fixture = new StoreFixture(profileStore: true);
        await fixture.Store.SaveAsync(NonDefaultConfiguration(fixture.Root));
        var before = await File.ReadAllBytesAsync(fixture.ConfigPath);
        var client = new StoreClient(fixture.Store) { Failure = failure };
        var viewModel = CreateViewModel(client);
        await viewModel.RefreshAsync();
        var selection = Selection(fixture.Root, "pending-project");
        viewModel.FileBrowser.ReplaceSelectionRule(selection);

        var exception = await Record.ExceptionAsync(() => viewModel.RunBackupNowCommand.ExecuteAsync(null));

        Assert.Equal(before, await File.ReadAllBytesAsync(fixture.ConfigPath));
        Assert.Equal(selection.Path, Assert.Single(viewModel.FileBrowser.GetSelectionRules()).Path);
        Assert.NotEmpty(viewModel.FileBrowser.PendingChanges);
        var visibleResult = viewModel.ServiceStatus;
        await viewModel.RefreshAsync();
        Assert.Equal(selection.Path, Assert.Single(viewModel.FileBrowser.GetSelectionRules()).Path);
        Assert.NotEmpty(viewModel.FileBrowser.PendingChanges);
        Assert.Null(exception);
        Assert.DoesNotContain(FluxVaultIpcCommand.RunBackupNow, client.Commands);
        Assert.Contains(failure == SaveFailure.Cancelled ? "cancel" : "save", visibleResult, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("not started", visibleResult, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("not started", viewModel.ServiceStatus, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Cancelling_removal_confirmation_keeps_edits_and_sends_neither_save_nor_backup()
    {
        using var fixture = new StoreFixture(profileStore: true);
        var existing = Selection(fixture.Root, "existing-project");
        var configuration = NonDefaultConfiguration(fixture.Root) with
        {
            SelectionRules = [existing],
            WatchedFolders = ProtectionSelectionCompiler.Compile([existing])
        };
        await fixture.Store.SaveAsync(configuration);
        var before = await File.ReadAllBytesAsync(fixture.ConfigPath);
        var client = new StoreClient(fixture.Store);
        var confirmation = new CancelRemoval();
        var viewModel = CreateViewModel(client, confirmation);
        await viewModel.RefreshAsync();
        Assert.True(viewModel.FileBrowser.RemovePathSelection(existing.Path, isDirectory: true));
        var added = Selection(fixture.Root, "replacement-project");
        viewModel.FileBrowser.ReplaceSelectionRule(added);

        await viewModel.RunBackupNowCommand.ExecuteAsync(null);

        Assert.Equal(1, confirmation.Calls);
        Assert.Equal(before, await File.ReadAllBytesAsync(fixture.ConfigPath));
        Assert.Equal(added.Path, Assert.Single(viewModel.FileBrowser.GetSelectionRules()).Path);
        Assert.NotEmpty(viewModel.FileBrowser.PendingChanges);
        Assert.DoesNotContain(FluxVaultIpcCommand.SaveConfiguration, client.Commands);
        Assert.DoesNotContain(FluxVaultIpcCommand.RunBackupNow, client.Commands);
        Assert.Contains("cancel", viewModel.ServiceStatus, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("not started", viewModel.ServiceStatus, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Successful_save_is_persisted_before_exactly_one_dependent_backup()
    {
        using var fixture = new StoreFixture(profileStore: true);
        await fixture.Store.SaveAsync(FluxVaultConfiguration.CreateDefault(fixture.Root));
        var client = new StoreClient(fixture.Store) { HoldSave = true };
        var viewModel = CreateViewModel(client);
        await viewModel.RefreshAsync();
        var selection = Selection(fixture.Root, "ready-project");
        viewModel.FileBrowser.ReplaceSelectionRule(selection);

        var command = viewModel.RunBackupNowCommand.ExecuteAsync(null);
        try
        {
            await client.SaveEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.DoesNotContain(FluxVaultIpcCommand.RunBackupNow, client.Commands);
            Assert.Empty((await fixture.Reopen().LoadAsync()).SelectionRules);
        }
        finally
        {
            client.ReleaseSave.TrySetResult();
            await command.WaitAsync(TimeSpan.FromSeconds(10));
        }

        Assert.Equal(new[] { "Save started", "Save persisted", "Backup requested" }, client.Events);
        Assert.Single(client.Commands, command => command == FluxVaultIpcCommand.RunBackupNow);
        Assert.Equal(selection.Path, Assert.Single(client.ConfigurationAtBackup!.SelectionRules).Path);
        Assert.Empty(viewModel.FileBrowser.PendingChanges);
        Assert.Equal(client.Identity, Assert.Single(client.Requests, request => request.Command == FluxVaultIpcCommand.RunBackupNow).VaultId);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Cancelled_status_reload_does_not_reclassify_an_acknowledged_save_or_discard_the_draft(bool discard)
    {
        using var fixture = new StoreFixture(true);
        await fixture.Store.SaveAsync(NonDefaultConfiguration(fixture.Root));
        var client = new StoreClient(fixture.Store);
        var viewModel = CreateViewModel(client);
        await viewModel.RefreshAsync();
        var selection = Selection(fixture.Root, "status-cancel-project");
        viewModel.FileBrowser.ReplaceSelectionRule(selection);
        client.StatusCancelled = true;

        var exception = await Record.ExceptionAsync(() => discard
            ? viewModel.DiscardConfigurationChangesCommand.ExecuteAsync(null)
            : viewModel.RunBackupNowCommand.ExecuteAsync(null));

        Assert.Null(exception);
        Assert.False(viewModel.IsProtectionSaveBusy);
        if (discard)
        {
            Assert.NotEmpty(viewModel.FileBrowser.PendingChanges);
            Assert.Empty((await fixture.Reopen().LoadAsync()).SelectionRules);
            Assert.Contains("Discard did not complete", viewModel.ProtectionSaveMessage);
            Assert.DoesNotContain(FluxVaultIpcCommand.RunBackupNow, client.Commands);
        }
        else
        {
            Assert.Equal(ProtectionSaveState.Saved, viewModel.ProtectionSaveState);
            Assert.Contains("Configuration saved", viewModel.ProtectionSaveMessage);
            Assert.Equal(selection.Path, Assert.Single((await fixture.Reopen().LoadAsync()).SelectionRules).Path);
            Assert.Empty(viewModel.FileBrowser.PendingChanges);
            Assert.Single(client.Requests, request => request.Command == FluxVaultIpcCommand.RunBackupNow);
        }
    }

    [Fact]
    public async Task Failure_explanation_survives_refresh_and_retry_saves_the_retained_draft()
    {
        using var fixture = new StoreFixture(true);
        await fixture.Store.SaveAsync(NonDefaultConfiguration(fixture.Root));
        var client = new StoreClient(fixture.Store) { Failure = SaveFailure.Rejected };
        var viewModel = CreateViewModel(client);
        await viewModel.RefreshAsync();
        var selection = Selection(fixture.Root, "retry-project");
        viewModel.FileBrowser.ReplaceSelectionRule(selection);
        await viewModel.SaveConfigurationCommand.ExecuteAsync(null);

        await viewModel.RefreshAsync();

        Assert.Contains("save failed", viewModel.ServiceStatus, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("kept", viewModel.ServiceStatus, StringComparison.OrdinalIgnoreCase);
        Assert.NotEmpty(viewModel.FileBrowser.PendingChanges);
        client.Failure = SaveFailure.None;
        await viewModel.RunBackupNowCommand.ExecuteAsync(null);
        Assert.Equal(selection.Path, Assert.Single((await fixture.Reopen().LoadAsync()).SelectionRules).Path);
        Assert.Empty(viewModel.FileBrowser.PendingChanges);
        Assert.Single(client.Requests, request => request.Command == FluxVaultIpcCommand.RunBackupNow);
        Assert.DoesNotContain("save failed", viewModel.ServiceStatus, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Explicit_discard_after_rejection_restores_accepted_configuration_without_backup()
    {
        using var fixture = new StoreFixture(true);
        await fixture.Store.SaveAsync(NonDefaultConfiguration(fixture.Root));
        var client = new StoreClient(fixture.Store) { Failure = SaveFailure.Rejected };
        var viewModel = CreateViewModel(client);
        await viewModel.RefreshAsync();
        viewModel.FileBrowser.ReplaceSelectionRule(Selection(fixture.Root, "discard-project"));
        await viewModel.SaveConfigurationCommand.ExecuteAsync(null);

        await viewModel.DiscardConfigurationChangesCommand.ExecuteAsync(null);

        Assert.Empty(viewModel.FileBrowser.GetSelectionRules());
        Assert.Empty(viewModel.FileBrowser.PendingChanges);
        Assert.DoesNotContain("save failed", viewModel.ServiceStatus, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(FluxVaultIpcCommand.RunBackupNow, client.Commands);
        Assert.False((await fixture.Reopen().LoadAsync()).IsEnabled);
    }

    [Fact]
    public async Task Edits_made_during_save_are_retained_and_stop_dependent_backup()
    {
        using var fixture = new StoreFixture(true);
        await fixture.Store.SaveAsync(FluxVaultConfiguration.CreateDefault(fixture.Root));
        var client = new StoreClient(fixture.Store) { HoldSave = true };
        var viewModel = CreateViewModel(client);
        await viewModel.RefreshAsync();
        var first = Selection(fixture.Root, "first-project");
        var second = Selection(fixture.Root, "later-project");
        viewModel.FileBrowser.ReplaceSelectionRule(first);
        var command = viewModel.RunBackupNowCommand.ExecuteAsync(null);
        try
        {
            await client.SaveEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            viewModel.FileBrowser.ReplaceSelectionRule(second);
        }
        finally
        {
            client.ReleaseSave.TrySetResult();
            await command.WaitAsync(TimeSpan.FromSeconds(10));
        }

        Assert.Equal(first.Path, Assert.Single((await fixture.Reopen().LoadAsync()).SelectionRules).Path);
        Assert.Contains(viewModel.FileBrowser.GetSelectionRules(), rule => rule.Path == second.Path);
        Assert.NotEmpty(viewModel.FileBrowser.PendingChanges);
        Assert.DoesNotContain(FluxVaultIpcCommand.RunBackupNow, client.Commands);
        await viewModel.RefreshAsync();
        Assert.Contains(viewModel.FileBrowser.GetSelectionRules(), rule => rule.Path == second.Path);
    }

    [Fact]
    public async Task Missing_accepted_baseline_prevents_save_and_backup_instead_of_writing_defaults()
    {
        using var fixture = new StoreFixture(true);
        await fixture.Store.SaveAsync(NonDefaultConfiguration(fixture.Root));
        var before = await File.ReadAllBytesAsync(fixture.ConfigPath);
        var client = new StoreClient(fixture.Store);
        var viewModel = CreateViewModel(client);
        viewModel.FileBrowser.ReplaceSelectionRule(Selection(fixture.Root, "unloaded-project"));

        await viewModel.RunBackupNowCommand.ExecuteAsync(null);

        Assert.DoesNotContain(FluxVaultIpcCommand.SaveConfiguration, client.Commands);
        Assert.DoesNotContain(FluxVaultIpcCommand.RunBackupNow, client.Commands);
        Assert.Equal(before, await File.ReadAllBytesAsync(fixture.ConfigPath));
        Assert.NotEmpty(viewModel.FileBrowser.PendingChanges);
        Assert.Contains("load", viewModel.ServiceStatus, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Lost_acknowledgement_after_real_commit_retains_edits_reports_uncertainty_and_prevents_backup()
    {
        using var fixture = new StoreFixture(true);
        await fixture.Store.SaveAsync(NonDefaultConfiguration(fixture.Root));
        var client = new StoreClient(fixture.Store) { Failure = SaveFailure.AcknowledgementLost };
        var viewModel = CreateViewModel(client);
        await viewModel.RefreshAsync();
        var selection = Selection(fixture.Root, "committed-project");
        viewModel.FileBrowser.ReplaceSelectionRule(selection);

        await viewModel.RunBackupNowCommand.ExecuteAsync(null);
        await viewModel.RefreshAsync();

        Assert.Equal(selection.Path, Assert.Single((await fixture.Reopen().LoadAsync()).SelectionRules).Path);
        Assert.NotEmpty(viewModel.FileBrowser.PendingChanges);
        Assert.DoesNotContain(FluxVaultIpcCommand.RunBackupNow, client.Commands);
        Assert.Contains("could not be confirmed", viewModel.ServiceStatus, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("not started", viewModel.ServiceStatus, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("rolled back", viewModel.ServiceStatus, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Saved_configuration_with_failed_purge_keeps_warning_and_stops_backup()
    {
        using var fixture = new StoreFixture(true);
        var existing = Selection(fixture.Root, "removed-project");
        await fixture.Store.SaveAsync(NonDefaultConfiguration(fixture.Root) with
        {
            SelectionRules = [existing], WatchedFolders = ProtectionSelectionCompiler.Compile([existing])
        });
        var client = new StoreClient(fixture.Store) { PurgeFails = true };
        var viewModel = CreateViewModel(client, new AcceptRemoval());
        await viewModel.RefreshAsync();
        Assert.True(viewModel.FileBrowser.RemovePathSelection(existing.Path, true));

        await viewModel.RunBackupNowCommand.ExecuteAsync(null);
        await viewModel.RefreshAsync();

        Assert.Empty((await fixture.Reopen().LoadAsync()).SelectionRules);
        Assert.NotEmpty(viewModel.FileBrowser.PendingChanges);
        Assert.DoesNotContain(FluxVaultIpcCommand.RunBackupNow, client.Commands);
        Assert.Contains("purge failed", viewModel.ServiceStatus, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("not started", viewModel.ServiceStatus, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Unknown_save_acknowledgement_requires_the_original_receipt_before_retry()
    {
        using var fixture = new StoreFixture(true);
        await fixture.Store.SaveAsync(NonDefaultConfiguration(fixture.Root));
        var client = new StoreClient(fixture.Store) { Failure = SaveFailure.AcknowledgementLost };
        var viewModel = CreateViewModel(client);
        await viewModel.RefreshAsync();
        viewModel.FileBrowser.ReplaceSelectionRule(Selection(fixture.Root, "uncertain-project"));
        await viewModel.SaveConfigurationCommand.ExecuteAsync(null);
        client.Failure = SaveFailure.None;

        await viewModel.RunBackupNowCommand.ExecuteAsync(null);

        Assert.Single(client.Requests, request => request.Command == FluxVaultIpcCommand.SaveConfiguration);
        Assert.DoesNotContain(FluxVaultIpcCommand.RunBackupNow, client.Commands);
        Assert.NotEmpty(viewModel.FileBrowser.PendingChanges);
        await viewModel.RefreshAsync();
        await viewModel.RunBackupNowCommand.ExecuteAsync(null);
        Assert.DoesNotContain(FluxVaultIpcCommand.RunBackupNow, client.Commands);
        await viewModel.CheckProtectionSaveOutcomeCommand.ExecuteAsync(null);
        Assert.DoesNotContain(FluxVaultIpcCommand.RunBackupNow, client.Commands);
        await viewModel.RunBackupNowCommand.ExecuteAsync(null);
        Assert.Single(client.Requests, request => request.Command == FluxVaultIpcCommand.RunBackupNow);
        Assert.Empty(viewModel.FileBrowser.PendingChanges);
    }

    [Fact]
    public Task Wpf_save_message_stays_visible_after_refresh_and_clears_after_retry() => RunOnStaAsync(async () =>
    {
        using var fixture = new StoreFixture(true);
        await fixture.Store.SaveAsync(NonDefaultConfiguration(fixture.Root));
        var client = new StoreClient(fixture.Store) { Failure = SaveFailure.Rejected };
        var viewModel = CreateViewModel(client);
        await viewModel.RefreshAsync();
        viewModel.FileBrowser.ReplaceSelectionRule(Selection(fixture.Root, "visible-project"));
        var window = new MainWindow(new FileDataGridLayoutStore(Path.Combine(fixture.Root, "layout.json")))
        {
            DataContext = viewModel, Width = 1400, Height = 900
        };
        try
        {
            viewModel.SelectedWorkspaceIndex = 1;
            window.Measure(new Size(1400, 900));
            window.Arrange(new Rect(0, 0, 1400, 900));
            window.UpdateLayout();
            var text = Assert.IsType<TextBlock>(window.FindName("ProtectionSaveStatusText"));
            await viewModel.SaveConfigurationCommand.ExecuteAsync(null);
            await viewModel.RefreshAsync();
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Assert.Equal(Visibility.Visible, text.Visibility);
            Assert.Contains("save failed", text.Text, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("kept", text.Text, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("not started", text.Text, StringComparison.OrdinalIgnoreCase);
            Assert.NotEmpty(viewModel.FileBrowser.PendingChanges);
            client.Failure = SaveFailure.None;
            await viewModel.SaveConfigurationCommand.ExecuteAsync(null);
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Assert.DoesNotContain("save failed", text.Text, StringComparison.OrdinalIgnoreCase);
            Assert.Empty(viewModel.FileBrowser.PendingChanges);
            Assert.DoesNotContain(FluxVaultIpcCommand.RunBackupNow, client.Commands);
        }
        finally
        {
            window.Close();
        }
    });

    private static async Task RunOnStaAsync(Func<Task> action)
    {
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            dispatcher.BeginInvoke(async () =>
            {
                try { await action(); completed.TrySetResult(); }
                catch (Exception exception) { completed.TrySetException(exception); }
                finally { dispatcher.BeginInvokeShutdown(DispatcherPriority.Background); }
            });
            Dispatcher.Run();
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        try { await completed.Task.WaitAsync(TimeSpan.FromSeconds(20)); }
        finally { Assert.True(thread.Join(TimeSpan.FromSeconds(10)), "Owned WPF test thread did not exit."); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Acknowledged_save_rebases_newer_edits_and_the_next_purge_scopes(bool initiallyProtected)
    {
        using var fixture = new StoreFixture(true);
        var first = Selection(fixture.Root, "first-project");
        var second = Selection(fixture.Root, "second-project");
        var initialRules = initiallyProtected ? new[] { first } : [];
        await fixture.Store.SaveAsync(NonDefaultConfiguration(fixture.Root) with
        { SelectionRules = initialRules, WatchedFolders = ProtectionSelectionCompiler.Compile(initialRules) });
        var client = new StoreClient(fixture.Store) { HoldSave = true };
        var viewModel = CreateViewModel(client, new AcceptRemoval());
        await viewModel.RefreshAsync();
        if (initiallyProtected) viewModel.FileBrowser.RemovePathSelection(first.Path, true);
        else viewModel.FileBrowser.ReplaceSelectionRule(first);
        var command = viewModel.RunBackupNowCommand.ExecuteAsync(null);
        try
        {
            await client.SaveEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            if (initiallyProtected) viewModel.FileBrowser.ReplaceSelectionRule(second);
            else viewModel.FileBrowser.RemovePathSelection(first.Path, true);
        }
        finally { client.ReleaseSave.TrySetResult(); await command.WaitAsync(TimeSpan.FromSeconds(10)); }

        Assert.DoesNotContain(FluxVaultIpcCommand.RunBackupNow, client.Commands);
        Assert.NotEmpty(viewModel.FileBrowser.PendingChanges);
        if (initiallyProtected)
        {
            Assert.DoesNotContain(viewModel.FileBrowser.PendingChanges, change => change.Path == first.Path);
            Assert.Empty(viewModel.FileBrowser.GetRemovedSelectionPurgeScopes());
        }
        else
        {
            Assert.Contains(viewModel.FileBrowser.PendingChanges, change => change.Path == first.Path && change.Change == "Removed");
            Assert.Equal(first.Path, Assert.Single(viewModel.FileBrowser.GetRemovedSelectionPurgeScopes()).SourcePath);
        }
        await viewModel.SaveConfigurationCommand.ExecuteAsync(null);
        var nextSave = client.Requests.Last(request => request.Command == FluxVaultIpcCommand.SaveConfiguration);
        Assert.Equal(!initiallyProtected, nextSave.PurgeRemovedSelections);
        Assert.Empty(viewModel.FileBrowser.PendingChanges);
    }

    [Theory]
    [InlineData(SaveFailure.Timeout)]
    [InlineData(SaveFailure.Cancelled)]
    public async Task Ambiguous_destructive_save_guard_uses_dispatched_scopes_even_if_the_user_readds_them(SaveFailure failure)
    {
        using var fixture = new StoreFixture(true);
        var selection = Selection(fixture.Root, "existing-project");
        await fixture.Store.SaveAsync(NonDefaultConfiguration(fixture.Root) with
        { SelectionRules = [selection], WatchedFolders = ProtectionSelectionCompiler.Compile([selection]) });
        var client = new StoreClient(fixture.Store) { HoldSave = true, Failure = failure };
        var viewModel = CreateViewModel(client, new AcceptRemoval());
        await viewModel.RefreshAsync();
        viewModel.FileBrowser.RemovePathSelection(selection.Path, true);
        var command = viewModel.RunBackupNowCommand.ExecuteAsync(null);
        try
        {
            await client.SaveEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            viewModel.FileBrowser.ReplaceSelectionRule(selection);
        }
        finally { client.ReleaseSave.TrySetResult(); await command.WaitAsync(TimeSpan.FromSeconds(10)); }
        client.Failure = SaveFailure.None;
        await viewModel.RefreshAsync();
        viewModel.FileBrowser.RemovePathSelection(selection.Path, true);

        await viewModel.RunBackupNowCommand.ExecuteAsync(null);

        Assert.Single(client.Requests, request => request.Command == FluxVaultIpcCommand.SaveConfiguration);
        Assert.DoesNotContain(FluxVaultIpcCommand.RunBackupNow, client.Commands);
        Assert.Contains("purge", viewModel.ProtectionSaveMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Explorer_show_versions_preserves_pending_edits_and_save_result(bool whileSaving)
    {
        using var fixture = new StoreFixture(true);
        await fixture.Store.SaveAsync(NonDefaultConfiguration(fixture.Root));
        var client = new StoreClient(fixture.Store) { HoldSave = whileSaving, Failure = SaveFailure.Rejected };
        var viewModel = CreateViewModel(client);
        await viewModel.RefreshAsync();
        var first = Selection(fixture.Root, "first-project");
        var later = Selection(fixture.Root, "later-project");
        viewModel.FileBrowser.ReplaceSelectionRule(first);
        var command = viewModel.RunBackupNowCommand.ExecuteAsync(null);
        try
        {
            if (whileSaving) await client.SaveEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            else await command;
            viewModel.FileBrowser.ReplaceSelectionRule(later);
            await viewModel.ApplyStartupRequestAsync(new AppStartupRequest(AppStartupRequestAction.ShowVersions, later.Path));
            Assert.Contains(viewModel.FileBrowser.GetSelectionRules(), rule => rule.Path == later.Path);
            Assert.NotEmpty(viewModel.FileBrowser.PendingChanges);
            if (!whileSaving) Assert.Contains("save failed", viewModel.ProtectionSaveMessage, StringComparison.OrdinalIgnoreCase);
        }
        finally { client.ReleaseSave.TrySetResult(); await command.WaitAsync(TimeSpan.FromSeconds(10)); }
        Assert.DoesNotContain(FluxVaultIpcCommand.RunBackupNow, client.Commands);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Failed_or_skipped_discard_keeps_draft_and_explains_that_reload_did_not_complete(bool busyRefresh)
    {
        using var fixture = new StoreFixture(true);
        await fixture.Store.SaveAsync(NonDefaultConfiguration(fixture.Root));
        var client = new StoreClient(fixture.Store) { Failure = SaveFailure.Rejected };
        var viewModel = CreateViewModel(client);
        await viewModel.RefreshAsync();
        viewModel.FileBrowser.ReplaceSelectionRule(Selection(fixture.Root, "pending-project"));
        await viewModel.SaveConfigurationCommand.ExecuteAsync(null);
        client.StatusFails = !busyRefresh;
        client.HoldStatus = busyRefresh;
        var refresh = busyRefresh ? viewModel.RefreshAsync() : Task.CompletedTask;
        try
        {
            if (busyRefresh) await client.StatusEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await viewModel.DiscardConfigurationChangesCommand.ExecuteAsync(null);
            Assert.NotEmpty(viewModel.FileBrowser.PendingChanges);
            Assert.Contains("discard", viewModel.ProtectionSaveMessage, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("kept", viewModel.ProtectionSaveMessage, StringComparison.OrdinalIgnoreCase);
        }
        finally { client.ReleaseStatus.TrySetResult(); await refresh.WaitAsync(TimeSpan.FromSeconds(10)); }
        client.StatusFails = false;
        client.HoldStatus = false;
        await viewModel.DiscardConfigurationChangesCommand.ExecuteAsync(null);
        Assert.Empty(viewModel.FileBrowser.PendingChanges);
        Assert.Empty(viewModel.ProtectionSaveMessage);
    }

    [Fact]
    public Task Wpf_options_entry_points_and_handler_are_guarded_during_pending_edits_and_save() => RunOnStaAsync(async () =>
    {
        using var fixture = new StoreFixture(true);
        await fixture.Store.SaveAsync(NonDefaultConfiguration(fixture.Root));
        var client = new StoreClient(fixture.Store) { HoldSave = true };
        var viewModel = CreateViewModel(client);
        await viewModel.RefreshAsync();
        var window = new MainWindow(new FileDataGridLayoutStore(Path.Combine(fixture.Root, "layout.json"))) { DataContext = viewModel };
        try
        {
            var header = Assert.IsType<Button>(window.FindName("OpenOptionsButton"));
            var workspace = Assert.IsType<Button>(window.FindName("OpenOptionsWorkspaceButton"));
            Assert.True(header.IsEnabled);
            Assert.True(workspace.IsEnabled);
            viewModel.FileBrowser.ReplaceSelectionRule(Selection(fixture.Root, "pending-project"));
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Assert.False(header.IsEnabled);
            Assert.False(workspace.IsEnabled);
            Exception? handlerFailure = null;
            DispatcherUnhandledExceptionEventHandler trap = (_, args) => { handlerFailure = args.Exception; args.Handled = true; };
            Dispatcher.CurrentDispatcher.UnhandledException += trap;
            try
            {
                // Routed events can bypass disabled controls: the actual handler must also refuse entry.
                header.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                Assert.Null(handlerFailure);
            }
            finally { Dispatcher.CurrentDispatcher.UnhandledException -= trap; }
            var save = viewModel.SaveConfigurationCommand.ExecuteAsync(null);
            try
            {
                await client.SaveEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                Assert.False(header.IsEnabled);
                Assert.False(workspace.IsEnabled);
            }
            finally { client.ReleaseSave.TrySetResult(); await save.WaitAsync(TimeSpan.FromSeconds(10)); }
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Assert.True(header.IsEnabled);
            Assert.True(workspace.IsEnabled);
        }
        finally { window.Close(); }
    });

    [Fact]
    public async Task New_edits_during_discard_reload_are_retained()
    {
        using var fixture = new StoreFixture(true);
        await fixture.Store.SaveAsync(NonDefaultConfiguration(fixture.Root));
        var client = new StoreClient(fixture.Store);
        var viewModel = CreateViewModel(client);
        await viewModel.RefreshAsync();
        viewModel.FileBrowser.ReplaceSelectionRule(Selection(fixture.Root, "earlier-project"));
        client.HoldStatus = true;
        var discard = viewModel.DiscardConfigurationChangesCommand.ExecuteAsync(null);
        var later = Selection(fixture.Root, "later-project");
        try
        {
            await client.StatusEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            viewModel.FileBrowser.ReplaceSelectionRule(later);
        }
        finally { client.ReleaseStatus.TrySetResult(); await discard.WaitAsync(TimeSpan.FromSeconds(10)); }
        Assert.Contains(viewModel.FileBrowser.GetSelectionRules(), rule => rule.Path == later.Path);
        Assert.NotEmpty(viewModel.FileBrowser.PendingChanges);
        Assert.DoesNotContain(FluxVaultIpcCommand.RunBackupNow, client.Commands);
    }

    [Fact]
    public Task Options_session_prevents_Explorer_save_until_the_dialogue_closes() => RunOnStaAsync(async () =>
    {
        using var fixture = new StoreFixture(true);
        await fixture.Store.SaveAsync(NonDefaultConfiguration(fixture.Root));
        var client = new StoreClient(fixture.Store);
        var viewModel = CreateViewModel(client);
        await viewModel.RefreshAsync();
        var window = new MainWindow(new FileDataGridLayoutStore(Path.Combine(fixture.Root, "layout.json"))) { DataContext = viewModel };
        var forwarded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var saveWasAvailable = false;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(10) };
        timer.Tick += async (_, _) =>
        {
            var options = window.OwnedWindows.OfType<OptionsWindow>().SingleOrDefault();
            if (options is null) return;
            timer.Stop();
            try
            {
                saveWasAvailable = viewModel.RunBackupNowCommand.CanExecute(null);
                await viewModel.ApplyStartupRequestAsync(new AppStartupRequest(AppStartupRequestAction.AddToFluxVault,
                    Path.Combine(fixture.Root, "external-project")));
                forwarded.TrySetResult();
            }
            catch (Exception exception) { forwarded.TrySetException(exception); }
            finally { options.Close(); }
        };
        try
        {
            window.Show();
            var button = Assert.IsType<Button>(window.FindName("OpenOptionsButton"));
            timer.Start();
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await forwarded.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.False(saveWasAvailable);
            Assert.DoesNotContain(FluxVaultIpcCommand.SaveConfiguration, client.Commands);
            Assert.Empty(viewModel.FileBrowser.GetSelectionRules());
        }
        finally { timer.Stop(); foreach (Window owned in window.OwnedWindows) owned.Close(); window.Close(); }
    });

    [Fact]
    public Task Options_window_keeps_an_in_flight_save_owned_until_acknowledgement() => RunOnStaAsync(async () =>
    {
        using var fixture = new StoreFixture(true);
        await fixture.Store.SaveAsync(NonDefaultConfiguration(fixture.Root));
        var client = new StoreClient(fixture.Store) { HoldSave = true };
        var viewModel = new OptionsViewModel(client);
        var window = new OptionsWindow(viewModel);
        Task? save = null;
        try
        {
            window.Show();
            await viewModel.InitialiseAsync();
            save = viewModel.SaveCommand.ExecuteAsync(null);
            await client.SaveEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            window.Close();
            Assert.True(window.IsVisible);
            Assert.Contains("wait", viewModel.StatusText, StringComparison.OrdinalIgnoreCase);
            client.ReleaseSave.TrySetResult();
            await save.WaitAsync(TimeSpan.FromSeconds(10));
            window.Close();
            Assert.False(window.IsVisible);
        }
        finally { client.ReleaseSave.TrySetResult(); if (save is not null) await save; window.Close(); }
    });

    private static ProtectionSelectionRule Selection(string root, string name) => new(
        name, Path.Combine(root, name), ProtectionSelectionMode.RecursiveFolder,
        CompressionPreference.Zstd, ResourceProfile.Balanced, IsEnabled: true);

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public Task Options_close_cannot_enable_protection_saves_with_a_stale_baseline(bool delayedReload, bool editDuringReload) => RunOnStaAsync(async () =>
    {
        using var fixture = new StoreFixture(true);
        await fixture.Store.SaveAsync(NonDefaultConfiguration(fixture.Root));
        var client = new StoreClient(fixture.Store);
        var viewModel = CreateViewModel(client);
        await viewModel.RefreshAsync();
        var window = new MainWindow(new FileDataGridLayoutStore(Path.Combine(fixture.Root, "layout.json"))) { DataContext = viewModel };
        var closedOptions = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(10) };
        timer.Tick += async (_, _) =>
        {
            var options = window.OwnedWindows.OfType<OptionsWindow>().SingleOrDefault();
            if (options?.DataContext is not OptionsViewModel optionsViewModel || optionsViewModel.StatusText != "Options loaded.") return;
            timer.Stop();
            try
            {
                optionsViewModel.MinimumVersionsPerFile = 17;
                await optionsViewModel.SaveCommand.ExecuteAsync(null);
                Assert.Equal(17, (await fixture.Reopen().LoadAsync()).RetentionPolicy.MinimumVersionsPerFile);
                client.HoldStatus = delayedReload;
                client.StatusFails = !delayedReload;
                options.Close();
                closedOptions.TrySetResult();
            }
            catch (Exception exception) { closedOptions.TrySetException(exception); options.Close(); }
        };
        try
        {
            window.Show();
            timer.Start();
            Assert.IsType<Button>(window.FindName("OpenOptionsButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await closedOptions.Task.WaitAsync(TimeSpan.FromSeconds(10));
            if (delayedReload) await client.StatusEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.False(viewModel.RunBackupNowCommand.CanExecute(null));
            Assert.False(viewModel.CanOpenOptions);
            var selection = Selection(fixture.Root, "after-options-project");
            if (editDuringReload) viewModel.FileBrowser.ReplaceSelectionRule(selection);
            await viewModel.RunBackupNowCommand.ExecuteAsync(null);
            Assert.Single(client.Requests, request => request.Command == FluxVaultIpcCommand.SaveConfiguration);
            Assert.DoesNotContain(FluxVaultIpcCommand.RunBackupNow, client.Commands);
            Assert.Equal(17, (await fixture.Reopen().LoadAsync()).RetentionPolicy.MinimumVersionsPerFile);
            client.ReleaseStatus.TrySetResult();
            await WaitForDiscardAsync();
            Assert.Contains("reload", viewModel.ProtectionSaveMessage, StringComparison.OrdinalIgnoreCase);
            client.StatusFails = false;
            if (editDuringReload)
            {
                Assert.NotEmpty(viewModel.FileBrowser.PendingChanges);
                await viewModel.DiscardConfigurationChangesCommand.ExecuteAsync(null);
            }
            else
            {
                await viewModel.RefreshAsync();
                Assert.DoesNotContain("unavailable", viewModel.ProtectionSaveMessage);
            }
            Assert.True(viewModel.CanSaveProtection);
            viewModel.FileBrowser.ReplaceSelectionRule(selection);
            await viewModel.SaveConfigurationCommand.ExecuteAsync(null);
            Assert.Equal(17, (await fixture.Reopen().LoadAsync()).RetentionPolicy.MinimumVersionsPerFile);
            Assert.Equal(selection.Path, Assert.Single((await fixture.Reopen().LoadAsync()).SelectionRules).Path);
        }
        finally
        {
            client.ReleaseStatus.TrySetResult(); timer.Stop();
            foreach (Window owned in window.OwnedWindows) owned.Close();
            window.Close();
        }

        async Task WaitForDiscardAsync()
        {
            async Task WaitAsync()
            {
                while (!viewModel.DiscardConfigurationChangesCommand.CanExecute(null)) await Task.Delay(10);
            }
            await WaitAsync().WaitAsync(TimeSpan.FromSeconds(10));
        }
    });

    private static FluxVaultConfiguration NonDefaultConfiguration(string root)
    {
        var defaults = FluxVaultConfiguration.CreateDefault(root);
        var device = new DeviceIdentityConfiguration("studio-device", "Studio PC", new DateTimeOffset(2026, 10, 1, 8, 0, 0, TimeSpan.Zero));
        return defaults with
        {
            IsEnabled = false,
            MetadataStore = defaults.MetadataStore with
            {
                Host = "127.0.0.1", Port = 55432, DatabaseName = "preserve_this_database",
                Username = "fixture_owner", ServiceName = "fixture-service", BackupRetentionDays = 47,
                MaxCaptureWorkers = 2, MaxDbWriterConcurrency = 3, ExportLagWarningThreshold = TimeSpan.FromMinutes(7)
            },
            RepositoryMaintenancePolicy = new RepositoryMaintenancePolicy(false, TimeSpan.FromHours(61), false, 7, true),
            Sync = new SyncConfiguration(device,
            [
                TrustedDeviceConfiguration.FromLocalDevice(device),
                new TrustedDeviceConfiguration("other-device", "Other PC", DeviceTrustState.Blocked, device.CreatedAtUtc)
            ]),
            DiagnosticsPolicy = new DiagnosticsPolicy(false, DiagnosticLogLevel.Error, Path.Combine(root, "custom-logs"),
                17, 3, TimeSpan.FromSeconds(11), 701),
            RetentionPolicy = defaults.RetentionPolicy with { IsEnabled = false }
        };
    }

    private static MainWindowViewModel CreateViewModel(StoreClient client, IProtectionRemovalConfirmation? confirmation = null, IBackupOperationStore? backupStore = null,
        IConfigurationSaveOperationStore? saveStore = null) => new(
        client, TimeSpan.FromHours(1), new FileBrowserViewModel(new WindowsFileBrowserFileSystem()),
        new FixtureServiceController(), new UnusedDestinationPicker(), new UnusedOverwriteConfirmation(),
        protectionRemovalConfirmation: confirmation, backupOperationStore: backupStore, saveOperationStore: saveStore);

    public enum SaveFailure { None, Rejected, Io, Timeout, Denied, Cancelled, AcknowledgementLost }

    private sealed class StoreClient(IFluxVaultConfigurationStore store) : IFluxVaultServiceClient
    {
        internal VaultId Identity { get; } = VaultId.New();
        private long revision = 1;
        internal EnvelopeFault SaveEnvelopeFault { get; init; }
        public SaveFailure Failure { get; set; }
        public SaveFailure BackupFailure { get; init; }
        public bool BackupCompletedUnsuccessfully { get; init; }
        public bool WrongPreviewOutput { get; init; }
        public SaveFailure ReceiptFailure { get; init; }
        public long? ReceiptRevisionOverride { get; set; }
        public FluxVaultIpcErrorCode? ReceiptErrorCode { get; init; }
        public Action<FluxVaultIpcRequest>? BackupDispatchCheck { get; set; }
        public Action<FluxVaultIpcRequest>? SaveDispatchCheck { get; set; }
        public Func<FluxVaultIpcRequest, CancellationToken, Task<FluxVaultIpcResponse>>? SaveDispatchHandler { get; init; }
        public Action<FluxVaultIpcRequest>? ReceiptDispatchCheck { get; set; }
        public bool RequireBoundRequests { get; init; }
        public bool HoldSave { get; init; }
        public bool PurgeFails { get; init; }
        public FluxVaultIpcResponse? ReceiptResponseOverride { get; init; }
        public bool StatusFails { get; set; }
        public bool StatusCancelled { get; set; }
        public bool HoldStatus { get; set; }
        public bool PerformanceThrows { get; set; }
        public TaskCompletionSource PerformanceRequested { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource StatusEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseStatus { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource SaveEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseSave { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public List<FluxVaultIpcCommand> Commands { get; } = [];
        public List<FluxVaultIpcRequest> Requests { get; } = [];
        public List<string> Events { get; } = [];
        public FluxVaultConfiguration? ConfigurationAtBackup { get; private set; }

        public async Task<FluxVaultIpcResponse> SendAsync(FluxVaultIpcRequest request, CancellationToken cancellationToken = default)
        {
            Commands.Add(request.Command);
            Requests.Add(request);
            switch (request.Command)
            {
                case FluxVaultIpcCommand.GetPerformance:
                    PerformanceRequested.TrySetResult();
                    if (PerformanceThrows) throw new IOException("Fixture telemetry disconnected");
                    return Envelope(FluxVaultIpcResponse.Failure("Fixture telemetry unavailable") with { ErrorCode = FluxVaultIpcErrorCode.Unavailable });
                case FluxVaultIpcCommand.GetStatus:
                    if (HoldStatus) { StatusEntered.TrySetResult(); await ReleaseStatus.Task.WaitAsync(cancellationToken); }
                    if (StatusCancelled) throw new OperationCanceledException("Fixture status reload cancelled");
                    if (StatusFails) return FluxVaultIpcResponse.Failure("Fixture status unavailable");
                    return Envelope(FluxVaultIpcResponse.WithStatus(new FluxVaultServiceStatus(true, await store.LoadAsync(cancellationToken),
                        "Fixture idle", null, [], [])));
                case FluxVaultIpcCommand.SaveConfiguration:
                    SaveDispatchCheck?.Invoke(request);
                    if (SaveDispatchHandler is not null) return await SaveDispatchHandler(request, cancellationToken);
                    if (RequireBoundRequests && (request.VaultId != Identity || request.ExpectedVaultRevision != revision || request.OperationId is null))
                        return FluxVaultIpcResponse.Failure("The accepted identity/revision is missing or stale") with { ErrorCode = FluxVaultIpcErrorCode.StaleRevision };
                    Events.Add("Save started");
                    SaveEntered.TrySetResult();
                    if (HoldSave) await ReleaseSave.Task.WaitAsync(cancellationToken);
                    switch (Failure)
                    {
                        case SaveFailure.Rejected: return FluxVaultIpcResponse.Failure("Fixture save rejected") with { ErrorCode = FluxVaultIpcErrorCode.InvalidRequest };
                        case SaveFailure.Io: throw new IOException("Fixture save IO failure");
                        case SaveFailure.Timeout: throw new TimeoutException("Fixture save acknowledgement timed out");
                        case SaveFailure.Denied: throw new UnauthorizedAccessException("Fixture save denied");
                        case SaveFailure.Cancelled: throw new OperationCanceledException("Fixture save cancelled");
                    }
                    await store.SaveAsync(request.Configuration ?? throw new InvalidOperationException("Missing configuration"), cancellationToken);
                    revision++;
                    Events.Add("Save persisted");
                    if (Failure == SaveFailure.AcknowledgementLost) throw new TimeoutException("Fixture lost acknowledgement after persistence");
                    return Envelope(PurgeFails
                        ? FluxVaultIpcResponse.WithPurge(new RepositoryPurgeResult(0, 0, 0, [], Success: false, ErrorMessage: "Fixture purge denied"))
                        : FluxVaultIpcResponse.Ok());
                case FluxVaultIpcCommand.RunBackupNow:
                    BackupDispatchCheck?.Invoke(request);
                    ConfigurationAtBackup = await store.LoadAsync(cancellationToken);
                    Events.Add("Backup requested");
                    if (BackupFailure == SaveFailure.Io) throw new IOException("Disconnected after backup execution");
                    if (BackupFailure == SaveFailure.Timeout) throw new TimeoutException("Lost backup acknowledgement");
                    if (BackupFailure == SaveFailure.Cancelled) throw new OperationCanceledException("Backup acknowledgement cancelled");
                    if (BackupFailure == SaveFailure.Denied) throw new UnauthorizedAccessException("Pipe server identity was refused");
                    return Envelope(CompletedBackupResponse());
                case FluxVaultIpcCommand.ListVersions:
                    return Envelope(FluxVaultIpcResponse.WithVersions([]));
                case FluxVaultIpcCommand.RestoreVersionPreview:
                    if (request.OutputPath is null) return Envelope(FluxVaultIpcResponse.Failure("Missing caller preview destination"));
                    File.WriteAllText(request.OutputPath, "verified preview bytes");
                    return Envelope(FluxVaultIpcResponse.WithRestore(new RepositoryRestoreResult(
                        WrongPreviewOutput ? request.OutputPath + ".unexpected" : request.OutputPath, 22, 1, ["Fixture permissions warning"])));
                case FluxVaultIpcCommand.GetOperationStatus:
                    ReceiptDispatchCheck?.Invoke(request);
                    if (ReceiptFailure == SaveFailure.Io) throw new IOException("Receipt disconnected");
                    if (ReceiptFailure == SaveFailure.Timeout) throw new TimeoutException("Receipt timed out");
                    if (ReceiptFailure == SaveFailure.Cancelled) throw new OperationCanceledException("Receipt cancelled");
                    if (ReceiptFailure == SaveFailure.Denied) throw new UnauthorizedAccessException("Pipe identity refused");
                    var receiptResponse = Envelope(ReceiptResponseOverride ?? (ReceiptErrorCode is { } error
                        ? FluxVaultIpcResponse.Failure("The receipt is not available") with { ErrorCode = error }
                        : CompletedBackupResponse()));
                    return ReceiptRevisionOverride is { } receiptRevision ? receiptResponse with { VaultRevision = receiptRevision } : receiptResponse;
                default: throw new InvalidOperationException($"Unexpected fixture command: {request.Command}");
            }
            FluxVaultIpcResponse CompletedBackupResponse() => BackupCompletedUnsuccessfully
                ? FluxVaultIpcResponse.WithBackup(new BackupRunSummary(false, "Fixture capture unavailable", 0, 1, DateTimeOffset.UtcNow)) with
                    { Success = false, ErrorCode = FluxVaultIpcErrorCode.Unavailable, ErrorMessage = "Fixture capture unavailable" }
                : FluxVaultIpcResponse.Ok();
            FluxVaultIpcResponse Envelope(FluxVaultIpcResponse response)
            {
                var result = response with { VaultId = Identity, VaultRevision = revision, OperationId = request.OperationId };
                return request.Command != FluxVaultIpcCommand.SaveConfiguration ? result : SaveEnvelopeFault switch
                {
                    EnvelopeFault.WrongIdentity => result with { VaultId = VaultId.New() },
                    EnvelopeFault.MissingOperation => result with { OperationId = null },
                    EnvelopeFault.WrongRevision => result with { VaultRevision = revision - 1 },
                    _ => result
                };
            }
        }
    }

    private sealed class StoreFixture : IDisposable
    {
        private readonly bool profileStore;
        public string Root { get; } = Path.Combine(Path.GetTempPath(), $"FluxVault.ProtectionSave.{Guid.NewGuid():N}");
        public string ConfigPath => Path.Combine(Root, "config.json");
        public IFluxVaultConfigurationStore Store { get; }
        public StoreFixture(bool profileStore)
        {
            this.profileStore = profileStore;
            Directory.CreateDirectory(Root);
            Store = Reopen();
        }
        public IFluxVaultConfigurationStore Reopen() => profileStore
            ? new FluxVaultProfileConfigurationStore(new FileFluxVaultProfileSetStore(ConfigPath, Root), "default")
            : new FileFluxVaultConfigurationStore(ConfigPath, Root);
        public void Dispose() => Directory.Delete(Root, recursive: true);
    }

    private sealed class CancelRemoval : IProtectionRemovalConfirmation
    {
        public int Calls { get; private set; }
        public bool ConfirmPurge(IReadOnlyList<RepositoryPurgeScope> scopes) { Calls++; Assert.NotEmpty(scopes); return false; }
    }
    private sealed class AcceptRemoval : IProtectionRemovalConfirmation
    {
        public bool ConfirmPurge(IReadOnlyList<RepositoryPurgeScope> scopes) => true;
    }
    private sealed class UnusedDestinationPicker : IRestoreDestinationPicker
    {
        public string? PickDestination(VersionRow version) => throw new InvalidOperationException("Unexpected restore dialogue");
    }
    private sealed class PreviewLauncher : IVersionPreviewLauncher
    {
        public List<string> Opened { get; } = [];
        public Exception? Failure { get; init; }
        public void OpenFile(string filePath)
        {
            if (Failure is not null) throw Failure;
            Opened.Add(filePath);
        }
    }
    private sealed class UnusedOverwriteConfirmation : IRestoreOverwriteConfirmation
    {
        public bool ConfirmOverwrite(string destinationPath) => throw new InvalidOperationException("Unexpected overwrite dialogue");
    }
    private sealed class FixtureServiceController : IFluxVaultWindowsServiceController
    {
        public Task<FluxVaultWindowsServiceStatus> GetStatusAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new FluxVaultWindowsServiceStatus("Fixture only", FluxVaultWindowsServiceState.Running, "Fixture running"));
        public Task<FluxVaultWindowsServiceActionResult> StartAsync(CancellationToken cancellationToken = default) => throw new InvalidOperationException("No live service access");
        public Task<FluxVaultWindowsServiceActionResult> StopAsync(CancellationToken cancellationToken = default) => throw new InvalidOperationException("No live service access");
    }
}
