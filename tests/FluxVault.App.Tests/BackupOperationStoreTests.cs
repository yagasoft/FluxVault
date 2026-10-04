using System.IO;
using FluxVault.App.Services;

namespace FluxVault.App.Tests;

public sealed class BackupOperationStoreTests
{
    [Fact]
    public void Store_round_trips_and_cannot_overwrite_or_clear_a_different_operation()
    {
        using var fixture = new Files();
        var first = new PendingBackupOperation(Guid.NewGuid(), Guid.NewGuid(), 3);
        var second = first with { OperationId = Guid.NewGuid() };
        fixture.Store.Reserve(first);
        Assert.Equal(first, new FileBackupOperationStore(fixture.Path).Read());
        Assert.Throws<InvalidOperationException>(() => new FileBackupOperationStore(fixture.Path).Reserve(second));
        Assert.Throws<InvalidOperationException>(() => new FileBackupOperationStore(fixture.Path).Clear(second));
        Assert.Equal(first, fixture.Store.Read());
        fixture.Store.Clear(first);
        fixture.Store.Reserve(second);
        Assert.Throws<InvalidOperationException>(() => new FileBackupOperationStore(fixture.Path).Clear(first));
        Assert.Equal(second, fixture.Store.Read());
        fixture.Store.Clear(second);
        Assert.Null(new FileBackupOperationStore(fixture.Path).Read());
    }

    [Fact]
    public async Task Competing_instances_publish_only_one_record_and_never_delete_a_newer_record()
    {
        using var fixture = new Files();
        var operations = Enumerable.Range(0, 8).Select(_ => new PendingBackupOperation(Guid.NewGuid(), Guid.NewGuid(), 3)).ToArray();
        var outcomes = await Task.WhenAll(operations.Select(operation => Task.Run(() => Record.Exception(() => new FileBackupOperationStore(fixture.Path).Reserve(operation)))));
        Assert.Single(outcomes, result => result is null);
        Assert.All(outcomes.Where(result => result is not null), result => Assert.True(result is IOException or InvalidOperationException));
        var original = Assert.IsType<PendingBackupOperation>(fixture.Store.Read());
        fixture.Store.Clear(original);
        var next = new PendingBackupOperation(original.RepositoryId, Guid.NewGuid(), original.Revision);
        fixture.Store.Reserve(next);
        Assert.Throws<InvalidOperationException>(() => new FileBackupOperationStore(fixture.Path).Clear(original));
        Assert.Equal(next, fixture.Store.Read());
    }

    [Theory]
    [InlineData("{")]
    [InlineData("{}")]
    [InlineData("{\"repositoryId\":\"00000000-0000-0000-0000-000000000000\",\"operationId\":\"00000000-0000-0000-0000-000000000000\",\"revision\":1}")]
    [InlineData("{\"revision\":1,\"revision\":2}")]
    public void Corrupt_records_are_preserved_and_refuse_another_backup(string content)
    {
        using var fixture = new Files();
        File.WriteAllText(fixture.Path, content);
        Assert.Throws<InvalidDataException>(() => fixture.Store.Read());
        Assert.Throws<InvalidDataException>(() => fixture.Store.Reserve(new(Guid.NewGuid(), Guid.NewGuid(), 3)));
        Assert.Equal(content, File.ReadAllText(fixture.Path));
    }

    [Fact]
    public void A_busy_cross_session_store_gate_refuses_access_instead_of_racing_the_other_writer()
    {
        using var fixture = new Files();
        using var gate = new FileStream(fixture.Path + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        Assert.Throws<IOException>(() => fixture.Store.Reserve(new(Guid.NewGuid(), Guid.NewGuid(), 3)));
        Assert.False(File.Exists(fixture.Path));
    }

    private sealed class Files : IDisposable
    {
        private readonly string root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "FluxVault.BackupOperation." + Guid.NewGuid().ToString("N"));
        public string Path => System.IO.Path.Combine(root, "pending.json");
        public FileBackupOperationStore Store => new(Path);
        public Files() => Directory.CreateDirectory(root);
        public void Dispose() => Directory.Delete(root, recursive: true);
    }
}
