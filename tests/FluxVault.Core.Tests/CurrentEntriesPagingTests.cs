using System.Text.Json;
using FluxVault.Abstractions.Ipc;
using FluxVault.Abstractions.Security;
using FluxVault.Abstractions.Storage;
using FluxVault.Core.Security;
using FluxVault.Core.Storage.Metadata;

namespace FluxVault.Core.Tests;

public sealed class CurrentEntriesPagingTests
{
    [Fact]
    public void Wire_shortening_anchors_the_last_emitted_path_including_terminal_pages()
    {
        var id=VaultId.New(); var query=new RepositoryCurrentEntriesQuery(id,256);
        var rows=new[]{Row(10,1700),Row(20,1700)};
        var page=RepositoryCurrentEntriesPaging.Page(query,7,rows,false);
        var envelope=FluxVaultIpcResponse.Ok() with {VaultId=id,VaultRevision=9,CurrentEntriesPage=page};
        var wire=new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var one=envelope with {CurrentEntriesPage=RepositoryCurrentEntriesPaging.Page(query,7,[rows[0]],true)};
        var limit=JsonSerializer.SerializeToUtf8Bytes(one,wire).Length+32;
        Assert.True(JsonSerializer.SerializeToUtf8Bytes(envelope,wire).Length>limit);
        var response=RepositoryHistoryPaging.BoundResponse(envelope,limit);
        Assert.True(response.Success); var bounded=Assert.IsType<RepositoryCurrentEntriesPage>(response.CurrentEntriesPage);
        Assert.Single(bounded.Entries); Assert.Equal(new RepositoryCurrentEntriesCursor(id,256,7,10),bounded.NextCursor);
        Assert.True(JsonSerializer.SerializeToUtf8Bytes(response,wire).Length<=limit);
    }
    [Fact]
    public void One_oversized_current_header_returns_clean_failure_without_a_page()
    {
        var id=VaultId.New(); var response=RepositoryHistoryPaging.BoundResponse(FluxVaultIpcResponse.Ok() with
            {VaultId=id,VaultRevision=9,CurrentEntriesPage=RepositoryCurrentEntriesPaging.Page(new(id),7,[Row(10,5000)],false)},1500);
        Assert.False(response.Success); Assert.Null(response.CurrentEntriesPage); Assert.Equal(id,response.VaultId); Assert.Equal(9,response.VaultRevision);
    }
    [Fact]
    public void Current_read_requires_history_authority_and_has_no_mutation_receipt()
    {
        Assert.True(VaultCommandPolicy.TryGet(FluxVaultIpcCommand.ListCurrentEntriesPage,out var policy));
        Assert.Equal(VaultPermission.ReadHistory,policy.Permissions); Assert.False(PostgreSqlVaultCatalogue.IsMutation(FluxVaultIpcCommand.ListCurrentEntriesPage));
    }
    private static RepositoryCurrentEntry Row(long id,int quoteCount)=>new(id,new(id.ToString("x32"),@"C:\Work\file"+id,DateTimeOffset.UtcNow,
        CaptureConsistency.BestEffort,0,0,InheritedFromSourcePath:new string('"',quoteCount)));
}
