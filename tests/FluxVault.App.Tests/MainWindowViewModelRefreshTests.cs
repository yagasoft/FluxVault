using System.IO;
using FluxVault.Abstractions.ChangeTracking;
using FluxVault.Abstractions.Configuration;
using FluxVault.Abstractions.Ipc;
using FluxVault.Abstractions.Policies;
using FluxVault.Abstractions.Storage;
using FluxVault.App.Services;
using FluxVault.App.ViewModels;
using FluxVault.Core.Ipc;

namespace FluxVault.App.Tests;

public sealed class MainWindowViewModelRefreshTests
{
    [Fact]
    public async Task Refresh_populates_versions_from_service_status()
    {
        var client = new FakeFluxVaultServiceClient(StatusWithVersions("v1"));
        var viewModel = new MainWindowViewModel(client, TimeSpan.FromMilliseconds(20));

        await viewModel.RefreshAsync();

        var version = Assert.Single(viewModel.RecentVersions);
        Assert.Equal("v1", version.VersionId);
        Assert.Contains("Last refreshed", viewModel.ServiceStatus);
    }

    [Fact]
    public async Task Refresh_populates_compact_lineage_text_for_version_rows()
    {
        var client = new FakeFluxVaultServiceClient(StatusWithVersionSummaries(
            new RepositoryVersionSummary(
                VersionId: "restore-version",
                SourcePath: @"D:\Work\file.txt",
                CapturedAtUtc: DateTimeOffset.UtcNow,
                Consistency: CaptureConsistency.BestEffort,
                LogicalLength: 128,
                ChunkCount: 1,
                OperationType: VersionOperationType.Restore,
                ParentVersionIds: [],
                RestoredFromVersionId: "source-version",
                ForkOriginVersionId: "source-version",
                InheritedFromVersionId: null,
                InheritedFromSourcePath: null,
                ContentSignature: "sig-v1"),
            new RepositoryVersionSummary(
                VersionId: "copy-version",
                SourcePath: @"D:\Work\copy.txt",
                CapturedAtUtc: DateTimeOffset.UtcNow,
                Consistency: CaptureConsistency.BestEffort,
                LogicalLength: 128,
                ChunkCount: 1,
                OperationType: VersionOperationType.InheritedCopy,
                ParentVersionIds: ["source-version"],
                RestoredFromVersionId: null,
                ForkOriginVersionId: "source-version",
                InheritedFromVersionId: "source-version",
                InheritedFromSourcePath: @"D:\Work\file.txt",
                ContentSignature: "sig-v1")));
        var viewModel = new MainWindowViewModel(client, TimeSpan.FromMilliseconds(20));

        await viewModel.RefreshAsync();

        Assert.Contains(viewModel.RecentVersions, version => version.VersionId == "restore-version"
            && version.Lineage == "Restored from source-version");
        Assert.Contains(viewModel.RecentVersions, version => version.VersionId == "copy-version"
            && version.Lineage == "Inherited from source-version");
    }

    [Fact]
    public async Task Auto_refresh_repeats_status_requests_without_manual_backup()
    {
        var client = new FakeFluxVaultServiceClient(StatusWithVersions("v1"));
        var viewModel = new MainWindowViewModel(client, TimeSpan.FromMilliseconds(20));

        viewModel.StartAutoRefresh();
        await WaitUntilAsync(() => client.GetStatusCount >= 2);
        viewModel.StopAutoRefresh();

        Assert.True(client.GetStatusCount >= 2);
        Assert.DoesNotContain(FluxVaultIpcCommand.RunBackupNow, client.Commands);
    }

    [Fact]
    public async Task Concurrent_refresh_requests_share_the_in_flight_request()
    {
        var client = new BlockingFluxVaultServiceClient(StatusWithVersions("v1"));
        var viewModel = new MainWindowViewModel(client, TimeSpan.FromMilliseconds(20));

        var first = viewModel.RefreshAsync();
        var second = viewModel.RefreshAsync();
        await WaitUntilAsync(() => client.GetStatusCount == 1);
        client.Release();
        await Task.WhenAll(first, second);

        Assert.Equal(1, client.GetStatusCount);
    }

    [Fact]
    public async Task Refresh_preserves_selected_version_when_version_still_exists()
    {
        var client = new FakeFluxVaultServiceClient(
            StatusWithVersions("v1"),
            StatusWithVersions("v2", "v1"));
        var viewModel = new MainWindowViewModel(client, TimeSpan.FromMilliseconds(20));
        await viewModel.RefreshAsync();
        viewModel.SelectedVersion = viewModel.RecentVersions.Single();

        await viewModel.RefreshAsync();

        Assert.NotNull(viewModel.SelectedVersion);
        Assert.Equal("v1", viewModel.SelectedVersion.VersionId);
    }

    [Fact]
    public async Task Background_refresh_does_not_overwrite_dirty_configuration_fields()
    {
        var client = new FakeFluxVaultServiceClient(
            StatusWithRepository(@"D:\Vault\Original"),
            StatusWithRepository(@"D:\Vault\FromService"));
        var viewModel = new MainWindowViewModel(client, TimeSpan.FromMilliseconds(20));
        await viewModel.RefreshAsync();

        viewModel.RepositoryPath = @"D:\Vault\UserTyping";
        viewModel.StartAutoRefresh();
        await Task.Delay(80);
        viewModel.StopAutoRefresh();

        Assert.Equal(@"D:\Vault\UserTyping", viewModel.RepositoryPath);
    }

