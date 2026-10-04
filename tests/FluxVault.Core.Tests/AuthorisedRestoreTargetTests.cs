using System.Text;
using System.Text.Json;
using FluxVault.Abstractions.Storage;

namespace FluxVault.Core.Tests;

public sealed class AuthorisedRestoreTargetTests
{
    [Fact]
    public async Task Owned_preview_returns_verified_result_and_publication_warnings()
    {
        using var workspace = TemporaryWorkspace.Create();
        var repo = RepositoryIntegrityTests.Create(workspace.RepositoryPath);
        var captured = await repo.CommitAsync(RepositoryIntegrityTests.Request("preview bytes"));
        var target = new Target(Path.Combine(workspace.RootPath, "preview.txt")) { Warning = "Published with incomplete permissions." };
        var result = await repo.RestorePreviewAsync(captured.Manifest.VersionId, target);
        Assert.Equal(target.OutputPath, result.OutputPath);
        Assert.Equal(13, result.VerifiedLogicalBytes);
        Assert.Equal([target.Warning], result.Warnings);
        Assert.Equal("preview bytes", Encoding.UTF8.GetString(target.Files[""]));
        Assert.True(target.Published);
        Assert.True(target.Disposed);
    }

    [Fact]
    public async Task Owned_preview_refuses_folder_before_preparing_any_output()
    {
        using var workspace = TemporaryWorkspace.Create();
        var (repo, folder, _) = await FolderRestoreIntegrityTests.Seed(workspace);
        var target = new Target(Path.Combine(workspace.RootPath, "preview"));
        await Assert.ThrowsAsync<InvalidDataException>(() => repo.RestorePreviewAsync(folder.VersionId, target));
        Assert.Equal(["dispose"], target.Trace);
        Assert.False(target.Published);
    }

    [Fact]
    public async Task Verified_file_uses_only_owned_target_and_reports_published_permission_warning()
    {
        using var workspace = TemporaryWorkspace.Create();
        var repo = RepositoryIntegrityTests.Create(workspace.RepositoryPath);
        var captured = await repo.CommitAsync(RepositoryIntegrityTests.Request("protected bytes"));
        var target = new Target(Path.Combine(workspace.RootPath, "output.txt")) { Warning = "Published with incomplete permissions." };
        var result = await repo.RestoreAsync(captured.Manifest.VersionId, target);
        Assert.Equal("protected bytes", Encoding.UTF8.GetString(target.Files[""]));
        Assert.Equal(["prepare:File", "file:", "flush", "publish", "dispose"], target.Trace);
        Assert.Equal(15, result.VerifiedLogicalBytes);
        Assert.Equal(1, result.RestoredFileCount);
        Assert.Equal([target.Warning], result.Warnings);
        Assert.False(File.Exists(target.OutputPath));
    }

    [Fact]
    public async Task Folder_graph_is_recovered_through_owned_target_without_privileged_path_writes()
    {
        using var workspace = TemporaryWorkspace.Create();
        var (repo, folder, _) = await FolderRestoreIntegrityTests.Seed(workspace, twoFiles: true);
        var target = new Target(Path.Combine(workspace.RootPath, "output-folder"));
        var result = await repo.RestoreAsync(folder.VersionId, target);
        Assert.Equal("first child", Encoding.UTF8.GetString(target.Files["a.txt"]));
        Assert.Equal("last child", Encoding.UTF8.GetString(target.Files["z.txt"]));
        Assert.Equal(2, result.RestoredFileCount);
        Assert.True(target.Published);
        Assert.True(target.Disposed);
        Assert.False(Directory.Exists(target.OutputPath));
    }

    [Fact]
    public async Task Late_corruption_never_publishes_partially_written_tree()
    {
        using var workspace = TemporaryWorkspace.Create();
        var (repo, folder, last) = await FolderRestoreIntegrityTests.Seed(workspace, twoFiles: true);
        var chunk = RepositoryIntegrityTests.ChunkPath(workspace.RepositoryPath, last.Chunks[0].Digest);
        var bytes = await File.ReadAllBytesAsync(chunk);
        bytes[0] ^= 0xff;
        await File.WriteAllBytesAsync(chunk, bytes);
        var target = new Target(Path.Combine(workspace.RootPath, "output"));
        await Assert.ThrowsAnyAsync<IOException>(() => repo.RestoreAsync(folder.VersionId, target));
        Assert.False(target.Published);
        Assert.True(target.Disposed);
        Assert.Equal("first child", Encoding.UTF8.GetString(target.Files["a.txt"]));
    }

    [Fact]
    public async Task Invalid_folder_graph_is_rejected_before_staging_and_releases_target()
    {
        using var workspace = TemporaryWorkspace.Create();
        var (repo, folder, _) = await FolderRestoreIntegrityTests.Seed(workspace);
        var path = Directory.EnumerateFiles(Path.Combine(workspace.RepositoryPath, "manifests"), "*.json").Single(p => Path.GetFileNameWithoutExtension(p) == folder.VersionId);
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(folder with { LogicalLength = folder.LogicalLength + 1 }, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        var target = new Target(Path.Combine(workspace.RootPath, "output"));
        await Assert.ThrowsAnyAsync<IOException>(() => repo.RestoreAsync(folder.VersionId, target));
        Assert.Equal(["dispose"], target.Trace);
    }

    [Fact]
    public async Task Cancellation_after_staging_prevents_publication_and_disposes_owned_target()
    {
        using var workspace = TemporaryWorkspace.Create();
        var repo = RepositoryIntegrityTests.Create(workspace.RepositoryPath);
        var captured = await repo.CommitAsync(RepositoryIntegrityTests.Request("protected bytes"));
        using var cancelled = new CancellationTokenSource();
        var target = new Target(Path.Combine(workspace.RootPath, "output")) { OnFlush = cancelled.Cancel };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => repo.RestoreAsync(captured.Manifest.VersionId, target, cancelled.Token));
        Assert.False(target.Published);
        Assert.True(target.Disposed);
    }

    private sealed class Target(string path) : IRepositoryRestoreTarget
    {
        public string OutputPath => path;
        internal List<string> Trace { get; } = [];
        internal Dictionary<string, byte[]> Files { get; } = [];
        private readonly Dictionary<Stream, string> streams = [];
        internal bool Published, Disposed;
        internal string? Warning;
        internal Action? OnFlush;
        public Task PrepareAsync(RepositoryEntryKind kind, CancellationToken cancellationToken) { Trace.Add("prepare:" + kind); return Task.CompletedTask; }
        public Task CreateDirectoryAsync(string relativePath, CancellationToken cancellationToken) { Trace.Add("directory:" + relativePath); return Task.CompletedTask; }
        public Task<Stream> CreateFileAsync(string relativePath, CancellationToken cancellationToken)
        {
            Trace.Add("file:" + relativePath); Stream stream = new MemoryStream(); streams.Add(stream, relativePath); return Task.FromResult(stream);
        }
        public Task FlushFileAsync(Stream file, CancellationToken cancellationToken)
        {
            Files.Add(streams[file], ((MemoryStream)file).ToArray()); Trace.Add("flush"); OnFlush?.Invoke(); return Task.CompletedTask;
        }
        public Task<IReadOnlyList<string>> PublishAsync(CancellationToken cancellationToken)
        {
            Published = true; Trace.Add("publish"); return Task.FromResult<IReadOnlyList<string>>(Warning is null ? [] : [Warning]);
        }
        public ValueTask DisposeAsync() { Disposed = true; Trace.Add("dispose"); return ValueTask.CompletedTask; }
    }
}
