using System.Text;
using FluxVault.Abstractions.Configuration;
using FluxVault.Abstractions.Policies;
using FluxVault.Abstractions.Security;
using FluxVault.Abstractions.Storage;
using FluxVault.Core.Chunking;
using FluxVault.Core.Content;
using FluxVault.Core.Storage;
using FluxVault.Core.Storage.Integrity;
using FluxVault.Core.Storage.Metadata;

namespace FluxVault.Core.Tests;

public sealed class ReviewedHistoryDeletionTests
{
    [Fact]
    public async Task Incomplete_large_review_refuses_deletion_and_retains_every_version()
    {
        using var workspace=TemporaryWorkspace.Create();
        var repository=Repository(workspace.RepositoryPath);
        var source=Path.Combine(workspace.RootPath,"many-versions.txt");
        for(var index=0;index<=RepositoryBrowsePolicy.MaximumItemsPerPage;index++)await Capture(repository,source,"version "+index);
        var request=Request(source,RepositoryPurgeScopeKind.File);
        var before=await Inventory(repository);
        var preview=await repository.PreviewHistoryDeletionAsync(request);
        Assert.False(preview.IsComplete);
        Assert.False(preview.CanDelete);
        Assert.Equal(RepositoryBrowsePolicy.MaximumItemsPerPage+1,preview.CandidateVersionCount);
        Assert.Equal(RepositoryBrowsePolicy.MaximumItemsPerPage,preview.Candidates.Count);
        await Assert.ThrowsAsync<RepositoryHistoryDeletionRefusedException>(()=>repository.PurgeAsync(Reviewed(request,preview)));
        Assert.Equal(before,await Inventory(repository));
    }