    [Fact]
    public async Task Refresh_populates_mirrors_workspace_from_service_status()
    {
        var status = StatusWithVersions() with
        {
            Configuration = FluxVaultConfiguration.CreateDefault(@"D:\Vault") with
            {
                MirrorSet = new MirrorSetConfiguration(
                [
                    new MirrorNodeConfiguration("cloud", "Cloud copy", @"D:\Mirrors\Cloud", IsEnabled: true),
                    new MirrorNodeConfiguration("usb", "USB shelf copy", @"E:\FluxVault", IsEnabled: false)
                ])
            },
            MirrorWarnings = ["Offline mirror D:\\Mirrors\\Cloud is unavailable."]
        };
        var client = new FakeFluxVaultServiceClient(status);
        var viewModel = new MainWindowViewModel(client, TimeSpan.FromMilliseconds(20));

        await viewModel.RefreshAsync();
        viewModel.OpenMirrorsWorkspaceCommand.Execute(null);

        Assert.Equal(3, viewModel.SelectedWorkspaceIndex);
        Assert.Equal(2, viewModel.MirrorNodes.Count);
        Assert.Equal("Cloud copy", viewModel.MirrorNodes[0].Label);
        Assert.Equal(@"D:\Mirrors\Cloud", viewModel.MirrorNodes[0].Path);
        Assert.True(viewModel.MirrorNodes[0].IsEnabled);
        Assert.Contains("1 of 2", viewModel.MirrorSummary);
        Assert.Contains("warning", viewModel.MirrorHealth, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Refresh_populates_mirror_repair_status_in_diagnostics_and_mirrors()
    {
        var mirrorRepair = MirrorRepairReport(
            isPreview: false,
            requestedMirrorNodeId: "cloud",
            nodeId: "cloud",
            nodeLabel: "Cloud copy",
            nodePath: @"D:\Mirrors\Cloud",
            healthState: RepositoryHealthState.Warning,
            repaired: 1);
        var status = StatusWithVersions() with
        {
            Configuration = FluxVaultConfiguration.CreateDefault(@"D:\Vault") with
            {
                MirrorSet = new MirrorSetConfiguration(
                [
                    new MirrorNodeConfiguration("cloud", "Cloud copy", @"D:\Mirrors\Cloud", IsEnabled: true)
                ])
            },
            RepositoryHealth = HealthSnapshot(
                RepositoryHealthState.Warning,
                "Mirror repair warning.",
                scrub: null,
                rehearsal: null,
                mirrorRepair)
        };
        var client = new FakeFluxVaultServiceClient(status);
        var viewModel = new MainWindowViewModel(client, TimeSpan.FromMilliseconds(20));

        await viewModel.RefreshAsync();

        var mirror = Assert.Single(viewModel.MirrorNodes);
        Assert.Contains("Warning", mirror.RepairStatus);
        Assert.Contains("repaired 1", mirror.RepairDetail, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(viewModel.RepositoryHealthRows,
            row => row.Name == "Mirror repair" && row.Status.Contains("Warning", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Mirror_repair_commands_send_selected_and_all_requests()
    {
        var status = StatusWithVersions() with
        {
            Configuration = FluxVaultConfiguration.CreateDefault(@"D:\Vault") with
            {
                MirrorSet = new MirrorSetConfiguration(
                [
                    new MirrorNodeConfiguration("cloud", "Cloud copy", @"D:\Mirrors\Cloud", IsEnabled: true)
                ])
            }
        };
        var client = new FakeFluxVaultServiceClient(status)
        {
            MirrorRepairResponse = FluxVaultIpcResponse.WithMirrorRepair(MirrorRepairReport(
                isPreview: false,
                requestedMirrorNodeId: "cloud",
                nodeId: "cloud",
                nodeLabel: "Cloud copy",
                nodePath: @"D:\Mirrors\Cloud",
                healthState: RepositoryHealthState.Healthy,
                repaired: 1))
        };
        var viewModel = new MainWindowViewModel(client, TimeSpan.FromMilliseconds(20));
        await viewModel.RefreshAsync();
        viewModel.SelectedMirrorNode = Assert.Single(viewModel.MirrorNodes);

        await viewModel.PreviewSelectedMirrorRepairCommand.ExecuteAsync(null);
        await viewModel.RunSelectedMirrorRepairCommand.ExecuteAsync(null);
        await viewModel.PreviewMirrorRepairCommand.ExecuteAsync(null);
        await viewModel.RunMirrorRepairCommand.ExecuteAsync(null);

        Assert.Contains((FluxVaultIpcCommand.PreviewMirrorRepair, "cloud"), client.MirrorRepairRequests);
        Assert.Contains((FluxVaultIpcCommand.RunMirrorRepair, "cloud"), client.MirrorRepairRequests);
        Assert.Contains((FluxVaultIpcCommand.PreviewMirrorRepair, null), client.MirrorRepairRequests);
        Assert.Contains((FluxVaultIpcCommand.RunMirrorRepair, null), client.MirrorRepairRequests);
        Assert.Contains("Mirror repair completed", viewModel.RepositoryHealthStatus);
    }

    [Fact]
    public async Task Save_configuration_persists_mirror_set_nodes_and_clears_legacy_mirror_path()
    {
        var client = new FakeFluxVaultServiceClient(StatusWithVersions());
        var viewModel = new MainWindowViewModel(client, TimeSpan.FromMilliseconds(20));
        await viewModel.RefreshAsync();

        viewModel.AddMirrorCommand.Execute(null);
        var mirror = Assert.Single(viewModel.MirrorNodes);
        mirror.Id = "cloud";
        mirror.Label = "Cloud copy";
        mirror.Path = @"D:\Mirrors\Cloud";
        mirror.IsEnabled = true;
        await viewModel.SaveConfigurationCommand.ExecuteAsync(null);

        var saved = Assert.Single(client.SavedConfigurations);
        Assert.Null(saved.MirrorPath);
        var node = Assert.Single(saved.MirrorSet.Nodes);
        Assert.Equal("cloud", node.Id);
        Assert.Equal("Cloud copy", node.Label);
        Assert.Equal(@"D:\Mirrors\Cloud", node.Path);
        Assert.True(node.IsEnabled);
    }

    [Fact]
    public async Task Manual_refresh_does_not_overwrite_dirty_file_browser_changes()
    {
        var client = new FakeFluxVaultServiceClient(
            StatusWithSelectionRules([]),
            StatusWithSelectionRules([]));
        var viewModel = new MainWindowViewModel(client, TimeSpan.FromMilliseconds(20));
        await viewModel.RefreshAsync();

        viewModel.FileBrowser.ReplaceSelectionRule(new ProtectionSelectionRule(
            "draft",
            Path.GetFullPath(@"D:\Work\draft.txt"),
            ProtectionSelectionMode.File,
            CompressionPreference.Zstd,
            ResourceProfile.Balanced,
            IsEnabled: true));
        await viewModel.RefreshAsync();

        Assert.Single(viewModel.FileBrowser.GetSelectionRules());
        Assert.Single(viewModel.FileBrowser.PendingChanges);
    }

    [Fact]
    public async Task Discard_configuration_changes_reloads_service_configuration()
    {
        var client = new FakeFluxVaultServiceClient(
            StatusWithSelectionRules([]),
            StatusWithSelectionRules(
                [
                    new ProtectionSelectionRule(
                        "service",
                        Path.GetFullPath(@"D:\Service"),
                        ProtectionSelectionMode.RecursiveFolder,
                        CompressionPreference.Zstd,
                        ResourceProfile.Balanced,
                        IsEnabled: true)
                ]));
        var viewModel = new MainWindowViewModel(client, TimeSpan.FromMilliseconds(20));
        await viewModel.RefreshAsync();
        viewModel.FileBrowser.ReplaceSelectionRule(new ProtectionSelectionRule(
            "local",
            Path.GetFullPath(@"D:\Local"),
            ProtectionSelectionMode.File,
            CompressionPreference.Zstd,
            ResourceProfile.Balanced,
            IsEnabled: true));

        await viewModel.DiscardConfigurationChangesCommand.ExecuteAsync(null);

        var rule = Assert.Single(viewModel.FileBrowser.GetSelectionRules());
        Assert.Equal(Path.GetFullPath(@"D:\Service"), rule.Path);
        Assert.Empty(viewModel.FileBrowser.PendingChanges);
    }

    [Fact]
    public async Task Service_unavailable_keeps_existing_versions_visible()
    {
        var client = new FakeFluxVaultServiceClient(StatusWithVersions("v1"));
        var viewModel = new MainWindowViewModel(client, TimeSpan.FromMilliseconds(20));
        await viewModel.RefreshAsync();
        client.ThrowOnNextRequest(new IOException("pipe unavailable"));

        await viewModel.RefreshAsync();

        Assert.Single(viewModel.RecentVersions);
        Assert.Contains("unavailable", viewModel.ServiceStatus);
    }

    [Fact]
    public async Task Stopped_windows_service_shows_warning_and_skips_ipc_status_request()
    {
        var client = new FakeFluxVaultServiceClient(StatusWithVersions("v1"));
        var serviceController = new FakeWindowsServiceController(ServiceStatus(FluxVaultWindowsServiceState.Stopped, "FluxVault service is stopped."));
        var viewModel = new MainWindowViewModel(
            client,
            TimeSpan.FromMilliseconds(20),
            new FileBrowserViewModel(new EmptyFileBrowserFileSystem()),
            serviceController);

        await viewModel.RefreshAsync();

        Assert.True(viewModel.IsServiceWarningVisible);
        Assert.Contains("stopped", viewModel.ServiceWarningText, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("Start service", viewModel.ServiceControlActionLabel);
        Assert.True(viewModel.IsServiceControlActionEnabled);
        Assert.Equal(0, client.GetStatusCount);
    }

    [Fact]
    public async Task Running_windows_service_with_ipc_failure_reports_unavailable_without_throwing()
    {
        var client = new FakeFluxVaultServiceClient(StatusWithVersions("v1"));
        client.ThrowOnNextRequest(new InvalidOperationException("pipe startup failed"));
        var serviceController = new FakeWindowsServiceController(ServiceStatus(FluxVaultWindowsServiceState.Running, "FluxVault service is running."));
        var viewModel = new MainWindowViewModel(
            client,
            TimeSpan.FromMilliseconds(20),
            new FileBrowserViewModel(new EmptyFileBrowserFileSystem()),
            serviceController);

        await viewModel.RefreshAsync();

        Assert.True(viewModel.IsServiceWarningVisible);
        Assert.Contains("dashboard cannot connect", viewModel.ServiceWarningText, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("unavailable", viewModel.ServiceStatus, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("Stop service", viewModel.ServiceControlActionLabel);
    }

    [Fact]
    public async Task Toggle_windows_service_starts_stopped_service_and_refreshes_status()
    {
        var client = new FakeFluxVaultServiceClient(StatusWithVersions("v1"));
        var serviceController = new FakeWindowsServiceController(ServiceStatus(FluxVaultWindowsServiceState.Stopped, "FluxVault service is stopped."))
        {
            StartResult = ActionResult(FluxVaultWindowsServiceState.Running, "FluxVault service started.")
        };
        var viewModel = new MainWindowViewModel(
            client,
            TimeSpan.FromMilliseconds(20),
            new FileBrowserViewModel(new EmptyFileBrowserFileSystem()),
            serviceController);
        await viewModel.RefreshAsync();

        await viewModel.ToggleWindowsServiceCommand.ExecuteAsync(null);

        Assert.Equal(1, serviceController.StartCount);
        Assert.Equal("Stop service", viewModel.ServiceControlActionLabel);
        Assert.False(viewModel.IsServiceWarningVisible);
    }

    [Fact]
    public async Task Toggle_windows_service_reports_elevation_required_when_service_control_is_denied()
    {
        var client = new FakeFluxVaultServiceClient(StatusWithVersions("v1"));
        var serviceController = new FakeWindowsServiceController(ServiceStatus(FluxVaultWindowsServiceState.Stopped, "FluxVault service is stopped."))
        {
            StartResult = new FluxVaultWindowsServiceActionResult(
                Success: false,
                Status: ServiceStatus(FluxVaultWindowsServiceState.Stopped, "FluxVault service is stopped."),
                Message: "Starting FluxVaultService requires elevated permissions.")
        };
        var viewModel = new MainWindowViewModel(
            client,
            TimeSpan.FromMilliseconds(20),
            new FileBrowserViewModel(new EmptyFileBrowserFileSystem()),
            serviceController);
        await viewModel.RefreshAsync();

        await viewModel.ToggleWindowsServiceCommand.ExecuteAsync(null);

        Assert.Equal(1, serviceController.StartCount);
        Assert.True(viewModel.IsServiceWarningVisible);
        Assert.Contains("elevated", viewModel.ServiceWarningText, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("Start service", viewModel.ServiceControlActionLabel);
    }

    [Fact]
    public async Task Refresh_shows_durable_change_status_when_service_reports_it()
    {
        var checkedAt = new DateTimeOffset(2026, 4, 27, 18, 14, 5, TimeSpan.Zero);
        var status = StatusWithVersions("v1") with
        {
            DurableChange = new DurableChangeRuntimeStatus(
                checkedAt,
                "USN active. Found 0 changed file(s).",
                null,
                [new UsnJournalCheckpoint("docs", @"D:\", 42, 200, 1, checkedAt)])
        };
        var client = new FakeFluxVaultServiceClient(status);
        var viewModel = new MainWindowViewModel(client, TimeSpan.FromMilliseconds(20));

        await viewModel.RefreshAsync();

        Assert.DoesNotContain("USN active", viewModel.ServiceStatus);
        Assert.Contains("USN: active", viewModel.UsnHealth);
        Assert.Contains("last checked", viewModel.UsnHealth);
        Assert.Contains(checkedAt.ToLocalTime().ToString("HH:mm:ss"), viewModel.UsnHealth);
        Assert.Contains("found 0 changed file(s)", viewModel.UsnHealth);
        Assert.DoesNotContain("Capture:", viewModel.UsnHealth);
    }

    [Fact]
    public async Task Refresh_shows_durable_change_fallback_reason_and_tooltip_details()
    {
        var checkedAt = DateTimeOffset.UtcNow;
        var status = StatusWithVersions("v1") with
        {
            DurableChange = new DurableChangeRuntimeStatus(
                checkedAt,
                "USN unavailable.",
                "Unable to open volume \\\\.\\D: Access is denied.",
                []) with
            {
                Details =
                    [
                        new DurableChangeDetail(
                            WatchedFolderId: "docs",
                            Path: @"D:\Work",
                            VolumeRoot: @"D:\",
                            Operation: "FSCTL_QUERY_USN_JOURNAL",
                            Reason: "Unsupported volume.",
                            Win32ErrorCode: 1)
                    ]
            }
        };
        var client = new FakeFluxVaultServiceClient(status);
        var viewModel = new MainWindowViewModel(client, TimeSpan.FromMilliseconds(20));

        await viewModel.RefreshAsync();

        Assert.Contains("Unable to open volume", viewModel.UsnHealth);
        Assert.Contains("FSCTL_QUERY_USN_JOURNAL", viewModel.UsnHealthToolTip);
        Assert.Contains("Unsupported volume", viewModel.UsnHealthToolTip);
    }

    [Fact]
    public async Task Refresh_keeps_raw_usn_failure_out_of_visible_header_text()
    {
        var rawFailure = "USN catch-up failed: Unable to find an entry point named 'NativeDeviceIoControl' in DLL 'kernel32.dll'.";
        var checkedAt = DateTimeOffset.UtcNow;
        var status = StatusWithVersions("v1") with
        {
            DurableChange = new DurableChangeRuntimeStatus(
                checkedAt,
                "USN unavailable.",
                rawFailure,
                []) with
            {
                Details =
                    [
                        new DurableChangeDetail(
                            WatchedFolderId: "docs",
                            Path: @"D:\Work",
                            VolumeRoot: @"D:\",
                            Operation: "FSCTL_QUERY_USN_JOURNAL",
                            Reason: rawFailure,
                            Win32ErrorCode: null)
                    ]
            }
        };
        var client = new FakeFluxVaultServiceClient(status);
        var viewModel = new MainWindowViewModel(client, TimeSpan.FromMilliseconds(20));

        await viewModel.RefreshAsync();

        Assert.DoesNotContain("NativeDeviceIoControl", viewModel.ServiceStatus);
        Assert.DoesNotContain("NativeDeviceIoControl", viewModel.UsnHealth);
        Assert.Contains("unable to query change journal", viewModel.UsnHealth, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("NativeDeviceIoControl", viewModel.UsnHealthToolTip);
        Assert.Contains("NativeDeviceIoControl", viewModel.ServiceStatusToolTip);
    }

    [Fact]
    public async Task Refresh_populates_capture_status_activity()
    {
        var status = StatusWithVersions("v1") with
        {
            CaptureStatuses =
            [
                new CaptureRuntimeStatus(
                    @"D:\Work\blocked.txt",
                    "docs",
                    CaptureRuntimeState.Blocked,
                    DateTimeOffset.UtcNow,
                    DateTimeOffset.UtcNow.AddSeconds(30),
                    DateTimeOffset.UtcNow,
                    null,
                    "File is locked.",
                    null,
                    2)
            ]
        };
        var client = new FakeFluxVaultServiceClient(status);
        var viewModel = new MainWindowViewModel(client, TimeSpan.FromMilliseconds(20));

        await viewModel.RefreshAsync();

        var row = Assert.Single(viewModel.CaptureStatuses);
        Assert.Equal(CaptureRuntimeState.Blocked, row.State);
        Assert.Contains("Blocked", viewModel.CaptureHealth);
    }

    [Fact]
    public async Task Refresh_populates_repository_health_dashboard_rows()
    {
        var status = StatusWithVersions("v1") with
        {
            RepositoryHealth = HealthSnapshot(
                RepositoryHealthState.Warning,
                "Repository repaired 1 issue.",
                ScrubReport(RepositoryHealthState.Warning, repaired: 1),
                RehearsalReport(RepositoryHealthState.Healthy, failed: 0))
        };
        var client = new FakeFluxVaultServiceClient(status);
        var viewModel = new MainWindowViewModel(client, TimeSpan.FromMilliseconds(20));

        await viewModel.RefreshAsync();

        Assert.Contains("Warning", viewModel.RepositoryHealthStatus);
        Assert.Contains(viewModel.RepositoryHealthRows, row => row.Name == "Repository integrity" && row.Status.Contains("repaired", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(viewModel.RepositoryHealthRows, row => row.Name == "Restore rehearsal" && row.Status.Contains("passed", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Run_repository_scrub_command_requests_scrub_and_updates_health_rows()
    {
        var client = new FakeFluxVaultServiceClient(StatusWithVersions("v1"))
        {
            RepositoryScrubResponse = FluxVaultIpcResponse.WithRepositoryScrub(ScrubReport(RepositoryHealthState.Healthy, repaired: 1))
        };
        var viewModel = new MainWindowViewModel(client, TimeSpan.FromMilliseconds(20));

        await viewModel.RunRepositoryScrubCommand.ExecuteAsync(null);

        Assert.Contains(FluxVaultIpcCommand.RunRepositoryScrub, client.Commands);
        Assert.Contains("scrub completed", viewModel.RepositoryHealthStatus, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(viewModel.RepositoryHealthRows, row => row.Name == "Repository integrity" && row.Status.Contains("repaired 1", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Run_restore_rehearsal_command_requests_rehearsal_and_updates_health_rows()
    {
        var client = new FakeFluxVaultServiceClient(StatusWithVersions("v1"))
        {
            RestoreRehearsalResponse = FluxVaultIpcResponse.WithRestoreRehearsal(RehearsalReport(RepositoryHealthState.Healthy, failed: 0))
        };
        var viewModel = new MainWindowViewModel(client, TimeSpan.FromMilliseconds(20));

        await viewModel.RunRestoreRehearsalCommand.ExecuteAsync(null);

        Assert.Contains(FluxVaultIpcCommand.RunRestoreRehearsal, client.Commands);
        Assert.Contains("restore rehearsal completed", viewModel.RepositoryHealthStatus, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(viewModel.RepositoryHealthRows, row => row.Name == "Restore rehearsal" && row.Status.Contains("passed", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Refresh_shows_capture_consistency_detail_from_service_status()
    {
        var status = StatusWithVersions("v1") with
        {
            CaptureStatuses =
            [
                new CaptureRuntimeStatus(
                    @"D:\Work\db.mdf",
                    "docs",
                    CaptureRuntimeState.Captured,
                    DateTimeOffset.UtcNow,
                    null,
                    DateTimeOffset.UtcNow,
                    null,
                    null,
                    CaptureConsistency.AppConsistent,
                    1,
                    ConsistencyDetail: "SqlServerWriter covered D:\\Work\\db.mdf.")
            ]
        };
        var client = new FakeFluxVaultServiceClient(status);
        var viewModel = new MainWindowViewModel(client, TimeSpan.FromMilliseconds(20));

        await viewModel.RefreshAsync();

        var row = Assert.Single(viewModel.CaptureStatuses);
        Assert.Equal(CaptureRuntimeState.Captured, row.State);
        Assert.Contains("SqlServerWriter", row.Detail);
    }

    [Fact]
    public async Task Restore_sends_ipc_only_after_destination_selection()
    {
        using var workspace = TempFolder.Create();
        var destination = Path.Combine(workspace.Path, "restored.txt");
        var client = new FakeFluxVaultServiceClient(StatusWithVersions("v1"));
        var picker = new FakeRestoreDestinationPicker(destination);
        var confirmation = new FakeRestoreOverwriteConfirmation(confirmOverwrite: true);
        var viewModel = new MainWindowViewModel(
            client,
            TimeSpan.FromMilliseconds(20),
            picker,
            confirmation);
        await viewModel.RefreshAsync();
        viewModel.SelectedVersion = viewModel.RecentVersions.Single();

        await viewModel.RestoreSelectedCommand.ExecuteAsync(null);

        var request = Assert.Single(client.RestoreRequests);
        Assert.Equal("v1", request.VersionId);
        Assert.Equal(destination, request.OutputPath);
        Assert.Equal(1, picker.PickCount);
        Assert.Equal(0, confirmation.ConfirmCount);
        Assert.Contains("restored v1", viewModel.ServiceStatus, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Restore_cancelled_destination_does_not_send_ipc_and_preserves_selection()
    {
        var client = new FakeFluxVaultServiceClient(StatusWithVersions("v1"));
        var picker = new FakeRestoreDestinationPicker((string?)null);
        var viewModel = new MainWindowViewModel(
            client,
            TimeSpan.FromMilliseconds(20),
            picker,
            new FakeRestoreOverwriteConfirmation(confirmOverwrite: true));
        await viewModel.RefreshAsync();
        viewModel.SelectedVersion = viewModel.RecentVersions.Single();

        await viewModel.RestoreSelectedCommand.ExecuteAsync(null);

        Assert.Empty(client.RestoreRequests);
        Assert.NotNull(viewModel.SelectedVersion);
        Assert.Contains("cancelled", viewModel.ServiceStatus, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Existing_restore_destination_requires_overwrite_confirmation()
    {
        using var workspace = TempFolder.Create();
        var destination = Path.Combine(workspace.Path, "restored.txt");
        await File.WriteAllTextAsync(destination, "existing");
        var client = new FakeFluxVaultServiceClient(StatusWithVersions("v1"));
        var confirmation = new FakeRestoreOverwriteConfirmation(confirmOverwrite: true);
        var viewModel = new MainWindowViewModel(
            client,
            TimeSpan.FromMilliseconds(20),
            new FakeRestoreDestinationPicker(destination),
            confirmation);
        await viewModel.RefreshAsync();
        viewModel.SelectedVersion = viewModel.RecentVersions.Single();

        await viewModel.RestoreSelectedCommand.ExecuteAsync(null);

        Assert.Single(client.RestoreRequests);
        Assert.Equal(1, confirmation.ConfirmCount);
        Assert.Equal(destination, confirmation.LastDestinationPath);
    }

    [Fact]
    public async Task Overwrite_denial_cancels_restore_without_ipc()
    {
        using var workspace = TempFolder.Create();
        var destination = Path.Combine(workspace.Path, "restored.txt");
        await File.WriteAllTextAsync(destination, "existing");
        var client = new FakeFluxVaultServiceClient(StatusWithVersions("v1"));
        var viewModel = new MainWindowViewModel(
            client,
            TimeSpan.FromMilliseconds(20),
            new FakeRestoreDestinationPicker(destination),
            new FakeRestoreOverwriteConfirmation(confirmOverwrite: false));
        await viewModel.RefreshAsync();
        viewModel.SelectedVersion = viewModel.RecentVersions.Single();

        await viewModel.RestoreSelectedCommand.ExecuteAsync(null);

        Assert.Empty(client.RestoreRequests);
        Assert.NotNull(viewModel.SelectedVersion);
        Assert.Contains("overwrite denied", viewModel.ServiceStatus, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Restore_ipc_failure_shows_safe_failure_status_and_preserves_selection()
    {
        using var workspace = TempFolder.Create();
        var destination = Path.Combine(workspace.Path, "restored.txt");
        var client = new FakeFluxVaultServiceClient(StatusWithVersions("v1"))
        {
            RestoreResponse = FluxVaultIpcResponse.Failure("Destination is locked or access is denied.")
        };
        var viewModel = new MainWindowViewModel(
            client,
            TimeSpan.FromMilliseconds(20),
            new FakeRestoreDestinationPicker(destination),
            new FakeRestoreOverwriteConfirmation(confirmOverwrite: true));
        await viewModel.RefreshAsync();
        viewModel.SelectedVersion = viewModel.RecentVersions.Single();

        await viewModel.RestoreSelectedCommand.ExecuteAsync(null);

        Assert.NotNull(viewModel.SelectedVersion);
        Assert.Contains("restore failed", viewModel.ServiceStatus, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("access is denied", viewModel.ServiceStatus, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Restore_ipc_exception_does_not_crash_dashboard()
    {
        using var workspace = TempFolder.Create();
        var destination = Path.Combine(workspace.Path, "restored.txt");
        var client = new FakeFluxVaultServiceClient(StatusWithVersions("v1"));
        client.ThrowOnNextRestore(new IOException("Named pipe closed."));
        var viewModel = new MainWindowViewModel(
            client,
            TimeSpan.FromMilliseconds(20),
            new FakeRestoreDestinationPicker(destination),
            new FakeRestoreOverwriteConfirmation(confirmOverwrite: true));
        await viewModel.RefreshAsync();
        viewModel.SelectedVersion = viewModel.RecentVersions.Single();

        await viewModel.RestoreSelectedCommand.ExecuteAsync(null);

        Assert.NotNull(viewModel.SelectedVersion);
        Assert.Contains("restore failed", viewModel.ServiceStatus, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Named pipe closed", viewModel.ServiceStatus);
    }

    [Fact]
    public async Task Restore_path_request_selects_matching_version_as_restore_hint()
    {
        var hintedPath = Path.GetFullPath(@"D:\Work\hinted.txt");
        var otherPath = Path.GetFullPath(@"D:\Work\other.txt");
        var client = new FakeFluxVaultServiceClient(StatusWithVersionSources(("v1", otherPath), ("v2", hintedPath)));
        var viewModel = new MainWindowViewModel(
            client,
            TimeSpan.FromMilliseconds(20),
            new FakeRestoreDestinationPicker((string?)null),
            new FakeRestoreOverwriteConfirmation(confirmOverwrite: true));

        viewModel.ApplyRestorePathRequest(hintedPath);
        await viewModel.RefreshAsync();

        Assert.Equal(hintedPath, viewModel.RestoreHintPath);
        Assert.NotNull(viewModel.SelectedVersion);
        Assert.Equal("v2", viewModel.SelectedVersion.VersionId);
        Assert.Contains("restore request", viewModel.ServiceStatus, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Explorer_add_path_request_saves_configuration_immediately()
    {
        var client = new FakeFluxVaultServiceClient(StatusWithSelectionRules([]));
        var viewModel = new MainWindowViewModel(client, TimeSpan.FromMilliseconds(20));
        await viewModel.RefreshAsync();

        await viewModel.ApplyStartupRequestAsync(
            new AppStartupRequest(AppStartupRequestAction.AddToFluxVault, @"D:\Work\Docs"));

        var saved = Assert.Single(client.SavedConfigurations);
        var rule = Assert.Single(saved.SelectionRules);
        Assert.Equal(Path.GetFullPath(@"D:\Work\Docs"), rule.Path);
        Assert.Equal(ProtectionSelectionMode.ImmediateFiles, rule.Mode);
        Assert.Contains("added", viewModel.ServiceStatus, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Explorer_remove_inherited_path_request_saves_scoped_exclusion_immediately()
    {
        var inherited = new ProtectionSelectionRule(
            "root",
            Path.GetFullPath(@"D:\Work"),
            ProtectionSelectionMode.RecursiveFolder,
            CompressionPreference.Zstd,
            ResourceProfile.Balanced,
            IsEnabled: true);
        var client = new FakeFluxVaultServiceClient(StatusWithSelectionRules([inherited]));
        var viewModel = new MainWindowViewModel(client, TimeSpan.FromMilliseconds(20));
        await viewModel.RefreshAsync();

        await viewModel.ApplyStartupRequestAsync(
            new AppStartupRequest(AppStartupRequestAction.RemoveFromFluxVault, @"D:\Work\Docs\brief.docx"));

        var saved = Assert.Single(client.SavedConfigurations);
        var rule = Assert.Single(saved.SelectionRules);
        Assert.Equal(Path.GetFullPath(@"D:\Work"), rule.Path);
        Assert.Contains(rule.ExcludeRegexRules ?? [], regex => regex.Pattern.Contains("brief\\.docx"));
        Assert.Contains("excluded", viewModel.ServiceStatus, StringComparison.OrdinalIgnoreCase);
    }

    private static FluxVaultServiceStatus StatusWithVersions(params string[] versionIds)
    {
        return StatusWithVersionSummaries(versionIds
            .Select(id => new RepositoryVersionSummary(
                id,
                @"D:\Work\file.txt",
                DateTimeOffset.UtcNow,
                CaptureConsistency.BestEffort,
                128,
                1))
            .ToArray());
    }

    private static FluxVaultServiceStatus StatusWithVersionSummaries(params RepositoryVersionSummary[] versions)
    {
        var configuration = FluxVaultConfiguration.CreateDefault(@"D:\Vault");
        return new FluxVaultServiceStatus(
            IsServiceRunning: true,
            Configuration: configuration,
            LastMessage: "Captured 1 file(s).",
            LastCaptureUtc: DateTimeOffset.UtcNow,
            WatchedFolders: [],
            RecentVersions: versions);
    }

    private static FluxVaultServiceStatus StatusWithVersionSources(params (string VersionId, string SourcePath)[] versions)
    {
        var configuration = FluxVaultConfiguration.CreateDefault(@"D:\Vault");
        return new FluxVaultServiceStatus(
            IsServiceRunning: true,
            Configuration: configuration,
            LastMessage: "Captured 1 file(s).",
            LastCaptureUtc: DateTimeOffset.UtcNow,
            WatchedFolders: [],
            RecentVersions: versions
                .Select(version => new RepositoryVersionSummary(
                    version.VersionId,
                    version.SourcePath,
                    DateTimeOffset.UtcNow,
                    CaptureConsistency.BestEffort,
                    128,
                    1))
                .ToArray());
    }

    private static FluxVaultServiceStatus StatusWithRepository(string repositoryPath)
    {
        return StatusWithVersions() with
        {
            Configuration = new FluxVaultConfiguration(
                RepositoryPath: repositoryPath,
                MirrorPath: null,
                IsEnabled: true,
                WatchedFolders: [])
        };
    }

    private static FluxVaultServiceStatus StatusWithSelectionRules(IReadOnlyList<ProtectionSelectionRule> selectionRules)
    {
        return StatusWithVersions() with
        {
            Configuration = FluxVaultConfiguration.CreateDefault(@"D:\Vault") with
            {
                SelectionRules = selectionRules,
                WatchedFolders = []
            }
        };
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!condition())
        {
            await Task.Delay(10, cts.Token);
        }
    }

    private static FluxVaultWindowsServiceStatus ServiceStatus(FluxVaultWindowsServiceState state, string message)
    {
        return new FluxVaultWindowsServiceStatus("FluxVaultService", state, message);
    }

    private static FluxVaultWindowsServiceActionResult ActionResult(FluxVaultWindowsServiceState state, string message)
    {
        return new FluxVaultWindowsServiceActionResult(true, ServiceStatus(state, message), message);
    }

    private static RepositoryHealthSnapshot HealthSnapshot(
        RepositoryHealthState state,
        string summary,
        RepositoryScrubReport? scrub,
        RestoreRehearsalReport? rehearsal,
        MirrorRepairReport? mirrorRepair = null)
    {
        return new RepositoryHealthSnapshot(DateTimeOffset.UtcNow, state, summary, scrub, rehearsal, mirrorRepair);
    }

    private static RepositoryScrubReport ScrubReport(RepositoryHealthState state, int repaired)
    {
        return new RepositoryScrubReport(
            DateTimeOffset.UtcNow,
            state,
            ManifestCount: 2,
            CheckedChunkCount: 3,
            IssueCount: repaired,
            RepairedIssueCount: repaired,
            Issues: []);
    }

    private static RestoreRehearsalReport RehearsalReport(RepositoryHealthState state, int failed)
    {
        return new RestoreRehearsalReport(
            DateTimeOffset.UtcNow,
            state,
            RequestedVersionCount: 3,
            RehearsedVersionCount: 3 - failed,
            FailedVersionCount: failed,
            Results: []);
    }

    private static MirrorRepairReport MirrorRepairReport(
        bool isPreview,
        string? requestedMirrorNodeId,
        string nodeId,
        string nodeLabel,
        string nodePath,
        RepositoryHealthState healthState,
        int repaired)
    {
        return new MirrorRepairReport(
            DateTimeOffset.UtcNow,
            isPreview,
            requestedMirrorNodeId,
            healthState,
            IssueCount: repaired,
            RepairedIssueCount: repaired,
            Nodes:
            [
                new MirrorNodeRepairReport(
                    nodeId,
                    nodeLabel,
                    nodePath,
                    IsEnabled: true,
                    healthState,
                    IssueCount: repaired,
                    RepairedIssueCount: repaired,
                    Issues: [])
            ],
            Issues: []);
    }

    private sealed class FakeFluxVaultServiceClient(params FluxVaultServiceStatus[] statuses) : IFluxVaultServiceClient
    {
        private readonly Queue<FluxVaultServiceStatus> statuses = new(statuses);
        private Exception? nextException;
        private Exception? nextRestoreException;

        public List<FluxVaultIpcCommand> Commands { get; } = [];

        public List<(string VersionId, string OutputPath)> RestoreRequests { get; } = [];

        public List<FluxVaultConfiguration> SavedConfigurations { get; } = [];

        public FluxVaultIpcResponse RestoreResponse { get; init; } = FluxVaultIpcResponse.Ok();

        public FluxVaultIpcResponse RepositoryScrubResponse { get; init; } = FluxVaultIpcResponse.Ok();

        public FluxVaultIpcResponse RestoreRehearsalResponse { get; init; } = FluxVaultIpcResponse.Ok();

        public FluxVaultIpcResponse MirrorRepairResponse { get; init; } = FluxVaultIpcResponse.Ok();

        public List<(FluxVaultIpcCommand Command, string? MirrorNodeId)> MirrorRepairRequests { get; } = [];

        public int GetStatusCount => Commands.Count(command => command == FluxVaultIpcCommand.GetStatus);

        public Task<FluxVaultIpcResponse> SendAsync(FluxVaultIpcRequest request, CancellationToken cancellationToken = default)
        {
            Commands.Add(request.Command);
            if (nextException is not null)
            {
                var exception = nextException;
                nextException = null;
                throw exception;
            }

            if (request.Command == FluxVaultIpcCommand.RestoreVersion)
            {
                if (nextRestoreException is not null)
                {
                    var exception = nextRestoreException;
                    nextRestoreException = null;
                    throw exception;
                }

                RestoreRequests.Add((
                    request.VersionId ?? throw new InvalidOperationException("Missing version id."),
                    request.OutputPath ?? throw new InvalidOperationException("Missing output path.")));
                return Task.FromResult(RestoreResponse);
            }

            if (request.Command == FluxVaultIpcCommand.SaveConfiguration)
            {
                SavedConfigurations.Add(request.Configuration ?? throw new InvalidOperationException("Missing configuration."));
                return Task.FromResult(FluxVaultIpcResponse.Ok());
            }

            if (request.Command == FluxVaultIpcCommand.RunRepositoryScrub)
            {
                return Task.FromResult(RepositoryScrubResponse);
            }

            if (request.Command == FluxVaultIpcCommand.RunRestoreRehearsal)
            {
                return Task.FromResult(RestoreRehearsalResponse);
            }

            if (request.Command is FluxVaultIpcCommand.PreviewMirrorRepair or FluxVaultIpcCommand.RunMirrorRepair)
            {
                MirrorRepairRequests.Add((request.Command, request.MirrorNodeId));
                return Task.FromResult(MirrorRepairResponse);
            }

            var status = statuses.Count > 1 ? statuses.Dequeue() : statuses.Peek();
            return Task.FromResult(FluxVaultIpcResponse.WithStatus(status));
        }

        public void ThrowOnNextRequest(Exception exception)
        {
            nextException = exception;
        }

        public void ThrowOnNextRestore(Exception exception)
        {
            nextRestoreException = exception;
        }
    }

    private sealed class BlockingFluxVaultServiceClient(FluxVaultServiceStatus status) : IFluxVaultServiceClient
    {
        private readonly TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int GetStatusCount { get; private set; }

        public async Task<FluxVaultIpcResponse> SendAsync(FluxVaultIpcRequest request, CancellationToken cancellationToken = default)
        {
            GetStatusCount++;
            await release.Task.WaitAsync(cancellationToken);
            return FluxVaultIpcResponse.WithStatus(status);
        }

        public void Release()
        {
            release.SetResult();
        }
    }

    private sealed class FakeWindowsServiceController(FluxVaultWindowsServiceStatus initialStatus) : IFluxVaultWindowsServiceController
    {
        private FluxVaultWindowsServiceStatus status = initialStatus;

        public FluxVaultWindowsServiceActionResult? StartResult { get; init; }

        public FluxVaultWindowsServiceActionResult? StopResult { get; init; }

        public int StartCount { get; private set; }

        public int StopCount { get; private set; }

        public Task<FluxVaultWindowsServiceStatus> GetStatusAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(status);
        }

        public Task<FluxVaultWindowsServiceActionResult> StartAsync(CancellationToken cancellationToken = default)
        {
            StartCount++;
            var result = StartResult ?? ActionResult(FluxVaultWindowsServiceState.Running, "FluxVault service started.");
            status = result.Status;
            return Task.FromResult(result);
        }

        public Task<FluxVaultWindowsServiceActionResult> StopAsync(CancellationToken cancellationToken = default)
        {
            StopCount++;
            var result = StopResult ?? ActionResult(FluxVaultWindowsServiceState.Stopped, "FluxVault service stopped.");
            status = result.Status;
            return Task.FromResult(result);
        }
    }

    private sealed class EmptyFileBrowserFileSystem : IFileBrowserFileSystem
    {
        public IReadOnlyList<FileBrowserFolderInfo> GetRoots()
        {
            return [];
        }

        public IReadOnlyList<FileBrowserFolderInfo> GetChildFolders(string path)
        {
            return [];
        }

        public IReadOnlyList<FileBrowserFileInfo> GetFiles(string path)
        {
            return [];
        }
    }

    private sealed class FakeRestoreDestinationPicker(params string?[] destinations) : IRestoreDestinationPicker
    {
        private readonly Queue<string?> destinations = new(destinations);

        public int PickCount { get; private set; }

        public VersionRow? LastVersion { get; private set; }

        public string? PickDestination(VersionRow version)
        {
            PickCount++;
            LastVersion = version;
            return destinations.Count > 1 ? destinations.Dequeue() : destinations.Peek();
        }
    }

    private sealed class FakeRestoreOverwriteConfirmation(bool confirmOverwrite) : IRestoreOverwriteConfirmation
    {
        public int ConfirmCount { get; private set; }

        public string? LastDestinationPath { get; private set; }

        public bool ConfirmOverwrite(string destinationPath)
        {
            ConfirmCount++;
            LastDestinationPath = destinationPath;
            return confirmOverwrite;
        }
    }

    private sealed class TempFolder : IDisposable
    {
        private TempFolder(string path)
        {
            Path = path;
        }

        public string Path { get; }

        public static TempFolder Create()
        {
            var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"FluxVault.App.Tests.{Guid.NewGuid():N}");
            Directory.CreateDirectory(path);
            return new TempFolder(path);
        }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}
