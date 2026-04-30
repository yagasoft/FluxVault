using FluxVault.Windows.Capture;

namespace FluxVault.Windows.Tests;

public sealed class WindowsVssSnapshotCoordinatorTests
{
    [Fact]
    public async Task Requester_coordinates_writers_and_snapshot_in_ci_testable_order()
    {
        var source = @"D:\Data\db.mdf";
        var volumeRoot = @"D:\";
        var session = new FakeVssBackupSession
        {
            Writers = [new VssWriterEvidence("SqlServerWriter", [@"D:\Data"])],
            SnapshotDeviceObject = @"\\?\GLOBALROOT\Device\HarddiskVolumeShadowCopy42"
        };
        var coordinator = new WindowsVssSnapshotCoordinator(() => session);

        var result = await coordinator.CreateSnapshotAsync(new VssSnapshotRequest(source, volumeRoot));

        Assert.True(result.Success);
        Assert.Equal(session.SnapshotId, result.SnapshotId);
        Assert.Equal(session.SnapshotDeviceObject, result.SnapshotDeviceObject);
        Assert.Equal("SqlServerWriter", Assert.Single(result.Writers).WriterName);
        Assert.Equal(
            [
                "InitializeForBackup",
                "SetContext:0",
                "SetBackupState:False,False,5,False",
                "GatherWriterMetadata",
                "GetWriterMetadata",
                "StartSnapshotSet",
                "AddToSnapshotSet:D:\\",
                "PrepareForBackup",
                "DoSnapshotSet",
                "GatherWriterStatus",
                "GetWriterStatuses",
                $"GetSnapshotDeviceObject:{session.SnapshotId}"
            ],
            session.Calls);

        await result.Cleanup!();

        Assert.Equal(
            [
                "InitializeForBackup",
                "SetContext:0",
                "SetBackupState:False,False,5,False",
                "GatherWriterMetadata",
                "GetWriterMetadata",
                "StartSnapshotSet",
                "AddToSnapshotSet:D:\\",
                "PrepareForBackup",
                "DoSnapshotSet",
                "GatherWriterStatus",
                "GetWriterStatuses",
                $"GetSnapshotDeviceObject:{session.SnapshotId}",
                "BackupComplete",
                $"DeleteSnapshot:{session.SnapshotId}",
                "FreeWriterMetadata",
                "FreeWriterStatus",
                "Dispose"
            ],
            session.Calls);
    }

    [Fact]
    public async Task Writer_status_failure_aborts_deletes_snapshot_and_returns_failure()
    {
        var session = new FakeVssBackupSession
        {
            Writers = [new VssWriterEvidence("SqlServerWriter", [@"D:\Data"])],
            WriterStatuses = [new VssWriterStatus("SqlServerWriter", State: 5, Failure: unchecked((int)0x800423F4))]
        };
        var coordinator = new WindowsVssSnapshotCoordinator(() => session);

        var result = await coordinator.CreateSnapshotAsync(new VssSnapshotRequest(@"D:\Data\db.mdf", @"D:\"));

        Assert.False(result.Success);
        Assert.Contains("SqlServerWriter", result.Message);
        Assert.Contains("0x800423F4", result.Message);
        Assert.DoesNotContain("BackupComplete", session.Calls);
        Assert.Contains("AbortBackup", session.Calls);
        Assert.Contains($"DeleteSnapshot:{session.SnapshotId}", session.Calls);
        Assert.Equal("Dispose", session.Calls[^1]);
    }

    private sealed class FakeVssBackupSession : IWindowsVssBackupSession
    {
        public Guid SnapshotId { get; } = Guid.Parse("3e7601f8-22fb-46d0-8c71-375a579c32c0");

        public string SnapshotDeviceObject { get; init; } = @"\\?\GLOBALROOT\Device\HarddiskVolumeShadowCopy1";

        public IReadOnlyList<VssWriterEvidence> Writers { get; init; } = [];

        public IReadOnlyList<VssWriterStatus> WriterStatuses { get; init; } =
            [new VssWriterStatus("SqlServerWriter", State: 1, Failure: 0)];

        public List<string> Calls { get; } = [];

        public void InitializeForBackup()
        {
            Calls.Add("InitializeForBackup");
        }

        public void SetContext(int context)
        {
            Calls.Add($"SetContext:{context}");
        }

        public void SetBackupState(
            bool selectComponents,
            bool backupBootableSystemState,
            int backupType,
            bool partialFileSupport)
        {
            Calls.Add($"SetBackupState:{selectComponents},{backupBootableSystemState},{backupType},{partialFileSupport}");
        }

        public Task GatherWriterMetadataAsync(CancellationToken cancellationToken)
        {
            Calls.Add("GatherWriterMetadata");
            return Task.CompletedTask;
        }

        public IReadOnlyList<VssWriterEvidence> GetWriterMetadata()
        {
            Calls.Add("GetWriterMetadata");
            return Writers;
        }

        public void StartSnapshotSet()
        {
            Calls.Add("StartSnapshotSet");
        }

        public Guid AddToSnapshotSet(string volumeRoot)
        {
            Calls.Add($"AddToSnapshotSet:{volumeRoot}");
            return SnapshotId;
        }

        public Task PrepareForBackupAsync(CancellationToken cancellationToken)
        {
            Calls.Add("PrepareForBackup");
            return Task.CompletedTask;
        }

        public Task DoSnapshotSetAsync(CancellationToken cancellationToken)
        {
            Calls.Add("DoSnapshotSet");
            return Task.CompletedTask;
        }

        public Task GatherWriterStatusAsync(CancellationToken cancellationToken)
        {
            Calls.Add("GatherWriterStatus");
            return Task.CompletedTask;
        }

        public IReadOnlyList<VssWriterStatus> GetWriterStatuses()
        {
            Calls.Add("GetWriterStatuses");
            return WriterStatuses;
        }

        public string? GetSnapshotDeviceObject(Guid snapshotId)
        {
            Calls.Add($"GetSnapshotDeviceObject:{snapshotId}");
            return SnapshotDeviceObject;
        }

        public Task BackupCompleteAsync(CancellationToken cancellationToken)
        {
            Calls.Add("BackupComplete");
            return Task.CompletedTask;
        }

        public void DeleteSnapshot(Guid snapshotId)
        {
            Calls.Add($"DeleteSnapshot:{snapshotId}");
        }

        public void AbortBackup()
        {
            Calls.Add("AbortBackup");
        }

        public void FreeWriterMetadata()
        {
            Calls.Add("FreeWriterMetadata");
        }

        public void FreeWriterStatus()
        {
            Calls.Add("FreeWriterStatus");
        }

        public ValueTask DisposeAsync()
        {
            Calls.Add("Dispose");
            return ValueTask.CompletedTask;
        }
    }
}
