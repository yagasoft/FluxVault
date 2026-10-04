using System.Text;
using FluxVault.Abstractions.Storage;
using FluxVault.Core.Storage.Integrity;

namespace FluxVault.Core.Tests;

public sealed class SelectionForestRecoveryTests
{
    [Theory]
    [InlineData(false, 1)]
    [InlineData(true, 2)]
    public async Task Depth_limits_count_the_synthetic_root_and_tombstone_recovery_chain(bool deleted, int maximumDepth)
    {
        using var workspace = TemporaryWorkspace.Create();
        var seed = RepositoryIntegrityTests.Create(workspace.RepositoryPath);
        var first = await seed.CommitAsync(RepositoryIntegrityTests.Request("one"));
        var version = first.Manifest.VersionId;
        if (deleted)
            version = (await seed.RecordDeletionAsync(new("integrity", Path.GetDirectoryName(first.Manifest.SourcePath)!,
                first.Manifest.SourcePath, false, DateTimeOffset.UtcNow.AddSeconds(2))))!.Manifest.VersionId;
        var repo = RepositoryPublicationTests.Create(workspace.RepositoryPath, new((_, _) => { }), new(MaxGraphDepth: maximumDepth));
        var target = new Target(Path.Combine(workspace.RootPath, "recovered"));
        var error = await Assert.ThrowsAsync<RepositoryIntegrityException>(() => repo.RestoreFilesAsync([new(version, "file.txt")], target));
        Assert.Equal(RepositoryIntegrityFailure.LimitExceeded, error.Code); Assert.Equal(["dispose"], target.Trace);
    }

    [Fact]
    public async Task Multiple_verified_roots_publish_one_folder_before_lineage_and_retain_warnings()
    {
        using var workspace = TemporaryWorkspace.Create();
        var repo = RepositoryIntegrityTests.Create(workspace.RepositoryPath);
        var first = await repo.CommitAsync(RepositoryIntegrityTests.Request("first file"));
        var second = await repo.CommitAsync(RepositoryIntegrityTests.Request("second file"));
        var target = new Target(Path.Combine(workspace.RootPath, "recovered")) { Warning = "Permissions need review." };
        target.BeforePublish = () => Assert.Empty(Hints(workspace));
        var result = await repo.RestoreFilesAsync([new(first.Manifest.VersionId, "first.txt"), new(second.Manifest.VersionId, Path.Combine("nested", "second.txt"))], target);
        Assert.Equal("first file", Encoding.UTF8.GetString(target.Files["first.txt"]));
        Assert.Equal("second file", Encoding.UTF8.GetString(target.Files[Path.Combine("nested", "second.txt")]));
        Assert.Equal(21, result.VerifiedLogicalBytes); Assert.Equal(2, result.RestoredFileCount);
        Assert.Equal([target.Warning], result.Warnings); Assert.Equal(1, target.PublishCount);
        Assert.Equal(2, Hints(workspace).Length); Assert.True(target.Disposed);
    }

    [Fact]
    public async Task Late_corrupt_root_publishes_no_partial_tree_or_lineage()
    {
        using var workspace = TemporaryWorkspace.Create();
        var repo = RepositoryIntegrityTests.Create(workspace.RepositoryPath);
        var first = await repo.CommitAsync(RepositoryIntegrityTests.Request("first file"));
        var second = await repo.CommitAsync(RepositoryIntegrityTests.Request("last file"));
        var chunk = RepositoryIntegrityTests.ChunkPath(workspace.RepositoryPath, second.Manifest.Chunks[0].Digest);
        var bytes = await File.ReadAllBytesAsync(chunk); bytes[0] ^= 0xff; await File.WriteAllBytesAsync(chunk, bytes);
        var target = new Target(Path.Combine(workspace.RootPath, "recovered"));
        await Assert.ThrowsAnyAsync<IOException>(() => repo.RestoreFilesAsync([new(first.Manifest.VersionId, "first.txt"), new(second.Manifest.VersionId, "last.txt")], target));
        Assert.Equal(0, target.PublishCount); Assert.Empty(Hints(workspace)); Assert.True(target.Disposed);
    }

    [Theory]
    [InlineData("../escape", "second.txt")]
    [InlineData("CON.txt", "second.txt")]
    [InlineData("First.txt", "first.TXT")]
    [InlineData("nested", "nested/second.txt")]
    [InlineData("nested/first.txt", "nested")]
    [InlineData("nested//first.txt", "second.txt")]
    public async Task Unsafe_or_colliding_forest_names_fail_before_preparing_output(string firstPath, string secondPath)
    {
        using var workspace = TemporaryWorkspace.Create();
        var repo = RepositoryIntegrityTests.Create(workspace.RepositoryPath);
        var first = await repo.CommitAsync(RepositoryIntegrityTests.Request("first"));
        var target = new Target(Path.Combine(workspace.RootPath, "recovered"));
        await Assert.ThrowsAnyAsync<IOException>(() => repo.RestoreFilesAsync([new(first.Manifest.VersionId, firstPath), new(first.Manifest.VersionId, secondPath)], target));
        Assert.Equal(["dispose"], target.Trace); Assert.Empty(Hints(workspace));
    }

