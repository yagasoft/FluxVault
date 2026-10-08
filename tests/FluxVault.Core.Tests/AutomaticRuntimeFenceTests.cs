using FluxVault.Core.Chunking;
using FluxVault.Core.Content;
using FluxVault.Core.Security;
using FluxVault.Core.Storage;

namespace FluxVault.Core.Tests;

public sealed class AutomaticRuntimeFenceTests
{
    [Fact]
    public async Task Lost_runtime_fence_refuses_publication_while_holding_the_repository_lease_and_releases_it()
    {
        using var workspace=TemporaryWorkspace.Create();
        var accepted=RepositoryIntegrityTests.Create(workspace.RepositoryPath);
        var previous=await accepted.CommitAsync(RepositoryIntegrityTests.Request("previous healthy bytes"));
        var checkedUnderLease=false;
        var stale=new FileSystemChunkRepository(workspace.RepositoryPath,new FastCdcChunker(new(128,256,512)),
            new Blake3ContentHasher(),new ZstdChunkCodec(),null,null,null,null,runtimeFence:_=>
            {
                Assert.ThrowsAny<IOException>(() => new FileStream(Path.Combine(workspace.RepositoryPath,".fluxvault.lock"),
                    FileMode.Open,FileAccess.ReadWrite,FileShare.None));
                checkedUnderLease=true;
                throw new AutomaticProtectionUnavailableException("Fixture generation was replaced.");
            });

        await Assert.ThrowsAsync<AutomaticProtectionUnavailableException>(() => stale.CommitAsync(RepositoryIntegrityTests.Request("stale writer bytes")));

        Assert.True(checkedUnderLease);
        Assert.Equal(previous.Manifest.VersionId,Assert.Single(await accepted.ListVersionsAsync()).VersionId);
        var output=Path.Combine(workspace.RootPath,"recovered.txt");
        await accepted.RestoreAsync(previous.Manifest.VersionId,output);
        Assert.Equal("previous healthy bytes",await File.ReadAllTextAsync(output));
    }

    [Fact]
    public async Task Absent_reserved_identity_is_distinct_from_corrupt_published_content()
    {
        using var workspace=TemporaryWorkspace.Create();
        var repository=RepositoryIntegrityTests.Create(workspace.RepositoryPath);
        var reserved=Guid.CreateVersion7().ToString("N");
        var request=RepositoryIntegrityTests.Request("captured bytes") with { ReservedVersionId=reserved };
        var work=new AutomaticPathWork("unused","integrity",workspace.RootPath,request.SourcePath,"unused",1,1,reserved,Guid.NewGuid(),1,null,null,null,null);
        Assert.Null(await repository.VerifyAutomaticResultAsync(work));
        var result=await repository.CommitAsync(request);
        Assert.Equal(reserved,(await repository.VerifyAutomaticResultAsync(work))!.VersionId);
        var chunk=Assert.Single(result.Manifest.Chunks);
        await File.WriteAllBytesAsync(RepositoryIntegrityTests.ChunkPath(workspace.RepositoryPath,chunk.Digest),[1,2,3]);

        await Assert.ThrowsAnyAsync<IOException>(() => repository.VerifyAutomaticResultAsync(work));
    }
}
