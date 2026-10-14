using FluxVault.Abstractions.Configuration;
using FluxVault.Abstractions.Storage;
using FluxVault.Core.Storage;

namespace FluxVault.Core.Tests;

public sealed class AutomaticMirrorRetryTests
{
    [Fact]
    public async Task Retry_repairs_only_the_requested_retained_version_and_acknowledges_under_lease()
    {
        using var workspace=TemporaryWorkspace.Create();
        var mirror=Path.Combine(workspace.RootPath,"mirror");
        var repository=RepositoryIntegrityTests.Create(workspace.RepositoryPath,MirrorSetConfiguration.FromLegacyPath(mirror));
        var first=await repository.CommitAsync(RepositoryIntegrityTests.Request("first retained mirror bytes"));
        var second=await repository.CommitAsync(RepositoryIntegrityTests.Request("newer independently retained mirror bytes"));
        var firstChunk=Assert.Single(first.Manifest.Chunks);var secondChunk=Assert.Single(second.Manifest.Chunks);
        File.Delete(RepositoryIntegrityTests.ChunkPath(mirror,firstChunk.Digest));
        File.Delete(RepositoryIntegrityTests.ChunkPath(mirror,secondChunk.Digest));
        var acknowledged=false;
        await repository.RetryAutomaticMirrorVersionAsync("integrity",first.Manifest.SourcePath,first.Manifest.VersionId,(warnings,_)=>
        {
            Assert.Empty(warnings);
            Assert.Throws<IOException>(()=>new FileStream(Path.Combine(workspace.RepositoryPath,".fluxvault.lock"),FileMode.Open,FileAccess.ReadWrite,FileShare.None));
            acknowledged=true;return Task.CompletedTask;
        });
        Assert.True(acknowledged);
        Assert.True(File.Exists(RepositoryIntegrityTests.ChunkPath(mirror,firstChunk.Digest)));
        Assert.False(File.Exists(RepositoryIntegrityTests.ChunkPath(mirror,secondChunk.Digest)));
    }

    [Fact]
    public async Task Corrupt_primary_is_not_used_as_a_mirror_donor_or_acknowledged()
    {
        using var workspace=TemporaryWorkspace.Create();
        var mirror=Path.Combine(workspace.RootPath,"mirror");
        var repository=RepositoryIntegrityTests.Create(workspace.RepositoryPath,MirrorSetConfiguration.FromLegacyPath(mirror));
        var first=await repository.CommitAsync(RepositoryIntegrityTests.Request("verified primary donor required"));
        var chunk=Assert.Single(first.Manifest.Chunks);
        File.Delete(RepositoryIntegrityTests.ChunkPath(mirror,chunk.Digest));
        await File.WriteAllTextAsync(RepositoryIntegrityTests.ChunkPath(workspace.RepositoryPath,chunk.Digest),"synthetic corruption");
        var acknowledged=false;
        await Assert.ThrowsAnyAsync<IOException>(()=>repository.RetryAutomaticMirrorVersionAsync("integrity",first.Manifest.SourcePath,first.Manifest.VersionId,(_,_)=>
        {acknowledged=true;return Task.CompletedTask;}));
        Assert.False(acknowledged);
        Assert.False(File.Exists(RepositoryIntegrityTests.ChunkPath(mirror,chunk.Digest)));
    }

    [Fact]
    public async Task Deleted_version_retires_retry_without_resurrecting_history_or_mirror_bytes()
    {
        using var workspace=TemporaryWorkspace.Create();
        var mirror=Path.Combine(workspace.RootPath,"mirror");
        var repository=RepositoryIntegrityTests.Create(workspace.RepositoryPath,MirrorSetConfiguration.FromLegacyPath(mirror));
        var first=await repository.CommitAsync(RepositoryIntegrityTests.Request("deliberately removed history"));
        await repository.PurgeAsync(new([new(first.Manifest.SourcePath,RepositoryPurgeScopeKind.File)]));
        var acknowledged=false;
        await repository.RetryAutomaticMirrorVersionAsync("integrity",first.Manifest.SourcePath,first.Manifest.VersionId,(warnings,_)=>
        {Assert.Empty(warnings);acknowledged=true;return Task.CompletedTask;});
        Assert.True(acknowledged);
        Assert.Empty(await repository.ListVersionsAsync());
        Assert.False(File.Exists(RepositoryIntegrityTests.ChunkPath(mirror,Assert.Single(first.Manifest.Chunks).Digest)));
    }
}
