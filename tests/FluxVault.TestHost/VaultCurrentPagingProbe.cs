using System.IO;
using System.Text.Json;
using FluxVault.Abstractions.Ipc;
using FluxVault.Abstractions.Security;
using FluxVault.Abstractions.Storage;
using FluxVault.Core.Storage.Integrity;
using FluxVault.Core.Storage.Metadata;
using FluxVault.Core.Security;
using Npgsql;

namespace FluxVault.TestHost;

internal static class VaultCurrentPagingProbe
{
    internal static async Task<IReadOnlyList<string>> RunAsync(NpgsqlDataSource source, VaultBinding binding,
        IRepositoryMetadataStore store, FileVersionManifest template)
    {
        var checks = new List<string>();
        var root = Path.Combine(Path.GetDirectoryName(template.SourcePath)!, "current-pages");
        var manifests = Enumerable.Range(9000, 13).Select((i,n) => template with
        { VersionId=i.ToString("x32"), SourcePath=Path.Combine(root,"file"+n+".dwg"), CapturedAtUtc=template.CapturedAtUtc.AddTicks(n),
            IsDeleted=n%2==0, LogicalLength=0, Chunks=[] }).ToArray();
        var large = Enumerable.Range(9100,12).Select((i,n) => manifests[0] with { VersionId=i.ToString("x32"),
            SourcePath=Path.Combine(root,"budget","file"+n), InheritedFromSourcePath=new string('"',100_000) }).ToArray();
        var folder = manifests[0] with { VersionId=9200.ToString("x32"), SourcePath=root, EntryKind=RepositoryEntryKind.Folder,
            FolderEntries=Enumerable.Range(1,1000).Select(n=>new FolderVersionEntry("child"+n,Path.Combine(root,"child"+n),RepositoryEntryKind.File,
                manifests[0].VersionId,false,0,template.CapturedAtUtc)).ToArray() };
        var mutation=manifests[0] with {VersionId=9300.ToString("x32"),SourcePath=Path.Combine(root,"after.dwg"),CapturedAtUtc=template.CapturedAtUtc.AddTicks(100)};
        var ids=manifests.Concat(large).Append(folder).Append(mutation).Select(m=>m.VersionId).ToArray();
        var schema='"'+binding.MetadataNamespace+'"';
        await using var connection=await source.OpenConnectionAsync();
        try
        {
            await store.RecordVersionsAsync(manifests.Append(folder).ToArray());
            var query=new RepositoryCurrentEntriesQuery(binding.Id,4);
            var expected=(await store.ListLatestEntriesAsync()).Select(v=>v.VersionId).Order(StringComparer.Ordinal).ToArray();
            var rows=await ReadAll(query);
            Check(rows.Select(r=>r.Version.VersionId).Order(StringComparer.Ordinal).SequenceEqual(expected),"current keysets preserve all entries including tombstones");
            Check(rows.All(r=>r.Version.FolderEntries is null && r.Version.ParentVersionIds is null),"current pages project thin headers without folder children or lineage arrays");
            Check(rows.Zip(rows.Skip(1)).All(pair=>pair.First.PathId<pair.Second.PathId),"current keysets advance strictly through canonical path IDs");
            var first=await store.ListCurrentEntriesPageAsync(query);
            await Invalid(()=>store.ListCurrentEntriesPageAsync(query with { RepositoryId=VaultId.New() }),"current pages refuse another repository binding");
            await Invalid(()=>store.ListCurrentEntriesPageAsync(query with { PageSize=0 }),"current pages refuse invalid counts");
            await Invalid(()=>store.ListCurrentEntriesPageAsync(query with { PageSize=5,Cursor=first.NextCursor }),"current cursor cannot change page size");
            await store.RecordVersionAsync(mutation);
            var stale=false;
            try { await store.ListCurrentEntriesPageAsync(query with { Cursor=first.NextCursor }); }
            catch(RepositoryHistoryChangedException){stale=true;}
            Check(stale,"mutation invalidates current-entry cursors without data");
            await using(var poison=new NpgsqlCommand($"UPDATE {schema}.current_entries SET captured_at_ticks=captured_at_ticks+1 WHERE version_id=@id",connection))
            {
                poison.Parameters.AddWithValue("id",manifests[0].VersionId); await poison.ExecuteNonQueryAsync();
                var refused=false;
                try { await ReadAll(query); } catch(RepositoryIntegrityException){refused=true;}
                finally { poison.CommandText=$"UPDATE {schema}.current_entries SET captured_at_ticks=captured_at_ticks-1 WHERE version_id=@id"; await poison.ExecuteNonQueryAsync(); }
                Check(refused,"selected current pointer mismatch refuses the page");
            }
            await store.RecordVersionsAsync(large);
            var all=await ReadAll(new(binding.Id,256),measureWire:true);
            Check(all.Select(r=>r.Version.VersionId).Distinct(StringComparer.Ordinal).Count()==all.Count,"size-shortened current pages have no holes or duplicates");
            Check(all.Count==(await store.ListLatestEntriesAsync()).Count,"size-shortened current pages retain complete inventory semantics");
            return checks;
        }
        finally
        {
            await store.DeleteVersionsAsync(ids);
            await using var retire=new NpgsqlCommand($"DELETE FROM {schema}.metadata_outbox WHERE version_id=ANY(@ids)",connection);
            retire.Parameters.AddWithValue("ids",ids); await retire.ExecuteNonQueryAsync();
        }
        async Task<List<RepositoryCurrentEntry>> ReadAll(RepositoryCurrentEntriesQuery query,bool measureWire=false)
        {
            var rows=new List<RepositoryCurrentEntry>(); var pages=0;
            do
            {
                if(++pages>100) throw new InvalidOperationException("Current inventory cursor did not finish.");
                var page=await store.ListCurrentEntriesPageAsync(query);
                if(measureWire)
                {
                    var response=RepositoryHistoryPaging.BoundResponse(FluxVaultIpcResponse.Ok() with {VaultId=binding.Id,VaultRevision=1,CurrentEntriesPage=page});
                    Check(response.Success && response.CurrentEntriesPage is not null,"current wire page fits its bound");
                    Check(JsonSerializer.SerializeToUtf8Bytes(response,new JsonSerializerOptions(JsonSerializerDefaults.Web)).Length<=RepositoryHistoryPaging.MaximumResponseBytes,
                        "current wire measurement includes authenticated envelope and escaping");
                    page=response.CurrentEntriesPage!;
                }
                Check(page.Entries.Count<=query.PageSize && page.Entries.Count>0,"current page count is bounded and advances");
                rows.AddRange(page.Entries); query=query with {Cursor=page.NextCursor};
                if(page.NextCursor is null) break;
            }while(true);
            return rows;
        }
        async Task Invalid(Func<Task> action,string name){var refused=false;try{await action();}catch(ArgumentException){refused=true;}Check(refused,name);}
        void Check(bool condition,string name){if(!condition)throw new InvalidOperationException("Current paging contract failed: "+name);checks.Add(name);}
    }
}
