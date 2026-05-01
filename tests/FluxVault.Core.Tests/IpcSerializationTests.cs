using FluxVault.Abstractions.Configuration;
using FluxVault.Abstractions.ChangeTracking;
using FluxVault.Abstractions.Ipc;
using FluxVault.Abstractions.Storage;
using FluxVault.Abstractions.Sync;
using FluxVault.Core.Ipc;

namespace FluxVault.Core.Tests;

public sealed class IpcSerializationTests
{
    [Fact]
    public void Request_serialization_preserves_command_and_payload()
    {
        var configuration = FluxVaultConfiguration.CreateDefault(@"C:\ProgramData\FluxVault");
        var request = FluxVaultIpcRequest.SaveConfiguration(configuration);

        var roundTrip = FluxVaultIpcSerializer.DeserializeRequest(FluxVaultIpcSerializer.SerializeRequest(request));

        Assert.Equal(FluxVaultIpcCommand.SaveConfiguration, roundTrip.Command);
        Assert.NotNull(roundTrip.Configuration);
        Assert.Equal(configuration.RepositoryPath, roundTrip.Configuration.RepositoryPath);
    }

    [Fact]
    public void Response_serialization_preserves_status_details()
    {
        var checkedAt = new DateTimeOffset(2026, 4, 27, 12, 1, 0, TimeSpan.Zero);
        var status = new FluxVaultServiceStatus(
            IsServiceRunning: true,
            Configuration: FluxVaultConfiguration.CreateDefault(@"C:\ProgramData\FluxVault"),
            LastMessage: "Ready",
            LastCaptureUtc: new DateTimeOffset(2026, 4, 27, 12, 0, 0, TimeSpan.Zero),
            WatchedFolders: [],
            RecentVersions: [],
            DurableChange: new DurableChangeRuntimeStatus(
                checkedAt,
                "USN active.",
                null,
                [new UsnJournalCheckpoint("docs", @"D:\", 42, 1000, 1, checkedAt)]) with
            {
                Details =
                    [
                        new DurableChangeDetail(
                            WatchedFolderId: "docs",
                            Path: @"D:\Work",
                            VolumeRoot: @"D:\",
                            Operation: "FSCTL_QUERY_USN_JOURNAL",
                            Reason: "USN active.",
                            Win32ErrorCode: null)
                    ]
            },
            CaptureStatuses:
            [
                new CaptureRuntimeStatus(
                    @"D:\Work\db.mdf",
                    "docs",
                    CaptureRuntimeState.Captured,
                    checkedAt,
                    null,
                    checkedAt,
                    null,
                    null,
                    CaptureConsistency.AppConsistent,
                    1,
                    ConsistencyDetail: "SqlServerWriter covered D:\\Work\\db.mdf.")
            ],
            DeviceIdentity: new DeviceIdentityRuntimeStatus(
                "device-local",
                "Studio PC",
                [
                    new TrustedDeviceRuntimeStatus(
                        "device-local",
                        "Studio PC",
                        DeviceTrustState.Local,
                        new DateTimeOffset(2026, 4, 27, 12, 2, 0, TimeSpan.Zero),
                        checkedAt)
                ]),
            Sync: new SyncRuntimeStatus(
                LocalDeviceId: "device-local",
                PeerHeads:
                [
                    new PeerHeadRecord(
                        "device-local",
                        "Studio PC",
                        HeadSequenceNumber: 3,
                        HeadOperationId: "operation-3",
                        UpdatedAtUtc: checkedAt)
                ],
                Cursors:
                [
                    new PeerCursorRecord(
                        "device-laptop",
                        LastSeenSequenceNumber: 2,
                        LastSeenOperationId: "operation-2",
                        UpdatedAtUtc: checkedAt)
                ],
                Mappings:
                [
                    new SyncMappingRecord(
                        MappingId: "mapping-1",
                        SourceDeviceId: "device-laptop",
                        SourcePath: @"D:\Work\Docs\brief.docx",
                        LocalDeviceId: "device-local",
                        LocalPath: @"E:\Protected\brief.docx",
                        Status: SyncMappingStatus.PendingConfirmation,
                        CreatedAtUtc: checkedAt)
                ],
                AppliedRemoteVersions:
                [
                    new SyncAppliedVersionRecord(
                        SourceDeviceId: "device-laptop",
                        SourceOperationId: "operation-42",
                        SourceVersionId: "remote-version-42",
                        LocalVersionId: "local-version-1",
                        LocalPath: @"E:\Protected\brief.docx",
                        ContentSignature: "sig-42",
                        AppliedAtUtc: checkedAt,
                        MappingId: "mapping-1")
                ],
                Hydrations:
                [
                    new SyncHydrationRecord(
                        HydrationId: "hydration-1",
                        SourceDeviceId: "device-laptop",
                        SourceOperationId: "operation-42",
                        SourceVersionId: "remote-version-42",
                        LocalPath: @"E:\Protected\brief.docx",
                        State: SyncHydrationState.Conflict,
                        Message: "Target has local changes.",
                        CompletedAtUtc: checkedAt,
                        ConflictId: "conflict-1")
                ],
                Conflicts:
                [
                    new SyncConflictRecord(
                        ConflictId: "conflict-1",
                        LocalPath: @"E:\Protected\brief.docx",
                        SourceDeviceId: "device-laptop",
                        SourceVersionId: "remote-version-42",
                        SourceOperationId: "operation-42",
                        DetectedAtUtc: checkedAt,
                        Status: SyncConflictStatus.Open,
                        AvailableActions: [SyncConflictAction.KeepLocal, SyncConflictAction.KeepRemote])
                ]),
            PerformanceWorkspace: new PerformanceWorkspaceRuntimeStatus(
                IsEnabled: true,
                Mode: PerformanceWorkspaceMode.WinFsp,
                WorkspacePath: @"D:\FluxVaultFast",
                CacheSizeMegabytes: 2048,
                MountName: "FluxVaultFast",
                SetupScriptPath: @"D:\Repo\eng\winfsp\Register-FluxVaultWinFspWorkspace.ps1",
                ManifestPath: @"D:\Repo\eng\winfsp\FluxVault.WinFsp.Workspace.manifest.json",
                Status: "Prepared; WinFsp driver install not executed.",
                IsDriverCheckDeferred: true),
            ShellIntegration: new ShellIntegrationRuntimeStatus(
                IsEnabled: true,
                Mode: ShellIntegrationMode.CloudFilesApi,
                SyncRootPath: @"D:\FluxVaultCloudFiles",
                DisplayName: "FluxVault",
                HydrationPolicy: ShellHydrationPolicy.OnDemand,
                PlaceholderStatePath: @"D:\FluxVaultCloudFiles\.fluxvault",
                RegisterScriptPath: @"D:\Repo\eng\shell-integration\Register-FluxVaultShellIntegration.ps1",
                ManifestPath: @"D:\Repo\eng\shell-integration\FluxVault.CloudFiles.ProjFs.manifest.json",
                Status: "Prepared; shell registration not executed.",
                IsRegistrationDeferred: true,
                IsPlaceholderCreationDeferred: true),
            DirectCloud: new DirectCloudRuntimeStatus(
                IsEnabled: true,
                Status: "Direct cloud adapters configured; live validation deferred.",
                IsLiveValidationDeferred: true,
                Providers:
                [
                    new DirectCloudProviderRuntimeStatus(
                        DirectCloudProvider.AzureBlob,
                        "Azure.Storage.Blobs",
                        "Azure.Storage.Blobs.BlobContainerClient",
                        IsSdkAvailable: true),
                    new DirectCloudProviderRuntimeStatus(
                        DirectCloudProvider.OneDrive,
                        "Microsoft.Graph",
                        "Microsoft.Graph.GraphServiceClient",
                        IsSdkAvailable: true)
                ],
                Adapters:
                [
                    new DirectCloudAdapterRuntimeStatus(
                        "azure",
                        DirectCloudProvider.AzureBlob,
                        "Azure archive",
                        IsEnabled: true,
                        "Ready; credentials not loaded during status refresh.")
                ]));
        var response = FluxVaultIpcResponse.WithStatus(status);

        var roundTrip = FluxVaultIpcSerializer.DeserializeResponse(FluxVaultIpcSerializer.SerializeResponse(response));

        Assert.True(roundTrip.Success);
        Assert.NotNull(roundTrip.Status);
        Assert.Equal("Ready", roundTrip.Status.LastMessage);
        Assert.Equal(new DateTimeOffset(2026, 4, 27, 12, 0, 0, TimeSpan.Zero), roundTrip.Status.LastCaptureUtc);
        Assert.NotNull(roundTrip.Status.DurableChange);
        Assert.Equal("USN active.", roundTrip.Status.DurableChange.Status);
        Assert.Equal((long)1000, Assert.Single(roundTrip.Status.DurableChange.Checkpoints).NextUsn);
        Assert.Equal("FSCTL_QUERY_USN_JOURNAL", Assert.Single(roundTrip.Status.DurableChange.Details).Operation);
        var captureStatus = Assert.Single(roundTrip.Status.CaptureStatuses!);
        Assert.Equal(CaptureConsistency.AppConsistent, captureStatus.Consistency);
        Assert.Contains("SqlServerWriter", captureStatus.ConsistencyDetail);
        Assert.NotNull(roundTrip.Status.DeviceIdentity);
        Assert.Equal("device-local", roundTrip.Status.DeviceIdentity.DeviceId);
        Assert.Equal(DeviceTrustState.Local, Assert.Single(roundTrip.Status.DeviceIdentity.TrustedDevices).TrustState);
        Assert.NotNull(roundTrip.Status.Sync);
        Assert.Equal("device-local", roundTrip.Status.Sync.LocalDeviceId);
        Assert.Equal("operation-3", Assert.Single(roundTrip.Status.Sync.PeerHeads).HeadOperationId);
        Assert.Equal("device-laptop", Assert.Single(roundTrip.Status.Sync.Cursors).PeerDeviceId);
        Assert.Equal(SyncMappingStatus.PendingConfirmation, Assert.Single(roundTrip.Status.Sync.Mappings!).Status);
        Assert.Equal("remote-version-42", Assert.Single(roundTrip.Status.Sync.AppliedRemoteVersions!).SourceVersionId);
        Assert.Equal(SyncHydrationState.Conflict, Assert.Single(roundTrip.Status.Sync.Hydrations!).State);
        Assert.Equal(SyncConflictStatus.Open, Assert.Single(roundTrip.Status.Sync.Conflicts!).Status);
        Assert.NotNull(roundTrip.Status.PerformanceWorkspace);
        Assert.Equal(PerformanceWorkspaceMode.WinFsp, roundTrip.Status.PerformanceWorkspace.Mode);
        Assert.True(roundTrip.Status.PerformanceWorkspace.IsDriverCheckDeferred);
        Assert.NotNull(roundTrip.Status.ShellIntegration);
        Assert.Equal(ShellIntegrationMode.CloudFilesApi, roundTrip.Status.ShellIntegration.Mode);
        Assert.True(roundTrip.Status.ShellIntegration.IsRegistrationDeferred);
        Assert.True(roundTrip.Status.ShellIntegration.IsPlaceholderCreationDeferred);
        Assert.NotNull(roundTrip.Status.DirectCloud);
        Assert.True(roundTrip.Status.DirectCloud.IsLiveValidationDeferred);
        Assert.Equal(2, roundTrip.Status.DirectCloud.Providers.Count);
        Assert.Equal(DirectCloudProvider.AzureBlob, Assert.Single(roundTrip.Status.DirectCloud.Adapters).Provider);
    }

    [Fact]
    public void Retention_request_serialization_preserves_command()
    {
        var request = FluxVaultIpcRequest.RunRetentionNow();

        var roundTrip = FluxVaultIpcSerializer.DeserializeRequest(FluxVaultIpcSerializer.SerializeRequest(request));

        Assert.Equal(FluxVaultIpcCommand.RunRetentionNow, roundTrip.Command);
    }

    [Fact]
    public void Retention_response_serialization_preserves_result()
    {
        var result = new RepositoryRetentionResult(
            Decisions: [],
            KeptVersionCount: 42,
            PrunedVersionCount: 8,
            DeletedChunkCount: 5,
            ReclaimedBytes: 1024,
            RepositorySizeBytes: 4096,
            MirrorWarnings: ["mirror warning"]);
        var response = FluxVaultIpcResponse.WithRetentionResult(result);

        var roundTrip = FluxVaultIpcSerializer.DeserializeResponse(FluxVaultIpcSerializer.SerializeResponse(response));

        Assert.True(roundTrip.Success);
        Assert.NotNull(roundTrip.RetentionResult);
        Assert.Equal(8, roundTrip.RetentionResult.PrunedVersionCount);
        Assert.Equal(1024, roundTrip.RetentionResult.ReclaimedBytes);
        Assert.Equal("mirror warning", Assert.Single(roundTrip.RetentionResult.MirrorWarnings));
    }

    [Fact]
    public void Version_summary_serialization_preserves_lineage_details()
    {
        var version = new RepositoryVersionSummary(
            VersionId: "restore-version",
            SourcePath: @"D:\Work\Docs\brief.docx",
            CapturedAtUtc: new DateTimeOffset(2026, 4, 30, 9, 0, 0, TimeSpan.Zero),
            Consistency: CaptureConsistency.BestEffort,
            LogicalLength: 128,
            ChunkCount: 2,
            OperationType: VersionOperationType.Restore,
            ParentVersionIds: ["previous-destination-version"],
            RestoredFromVersionId: "source-version",
            ForkOriginVersionId: "source-version",
            InheritedFromVersionId: null,
            InheritedFromSourcePath: null,
            ContentSignature: "sig-v1");
        var response = FluxVaultIpcResponse.WithVersions([version]);

        var roundTrip = FluxVaultIpcSerializer.DeserializeResponse(FluxVaultIpcSerializer.SerializeResponse(response));

        var roundTrippedVersion = Assert.Single(roundTrip.Versions!);
        Assert.Equal(VersionOperationType.Restore, roundTrippedVersion.OperationType);
        Assert.Equal(["previous-destination-version"], roundTrippedVersion.ParentVersionIds);
        Assert.Equal("source-version", roundTrippedVersion.RestoredFromVersionId);
        Assert.Equal("source-version", roundTrippedVersion.ForkOriginVersionId);
        Assert.Equal("sig-v1", roundTrippedVersion.ContentSignature);
    }

    [Theory]
    [InlineData(FluxVaultIpcCommand.GetRepositoryHealth)]
    [InlineData(FluxVaultIpcCommand.RunRepositoryScrub)]
    [InlineData(FluxVaultIpcCommand.RunRestoreRehearsal)]
    [InlineData(FluxVaultIpcCommand.PreviewMirrorRebalance)]
    [InlineData(FluxVaultIpcCommand.RunMirrorRebalance)]
    [InlineData(FluxVaultIpcCommand.PreviewMirrorRepair)]
    [InlineData(FluxVaultIpcCommand.RunMirrorRepair)]
    [InlineData(FluxVaultIpcCommand.PreviewMirrorDrain)]
    [InlineData(FluxVaultIpcCommand.RunMirrorDrain)]
    public void Repository_maintenance_request_serialization_preserves_command(FluxVaultIpcCommand command)
    {
        var request = new FluxVaultIpcRequest(command, null, null, null, null, MirrorNodeId: "mirror-1");

        var roundTrip = FluxVaultIpcSerializer.DeserializeRequest(FluxVaultIpcSerializer.SerializeRequest(request));

        Assert.Equal(command, roundTrip.Command);
        Assert.Equal("mirror-1", roundTrip.MirrorNodeId);
    }

    [Fact]
    public void Mirror_drain_request_helpers_preserve_selected_node()
    {
        var preview = FluxVaultIpcSerializer.DeserializeRequest(
            FluxVaultIpcSerializer.SerializeRequest(FluxVaultIpcRequest.PreviewMirrorDrain("cloud")));
        var run = FluxVaultIpcSerializer.DeserializeRequest(
            FluxVaultIpcSerializer.SerializeRequest(FluxVaultIpcRequest.RunMirrorDrain("cloud")));

        Assert.Equal(FluxVaultIpcCommand.PreviewMirrorDrain, preview.Command);
        Assert.Equal("cloud", preview.MirrorNodeId);
        Assert.Equal(FluxVaultIpcCommand.RunMirrorDrain, run.Command);
        Assert.Equal("cloud", run.MirrorNodeId);
    }

    [Fact]
    public void Sync_conflict_request_helper_preserves_conflict_action()
    {
        var request = FluxVaultIpcRequest.ResolveConflict("conflict-1", SyncConflictAction.KeepLocal);

        var roundTrip = FluxVaultIpcSerializer.DeserializeRequest(FluxVaultIpcSerializer.SerializeRequest(request));

        Assert.Equal(FluxVaultIpcCommand.ResolveConflict, roundTrip.Command);
        Assert.Equal("conflict-1", roundTrip.ConflictId);
        Assert.Equal(SyncConflictAction.KeepLocal, roundTrip.ConflictAction);
    }

    [Fact]
    public void Mirror_rebalance_response_serialization_preserves_actions()
    {
        var report = new MirrorRebalancePreviewReport(
            CompletedAtUtc: new DateTimeOffset(2026, 5, 1, 9, 0, 0, TimeSpan.Zero),
            HealthState: RepositoryHealthState.Warning,
            CheckedChunkCount: 1,
            ActionCount: 1,
            EstimatedCopyBytes: 512,
            EstimatedDeleteBytes: 0,
            Nodes:
            [
                new MirrorNodeRebalancePreview(
                    NodeId: "cloud",
                    Label: "Cloud mirror",
                    Path: @"D:\Mirrors\Cloud",
                    IsEnabled: true,
                    HealthState: RepositoryHealthState.Warning,
                    ActionCount: 1,
                    EstimatedCopyBytes: 512,
                    EstimatedDeleteBytes: 0)
            ],
            Actions:
            [
                new MirrorRebalanceAction(
                    Action: MirrorRebalanceActionKind.CopyToMirror,
                    ArtefactKind: MirrorRebalanceArtefactKind.Chunk,
                    MirrorNodeId: "cloud",
                    MirrorNodeLabel: "Cloud mirror",
                    Path: @"D:\Mirrors\Cloud\chunks\aa\chunk.chunk",
                    ChunkDigest: "chunk-digest",
                    EstimatedBytes: 512,
                    Message: "Copy required.")
            ]);
        var response = FluxVaultIpcResponse.WithMirrorRebalance(report);

        var roundTrip = FluxVaultIpcSerializer.DeserializeResponse(FluxVaultIpcSerializer.SerializeResponse(response));

        Assert.True(roundTrip.Success);
        Assert.NotNull(roundTrip.MirrorRebalance);
        Assert.Equal(RepositoryHealthState.Warning, roundTrip.MirrorRebalance.HealthState);
        var action = Assert.Single(roundTrip.MirrorRebalance.Actions);
        Assert.Equal(MirrorRebalanceActionKind.CopyToMirror, action.Action);
        Assert.Equal("cloud", action.MirrorNodeId);
    }

    [Fact]
    public void Mirror_drain_response_serialization_preserves_operation_and_selected_node()
    {
        var report = new MirrorRebalancePreviewReport(
            CompletedAtUtc: new DateTimeOffset(2026, 5, 1, 10, 0, 0, TimeSpan.Zero),
            HealthState: RepositoryHealthState.Warning,
            CheckedChunkCount: 1,
            ActionCount: 1,
            EstimatedCopyBytes: 0,
            EstimatedDeleteBytes: 512,
            Nodes: [],
            Actions: [],
            Operation: MirrorRebalanceOperation.Drain,
            IsPreview: true,
            RequestedMirrorNodeId: "cloud");
        var response = FluxVaultIpcResponse.WithMirrorRebalance(report);

        var roundTrip = FluxVaultIpcSerializer.DeserializeResponse(FluxVaultIpcSerializer.SerializeResponse(response));

        Assert.True(roundTrip.Success);
        Assert.NotNull(roundTrip.MirrorRebalance);
        Assert.Equal(MirrorRebalanceOperation.Drain, roundTrip.MirrorRebalance.Operation);
        Assert.True(roundTrip.MirrorRebalance.IsPreview);
        Assert.Equal("cloud", roundTrip.MirrorRebalance.RequestedMirrorNodeId);
    }

    [Fact]
    public void Mirror_repair_response_serialization_preserves_node_report()
    {
        var report = new MirrorRepairReport(
            CompletedAtUtc: new DateTimeOffset(2026, 4, 30, 11, 0, 0, TimeSpan.Zero),
            IsPreview: true,
            RequestedMirrorNodeId: "cloud",
            HealthState: RepositoryHealthState.Warning,
            IssueCount: 1,
            RepairedIssueCount: 0,
            Nodes:
            [
                new MirrorNodeRepairReport(
                    NodeId: "cloud",
                    Label: "Cloud mirror",
                    Path: @"D:\Mirrors\Cloud",
                    IsEnabled: true,
                    HealthState: RepositoryHealthState.Warning,
                    IssueCount: 1,
                    RepairedIssueCount: 0,
                    Issues:
                    [
                        new MirrorRepairIssue(
                            MirrorNodeId: "cloud",
                            MirrorNodeLabel: "Cloud mirror",
                            Severity: RepositoryScrubIssueSeverity.Warning,
                            ArtefactKind: MirrorRepairArtefactKind.Chunk,
                            Path: @"D:\Mirrors\Cloud\chunks\aa\chunk.chunk",
                            VersionId: "version-1",
                            ChunkDigest: "digest-1",
                            Message: "Mirror chunk is missing.",
                            RepairAction: MirrorRepairAction.None)
                    ])
            ],
            Issues: []);
        var response = FluxVaultIpcResponse.WithMirrorRepair(report);

        var roundTrip = FluxVaultIpcSerializer.DeserializeResponse(FluxVaultIpcSerializer.SerializeResponse(response));

        Assert.True(roundTrip.Success);
        Assert.NotNull(roundTrip.MirrorRepair);
        Assert.True(roundTrip.MirrorRepair.IsPreview);
        Assert.Equal("cloud", roundTrip.MirrorRepair.RequestedMirrorNodeId);
        var node = Assert.Single(roundTrip.MirrorRepair.Nodes);
        Assert.Equal("Cloud mirror", node.Label);
        Assert.Equal(MirrorRepairArtefactKind.Chunk, Assert.Single(node.Issues).ArtefactKind);
    }

    [Fact]
    public void Repository_health_response_serialization_preserves_scrub_and_rehearsal_results()
    {
        var scrub = new RepositoryScrubReport(
            CompletedAtUtc: new DateTimeOffset(2026, 4, 30, 10, 0, 0, TimeSpan.Zero),
            HealthState: RepositoryHealthState.Warning,
            ManifestCount: 2,
            CheckedChunkCount: 3,
            IssueCount: 1,
            RepairedIssueCount: 1,
            Issues:
            [
                new RepositoryScrubIssue(
                    RepositoryScrubIssueSeverity.Warning,
                    RepositoryScrubIssueKind.MirrorDrift,
                    @"D:\Vault\chunks\aa\chunk.chunk",
                    "version-1",
                    "chunk-digest",
                    "Mirror chunk was repaired.",
                    RepositoryRepairAction.RepairedMirrorFromPrimary)
            ]);
        var rehearsal = new RestoreRehearsalReport(
            CompletedAtUtc: new DateTimeOffset(2026, 4, 30, 10, 1, 0, TimeSpan.Zero),
            HealthState: RepositoryHealthState.Healthy,
            RequestedVersionCount: 3,
            RehearsedVersionCount: 1,
            FailedVersionCount: 0,
            Results:
            [
                new RestoreRehearsalResult("version-1", @"D:\Work\Docs\brief.docx", true, 128, "Restore rehearsal passed.")
            ]);
        var health = new RepositoryHealthSnapshot(
            CheckedAtUtc: new DateTimeOffset(2026, 4, 30, 10, 2, 0, TimeSpan.Zero),
            OverallState: RepositoryHealthState.Warning,
            Summary: "Repository repaired 1 issue.",
            LastScrub: scrub,
            LastRestoreRehearsal: rehearsal,
            LastMirrorRebalance: new MirrorRebalancePreviewReport(
                CompletedAtUtc: new DateTimeOffset(2026, 5, 1, 9, 0, 0, TimeSpan.Zero),
                HealthState: RepositoryHealthState.Warning,
                CheckedChunkCount: 1,
                ActionCount: 1,
                EstimatedCopyBytes: 512,
                EstimatedDeleteBytes: 0,
                Nodes: [],
                Actions:
                [
                    new MirrorRebalanceAction(
                        MirrorRebalanceActionKind.CopyToMirror,
                        MirrorRebalanceArtefactKind.Chunk,
                        "cloud",
                        "Cloud mirror",
                        @"D:\Mirrors\Cloud\chunks\aa\chunk.chunk",
                        "chunk-digest",
                        512,
                        "Copy required.")
                ]));
        var response = FluxVaultIpcResponse.WithRepositoryHealth(health);

        var roundTrip = FluxVaultIpcSerializer.DeserializeResponse(FluxVaultIpcSerializer.SerializeResponse(response));

        Assert.NotNull(roundTrip.RepositoryHealth);
        Assert.Equal(RepositoryHealthState.Warning, roundTrip.RepositoryHealth.OverallState);
        Assert.Equal(RepositoryRepairAction.RepairedMirrorFromPrimary, Assert.Single(roundTrip.RepositoryHealth.LastScrub!.Issues).RepairAction);
        Assert.True(Assert.Single(roundTrip.RepositoryHealth.LastRestoreRehearsal!.Results).Success);
        Assert.Equal(MirrorRebalanceActionKind.CopyToMirror, Assert.Single(roundTrip.RepositoryHealth.LastMirrorRebalance!.Actions).Action);
    }
}
