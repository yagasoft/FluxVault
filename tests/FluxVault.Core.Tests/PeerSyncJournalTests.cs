using FluxVault.Abstractions.Configuration;
using FluxVault.Abstractions.Sync;
using FluxVault.Core.Sync;

namespace FluxVault.Core.Tests;

public sealed class PeerSyncJournalTests
{
    [Fact]
    public async Task Append_operation_writes_immutable_record_and_updates_peer_head()
    {
        using var workspace = TemporaryWorkspace.Create();
        var local = new DeviceIdentityConfiguration("device-local", "Studio PC", new DateTimeOffset(2026, 5, 1, 8, 0, 0, TimeSpan.Zero));
        var journal = new FilePeerSyncJournal(workspace.RepositoryPath);

        var record = await journal.AppendOperationAsync(local, new PeerOperationDraft(
            PeerOperationKind.VersionCommitted,
            VersionId: "version-1",
            SourcePath: @"D:\Work\Docs\brief.docx",
            ContentSignature: "sig-1"));

        var head = await journal.ReadPeerHeadAsync(local.DeviceId);
        var operations = await journal.ReadOperationsAfterAsync(local.DeviceId, afterSequenceNumber: 0);

        Assert.Equal(1, record.SequenceNumber);
        Assert.Equal(local.DeviceId, record.DeviceId);
        Assert.Equal(record.OperationId, head!.HeadOperationId);
        Assert.Equal(1, head.HeadSequenceNumber);
        var stored = Assert.Single(operations);
        Assert.Equal(record.OperationId, stored.OperationId);
        Assert.Equal("version-1", stored.VersionId);
    }

    [Fact]
    public async Task Append_operation_rejects_duplicate_operation_id()
    {
        using var workspace = TemporaryWorkspace.Create();
        var local = new DeviceIdentityConfiguration("device-local", "Studio PC", new DateTimeOffset(2026, 5, 1, 8, 0, 0, TimeSpan.Zero));
        var journal = new FilePeerSyncJournal(workspace.RepositoryPath);
        var draft = new PeerOperationDraft(
            PeerOperationKind.VersionCommitted,
            VersionId: "version-1",
            SourcePath: @"D:\Work\Docs\brief.docx",
            ContentSignature: "sig-1",
            OperationId: "operation-1");
        await journal.AppendOperationAsync(local, draft);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            journal.AppendOperationAsync(local, draft));

        Assert.Contains("already exists", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Peer_cursor_round_trips_and_filters_operations()
    {
        using var workspace = TemporaryWorkspace.Create();
        var local = new DeviceIdentityConfiguration("device-local", "Studio PC", new DateTimeOffset(2026, 5, 1, 8, 0, 0, TimeSpan.Zero));
        var journal = new FilePeerSyncJournal(workspace.RepositoryPath);
        var first = await journal.AppendOperationAsync(local, new PeerOperationDraft(PeerOperationKind.VersionCommitted, VersionId: "version-1"));
        var second = await journal.AppendOperationAsync(local, new PeerOperationDraft(PeerOperationKind.VersionCommitted, VersionId: "version-2"));

        await journal.SaveCursorAsync(new PeerCursorRecord(local.DeviceId, first.SequenceNumber, first.OperationId, DateTimeOffset.UtcNow));
        var cursor = await journal.ReadCursorAsync(local.DeviceId);
        var remaining = await journal.ReadOperationsAfterAsync(local.DeviceId, cursor!.LastSeenSequenceNumber);

        Assert.Equal(first.OperationId, cursor.LastSeenOperationId);
        var operation = Assert.Single(remaining);
        Assert.Equal(second.OperationId, operation.OperationId);
    }
}
