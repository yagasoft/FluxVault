using FluxVault.Abstractions.Configuration;
using FluxVault.Abstractions.ChangeTracking;
using FluxVault.Abstractions.Ipc;
using FluxVault.Abstractions.Storage;
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
            ]);
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
    public void Repository_maintenance_request_serialization_preserves_command(FluxVaultIpcCommand command)
    {
        var request = new FluxVaultIpcRequest(command, null, null, null, null);

        var roundTrip = FluxVaultIpcSerializer.DeserializeRequest(FluxVaultIpcSerializer.SerializeRequest(request));

        Assert.Equal(command, roundTrip.Command);
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
            LastRestoreRehearsal: rehearsal);
        var response = FluxVaultIpcResponse.WithRepositoryHealth(health);

        var roundTrip = FluxVaultIpcSerializer.DeserializeResponse(FluxVaultIpcSerializer.SerializeResponse(response));

        Assert.NotNull(roundTrip.RepositoryHealth);
        Assert.Equal(RepositoryHealthState.Warning, roundTrip.RepositoryHealth.OverallState);
        Assert.Equal(RepositoryRepairAction.RepairedMirrorFromPrimary, Assert.Single(roundTrip.RepositoryHealth.LastScrub!.Issues).RepairAction);
        Assert.True(Assert.Single(roundTrip.RepositoryHealth.LastRestoreRehearsal!.Results).Success);
    }
}