    [Fact]
    public async Task Reviewed_deletion_keeps_shared_chunks_needed_by_unrelated_different_content()
    {
        using var workspace=TemporaryWorkspace.Create();
        var repository=Repository(workspace.RepositoryPath);
        var source=Path.Combine(workspace.RootPath,"retired.txt");
        var shared=new string('X',4096);
        var removed=await Capture(repository,source,shared+"removed tail");
        var kept=await Capture(repository,Path.Combine(workspace.RootPath,"retained.txt"),shared+"retained different tail");
        Assert.NotEmpty(removed.Manifest.Chunks.Select(chunk=>chunk.Digest).Intersect(kept.Manifest.Chunks.Select(chunk=>chunk.Digest)));
        var request=Request(source,RepositoryPurgeScopeKind.File);
        var preview=await repository.PreviewHistoryDeletionAsync(request);
        Assert.True(preview.CanDelete);
        await repository.PurgeAsync(Reviewed(request,preview));
        var output=Path.Combine(workspace.RootPath,"verified.txt");
        await repository.RestoreAsync(kept.Manifest.VersionId,output);
        Assert.Equal(shared+"retained different tail",await File.ReadAllTextAsync(output));
    }
    [Fact]
    public async Task Preview_is_read_only_and_reviewed_folder_deletion_preserves_sources_and_neighbour_recovery()
    {
        using var workspace=TemporaryWorkspace.Create();
        var repository=Repository(workspace.RepositoryPath);
        var retired=Path.Combine(workspace.RootPath,"retired");
        Directory.CreateDirectory(retired);
        var source=Path.Combine(retired,"document.txt");
        await File.WriteAllTextAsync(source,"retired source remains");
        var removed=await Capture(repository,source,await File.ReadAllTextAsync(source),retired);
        var neighbour=await Capture(repository,Path.Combine(workspace.RootPath,"neighbour.txt"),"independent retained bytes");
        var before=await Inventory(repository);
        var request=Request(retired);

        var preview=await repository.PreviewHistoryDeletionAsync(request);

        Assert.Equal(before,await Inventory(repository));
        Assert.True(preview.CanDelete);
        Assert.True(preview.IsComplete);
        Assert.Contains(preview.Candidates,item=>item.VersionId==removed.Manifest.VersionId);
        Assert.Equal(preview.CandidateVersionCount,preview.Candidates.Count);
        var result=await repository.PurgeAsync(Reviewed(request,preview));
        Assert.True(result.Success);
        Assert.Equal(preview.CandidateVersionCount,result.PurgedVersionCount);
        Assert.DoesNotContain((await repository.ListVersionsAsync()),item=>item.SourcePath.StartsWith(retired,StringComparison.OrdinalIgnoreCase));
        Assert.Equal("retired source remains",await File.ReadAllTextAsync(source));
        var output=Path.Combine(workspace.RootPath,"neighbour-recovered.txt");
        await repository.RestoreAsync(neighbour.Manifest.VersionId,output);
        Assert.Equal("independent retained bytes",await File.ReadAllTextAsync(output));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Protected_overlap_or_outside_scope_inherited_dependency_refuses_every_deletion(bool protectedOverlap)
    {
        using var workspace=TemporaryWorkspace.Create();
        var repository=Repository(workspace.RepositoryPath);
        var root=Path.Combine(workspace.RootPath,"retired");
        var original=await Capture(repository,Path.Combine(root,"original.txt"),"shared lineage bytes",root);
        FileCommitResult? copy=null;
        var request=Request(root);
        if(protectedOverlap)request=request with {PreserveScopes=[new(root,RepositoryPurgeScopeKind.RecursiveFolder)]};
        else copy=await Capture(repository,Path.Combine(workspace.RootPath,"outside","copy.txt"),"shared lineage bytes");
        var before=await Inventory(repository);

        var preview=await repository.PreviewHistoryDeletionAsync(request);

        Assert.False(preview.CanDelete);
        Assert.NotEmpty(preview.Warnings);
        if(copy is not null)Assert.Contains(preview.BlockingDependencies,item=>item.VersionId==copy.Manifest.VersionId);
        await Assert.ThrowsAsync<RepositoryHistoryDeletionRefusedException>(()=>repository.PurgeAsync(Reviewed(request,preview)));
        Assert.Equal(before,await Inventory(repository));
        var output=Path.Combine(workspace.RootPath,"still-recoverable.txt");
        await repository.RestoreAsync(copy?.Manifest.VersionId ?? original.Manifest.VersionId,output);
        Assert.Equal("shared lineage bytes",await File.ReadAllTextAsync(output));
    }

    [Fact]
    public async Task A_file_scope_cannot_delete_its_unprotected_parent_folder_snapshot()
    {
        using var workspace=TemporaryWorkspace.Create();
        var repository=Repository(workspace.RepositoryPath);
        var root=Path.Combine(workspace.RootPath,"retired");
        var captured=await Capture(repository,Path.Combine(root,"nested","document.txt"),"folder referenced bytes",root);
        var request=Request(captured.Manifest.SourcePath,RepositoryPurgeScopeKind.File);
        var before=await Inventory(repository);
        var preview=await repository.PreviewHistoryDeletionAsync(request);
        Assert.False(preview.CanDelete);
        Assert.Contains(preview.BlockingDependencies,item=>item.EntryKind==RepositoryEntryKind.Folder && item.SourcePath==root);
        await Assert.ThrowsAsync<RepositoryHistoryDeletionRefusedException>(()=>repository.PurgeAsync(Reviewed(request,preview)));
        Assert.Equal(before,await Inventory(repository));
    }

    [Theory]
    [InlineData("capture")]
    [InlineData("revision")]
    [InlineData("vault")]
    [InlineData("protected")]
    public async Task Changed_content_or_review_binding_is_refused_before_any_deletion(string change)
    {
        using var workspace=TemporaryWorkspace.Create();
        var repository=Repository(workspace.RepositoryPath);
        var root=Path.Combine(workspace.RootPath,"retired");
        var source=Path.Combine(root,"document.txt");
        await Capture(repository,source,"first reviewed version",root);
        var request=Request(root);
        var preview=await repository.PreviewHistoryDeletionAsync(request);
        request=Reviewed(request,preview);
        if(change=="capture")await Capture(repository,source,"new version after preview",root);
        if(change=="revision")request=request with {HistoryDeletionReview=request.HistoryDeletionReview! with {ConfigurationRevision=18}};
        if(change=="vault")request=request with {HistoryDeletionReview=request.HistoryDeletionReview! with {VaultId=VaultId.New()}};
        if(change=="protected")request=request with {PreserveScopes=[new(root,RepositoryPurgeScopeKind.RecursiveFolder)]};
        var before=await Inventory(repository);
        await Assert.ThrowsAsync<RepositoryHistoryChangedException>(()=>repository.PurgeAsync(request));
        Assert.Equal(before,await Inventory(repository));
    }

    private static RepositoryPurgeRequest Request(string path,RepositoryPurgeScopeKind kind=RepositoryPurgeScopeKind.RecursiveFolder)=>
        new([new(path,kind)],[],new(VaultId.New(),17));
    private static RepositoryPurgeRequest Reviewed(RepositoryPurgeRequest request,RepositoryHistoryDeletionPreview preview)=>
        request with {HistoryDeletionReview=request.HistoryDeletionReview! with {Fingerprint=preview.Fingerprint}};
    private static async Task<string[]> Inventory(FileSystemChunkRepository repository)=>
        (await repository.ListVersionsAsync()).Select(item=>item.VersionId).Order().ToArray();
    private static FileSystemChunkRepository Repository(string path)=>new(path,new FastCdcChunker(new ChunkingOptions(128,256,512)),new Blake3ContentHasher(),new ZstdChunkCodec());
    private static Task<FileCommitResult> Capture(FileSystemChunkRepository repository,string source,string payload,string? watchedRoot=null)=>
        repository.CommitAsync(new("history-deletion",source,DateTimeOffset.UtcNow,CaptureConsistency.CrashConsistent,
            CompressionPreference.Off,128,new MemoryStream(Encoding.UTF8.GetBytes(payload)),WatchedFolderPath:watchedRoot));
}