    [Theory]
    [InlineData("nodes")]
    [InlineData("entries")]
    [InlineData("chunks")]
    [InlineData("depth")]
    [InlineData("metadata")]
    public async Task Aggregate_limits_apply_to_the_whole_forest_and_synthetic_directories(string limit)
    {
        using var workspace = TemporaryWorkspace.Create();
        var seed = RepositoryIntegrityTests.Create(workspace.RepositoryPath);
        var first = await seed.CommitAsync(RepositoryIntegrityTests.Request("one"));
        var limits = limit switch
        {
            "nodes" => new RepositoryIntegrityLimits(MaxGraphNodes: 2),
            "entries" => new RepositoryIntegrityLimits(MaxExpandedEntries: 2),
            "chunks" => new RepositoryIntegrityLimits(MaxExpandedChunkReferences: 1),
            "depth" => new RepositoryIntegrityLimits(MaxGraphDepth: 1),
            _ => new RepositoryIntegrityLimits(MaxRestoreMetadataBytes: 1)
        };
        var repo = RepositoryPublicationTests.Create(workspace.RepositoryPath, new((_, _) => { }), limits);
        var target = new Target(Path.Combine(workspace.RootPath, "recovered"));
        var error = await Assert.ThrowsAsync<RepositoryIntegrityException>(() => repo.RestoreFilesAsync(
            [new(first.Manifest.VersionId, Path.Combine("one", "first.txt")), new(first.Manifest.VersionId, Path.Combine("two", "second.txt"))], target));
        Assert.Equal(RepositoryIntegrityFailure.LimitExceeded, error.Code); Assert.Equal(["dispose"], target.Trace);
    }

    [Fact]
    public async Task Cancellation_after_verified_staging_prevents_publication_and_hints()
    {
        using var workspace = TemporaryWorkspace.Create();
        var repo = RepositoryIntegrityTests.Create(workspace.RepositoryPath);
        var first = await repo.CommitAsync(RepositoryIntegrityTests.Request("first"));
        using var cancellation = new CancellationTokenSource();
        var target = new Target(Path.Combine(workspace.RootPath, "recovered")) { OnFlush = cancellation.Cancel };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => repo.RestoreFilesAsync([new(first.Manifest.VersionId, "first.txt")], target, cancellation.Token));
        Assert.Equal(0, target.PublishCount); Assert.Empty(Hints(workspace)); Assert.True(target.Disposed);
    }

    private static string[] Hints(TemporaryWorkspace workspace) => Directory.Exists(Path.Combine(workspace.RepositoryPath, "lineage", "restore-hints"))
        ? Directory.GetFiles(Path.Combine(workspace.RepositoryPath, "lineage", "restore-hints"), "*", SearchOption.AllDirectories) : [];
    internal sealed class Target(string path) : IRepositoryRestoreTarget
    {
        public string OutputPath => path;
        internal List<string> Trace { get; } = []; internal Dictionary<string, byte[]> Files { get; } = [];
        private readonly Dictionary<Stream, string> streams = [];
        internal int PublishCount; internal bool Disposed; internal string? Warning; internal Action? BeforePublish, OnFlush;
        internal RepositoryEntryKind ExpectedKind = RepositoryEntryKind.Folder;
        public Task PrepareAsync(RepositoryEntryKind kind, CancellationToken cancellationToken) { Assert.Equal(ExpectedKind, kind); Trace.Add("prepare"); return Task.CompletedTask; }
        public Task CreateDirectoryAsync(string relativePath, CancellationToken cancellationToken) { Trace.Add("directory:" + relativePath); return Task.CompletedTask; }
        public Task<Stream> CreateFileAsync(string relativePath, CancellationToken cancellationToken) { Trace.Add("file:" + relativePath); Stream stream = new MemoryStream(); streams.Add(stream, relativePath); return Task.FromResult(stream); }
        public Task FlushFileAsync(Stream file, CancellationToken cancellationToken) { Files[streams[file]] = ((MemoryStream)file).ToArray(); OnFlush?.Invoke(); return Task.CompletedTask; }
        public Task<IReadOnlyList<string>> PublishAsync(CancellationToken cancellationToken) { cancellationToken.ThrowIfCancellationRequested(); BeforePublish?.Invoke(); PublishCount++; return Task.FromResult<IReadOnlyList<string>>(Warning is null ? [] : [Warning]); }
        public ValueTask DisposeAsync() { Disposed = true; Trace.Add("dispose"); return ValueTask.CompletedTask; }
    }
}
