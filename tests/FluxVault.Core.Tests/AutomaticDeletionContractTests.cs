using FluxVault.Abstractions.Storage;
using FluxVault.Core.Security;

namespace FluxVault.Core.Tests;

public sealed class AutomaticDeletionContractTests
{
    [Fact]
    public async Task Deletion_has_a_reserved_identity_and_reconciles_its_exact_intent_and_lineage()
    {
        using var workspace = TemporaryWorkspace.Create();
        var repository = RepositoryIntegrityTests.Create(workspace.RepositoryPath);
        var captured = await repository.CommitAsync(RepositoryIntegrityTests.Request("retained before deletion"));
        var reserved = Guid.CreateVersion7().ToString("N");
        var result = await repository.RecordDeletionAsync(new("integrity", workspace.RootPath,
            captured.Manifest.SourcePath, false, DateTimeOffset.UtcNow, ReservedVersionId: reserved,
            ExpectedDeletedFromVersionId: captured.Manifest.VersionId));
        Assert.Equal(reserved, result!.Manifest.VersionId);
        var claim = new AutomaticPathWork("unused", "integrity", workspace.RootPath, captured.Manifest.SourcePath,
            "unused", 1, 1, reserved, Guid.NewGuid(), 1, null, null, null, null,
            IsDeletion: true, DeletedFromVersionId: captured.Manifest.VersionId);
        Assert.Equal(reserved, (await repository.VerifyAutomaticResultAsync(claim))!.VersionId);
        await Assert.ThrowsAnyAsync<IOException>(() => repository.VerifyAutomaticResultAsync(claim with { IsDeletion = false }));
        await Assert.ThrowsAnyAsync<IOException>(() => repository.VerifyAutomaticResultAsync(claim with { DeletedFromVersionId = Guid.NewGuid().ToString("N") }));
        var output = Path.Combine(workspace.RootPath, "recovered.txt");
        await repository.RestoreAsync(captured.Manifest.VersionId, output);
        Assert.Equal("retained before deletion", await File.ReadAllTextAsync(output));
    }

    [Fact]
    public async Task Rejected_absence_or_changed_lineage_publishes_no_deletion()
    {
        using var workspace = TemporaryWorkspace.Create();
        var repository = RepositoryIntegrityTests.Create(workspace.RepositoryPath);
        var captured = await repository.CommitAsync(RepositoryIntegrityTests.Request("previous healthy bytes"));
        var request = new RepositoryDeletionRequest("integrity", workspace.RootPath, captured.Manifest.SourcePath,
            false, DateTimeOffset.UtcNow, ReservedVersionId: Guid.CreateVersion7().ToString("N"),
            ExpectedDeletedFromVersionId: captured.Manifest.VersionId,
            BeforePublication: (_, _) => throw new IOException("The source reappeared or its authority ended."));
        await Assert.ThrowsAnyAsync<IOException>(() => repository.RecordDeletionAsync(request));
        await Assert.ThrowsAnyAsync<IOException>(() => repository.RecordDeletionAsync(request with
        { ExpectedDeletedFromVersionId = Guid.NewGuid().ToString("N"), BeforePublication = null }));
        Assert.Equal(captured.Manifest.VersionId, Assert.Single(await repository.ListVersionsAsync()).VersionId);
    }

    [Fact]
    public async Task Deletion_does_not_reuse_an_existing_reserved_identity()
    {
        using var workspace = TemporaryWorkspace.Create();
        var repository = RepositoryIntegrityTests.Create(workspace.RepositoryPath);
        var captured = await repository.CommitAsync(RepositoryIntegrityTests.Request("healthy bytes"));
        await Assert.ThrowsAnyAsync<IOException>(() => repository.RecordDeletionAsync(new("integrity", workspace.RootPath,
            captured.Manifest.SourcePath, false, DateTimeOffset.UtcNow, ReservedVersionId: captured.Manifest.VersionId)));
        Assert.False(Assert.Single(await repository.ListVersionsAsync()).IsDeleted);
    }
}
