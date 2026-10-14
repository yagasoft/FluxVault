using FluxVault.Abstractions.Storage;
using FluxVault.Core.Security;
using FluxVault.Core.Storage;
using FluxVault.Core.Storage.Metadata;
using FluxVault.Core.Chunking;
using Npgsql;
using System.IO;

namespace FluxVault.TestHost;

internal static class VaultAutomaticMirrorProbe
{
    internal static async Task VerifyAsync(PostgreSqlVaultCatalogue catalogue,NpgsqlDataSource source,
        PostgreSqlRepositoryMetadataStore metadata,AutomaticCatalogueState state,List<string> checks)
    {
        var binding=state.Vault.Binding;var folder=state.Vault.Configuration.WatchedFolders.Single();
        var path=Path.Combine(folder.Path,"mirror-cursor-fixture.txt");
        var first=await PublishAsync("one");
        var old=(await catalogue.ListAutomaticMirrorWorkAsync(binding,32)).Single();
        var firstTarget=(await catalogue.FindNextAutomaticMirrorVersionAsync(binding,old))!;
        var second=await PublishAsync("two");
        var extended=(await catalogue.ListAutomaticMirrorWorkAsync(binding,32)).Single();
        Check(first.ReservedVersionId!=second.ReservedVersionId && extended.AfterOutboxId==old.AfterOutboxId && extended.ThroughOutboxId>old.ThroughOutboxId,
            "newer primary captures remain eligible while the original mirror interval stays durable");
        await metadata.ExportOutboxAsync(binding.RepositoryPath);
        Check((await catalogue.ReadAutomaticStateAsync(binding)).MirrorPendingCount==1 &&
            (await catalogue.FindNextAutomaticMirrorVersionAsync(binding,extended))!.VersionId==first.ReservedVersionId,
            "metadata export success neither clears mirror coverage nor skips the older retained publication");
        var repository=new FileSystemChunkRepository(binding,new FastCdcChunker(new()),new(),new(),null,metadata,runtimeFence:catalogue.VerifyRuntimeFenceAsync);
        await repository.RetryAutomaticMirrorVersionAsync(old.ScopeId,old.SourcePath,firstTarget.VersionId,
            (_,token)=>catalogue.AdvanceAutomaticMirrorWorkAsync(binding,old,firstTarget.OutboxId,token));
        var remaining=(await catalogue.ListAutomaticMirrorWorkAsync(binding,32)).Single();
        Check(remaining.ThroughOutboxId==extended.ThroughOutboxId &&
            (await catalogue.FindNextAutomaticMirrorVersionAsync(binding,remaining))!.VersionId==second.ReservedVersionId,
            "acknowledging an older mirror snapshot preserves concurrently extended newer history coverage");
        await catalogue.DelayAutomaticMirrorWorkAsync(binding,remaining,"synthetic mirror failure remains visible",TimeSpan.FromSeconds(15));
        Check((await catalogue.ListAutomaticMirrorWorkAsync(binding,32)).Count==0 &&
            (await catalogue.ReadAutomaticStateAsync(binding)).MirrorReason=="synthetic mirror failure remains visible",
            "failed mirror replication retains durable reason and bounded retry deadline");
        await repository.PurgeAsync(new([new(path,RepositoryPurgeScopeKind.File)]));
        Check(await catalogue.FindNextAutomaticMirrorVersionAsync(binding,remaining) is null,"purged history cannot be selected as a mirror retry donor");
        await repository.RetryAutomaticMirrorVersionAsync(remaining.ScopeId,remaining.SourcePath,null,
            (_,token)=>catalogue.AdvanceAutomaticMirrorWorkAsync(binding,remaining,remaining.ThroughOutboxId,token));
        Check((await catalogue.ReadAutomaticStateAsync(binding)).MirrorPendingCount==0 && (await metadata.ListVersionsAsync()).Count==0,
            "authoritative absence retires the mirror interval without resurrecting deleted history");
        await using var connection=await source.OpenConnectionAsync();
        await using var remove=new NpgsqlCommand("DELETE FROM fv_control.automatic_work WHERE path_key=@key",connection);
        remove.Parameters.AddWithValue("key",first.PathKey);await remove.ExecuteNonQueryAsync();

        async Task<AutomaticPathWork> PublishAsync(string observation)
        {
            await catalogue.QueueAutomaticPathAsync(binding,path,state.ScopeFingerprint,DateTimeOffset.UtcNow.AddMinutes(-1),"mirror fixture publication",new());
            var claim=await catalogue.ClaimAutomaticPathAsync(binding) ?? throw new InvalidOperationException("Mirror backlog blocked a newer primary claim.");
            await catalogue.ObserveAutomaticClaimAsync(binding,claim,observation);
            await metadata.RecordVersionAsync(new(claim.ReservedVersionId!,folder.Id,path,DateTimeOffset.UtcNow,CaptureConsistency.CrashConsistent,0,[],VaultId:binding.Id));
            await catalogue.CompleteAutomaticPathAsync(binding,claim,claim.ReservedVersionId!);return claim;
        }
        void Check(bool condition,string name){if(!condition)throw new InvalidOperationException(name);checks.Add(name);}
    }
}
