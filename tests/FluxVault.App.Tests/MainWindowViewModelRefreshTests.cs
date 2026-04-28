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

    private static FluxVaultServiceStatus StatusWithVersions(params string[] versionIds)
    {
        var configuration = FluxVaultConfiguration.CreateDefault(@"D:\Vault");
        return new FluxVaultServiceStatus(
            IsServiceRunning: true,
            Configuration: configuration,
            LastMessage: "Captured 1 file(s).",
            LastCaptureUtc: DateTimeOffset.UtcNow,
            WatchedFolders: [],
            RecentVersions: versionIds
                .Select(id => new RepositoryVersionSummary(
                    id,
                    @"D:\Work\file.txt",
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

    private sealed class FakeFluxVaultServiceClient(params FluxVaultServiceStatus[] statuses) : IFluxVaultServiceClient
    {
        private readonly Queue<FluxVaultServiceStatus> statuses = new(statuses);
        private Exception? nextException;

        public List<FluxVaultIpcCommand> Commands { get; } = [];

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

            var status = statuses.Count > 1 ? statuses.Dequeue() : statuses.Peek();
            return Task.FromResult(FluxVaultIpcResponse.WithStatus(status));
        }

        public void ThrowOnNextRequest(Exception exception)
        {
            nextException = exception;
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
}
