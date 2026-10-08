using FluxVault.Core.Storage.Metadata;

namespace FluxVault.Core.Tests;

public sealed class AutomaticOutboxTests
{
    [Theory]
    [InlineData("different immutable journal payload")]
    [InlineData("{\"versionId\":\"tampered\"}")]
    public async Task Existing_mismatched_export_is_preserved_and_refused_before_acknowledgement(string existing)
    {
        using var workspace=TemporaryWorkspace.Create();
        var journal=Path.Combine(workspace.RepositoryPath,"metadata-journal","fixture");
        Directory.CreateDirectory(journal);
        var path=Path.Combine(journal,"00000000000000000001.fvop");
        await File.WriteAllTextAsync(path,existing);
        var row=new PostgreSqlRepositoryMetadataStore.OutboxRow(1,"fixture","{\"versionId\":\"expected\"}");

        await Assert.ThrowsAsync<InvalidDataException>(() => PostgreSqlRepositoryMetadataStore.WriteOutboxFileAsync(workspace.RepositoryPath,row,CancellationToken.None));

        Assert.Equal(existing,await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task Identical_existing_export_reconciles_lost_acknowledgement_without_rewriting()
    {
        using var workspace=TemporaryWorkspace.Create();
        var row=new PostgreSqlRepositoryMetadataStore.OutboxRow(1,"fixture","{\"versionId\":\"expected\"}");
        var path=await PostgreSqlRepositoryMetadataStore.WriteOutboxFileAsync(workspace.RepositoryPath,row,CancellationToken.None);
        var before=File.GetLastWriteTimeUtc(path);

        Assert.Equal(path,await PostgreSqlRepositoryMetadataStore.WriteOutboxFileAsync(workspace.RepositoryPath,row,CancellationToken.None));
        Assert.Equal(before,File.GetLastWriteTimeUtc(path));
        Assert.Equal(row.PayloadJson,await File.ReadAllTextAsync(path));
    }
}
