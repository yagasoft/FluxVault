using System.Text.Json;
using FluxVault.Abstractions.Capture;
using FluxVault.Abstractions.Configuration;
using FluxVault.Abstractions.Ipc;
using FluxVault.Abstractions.Security;
using FluxVault.Abstractions.Storage;
using FluxVault.Core.Configuration;
using FluxVault.Core.Service;
using FluxVault.Core.Storage.Metadata;

namespace FluxVault.Core.Tests;

public sealed class AuthorisedDiagnosticsExportTests
{
    [Fact]
    public async Task Real_status_is_streamed_to_owned_target_with_binding_revision_and_publication_warning()
    {
        using var workspace = TemporaryWorkspace.Create();
        var store = new FileFluxVaultConfigurationStore(Path.Combine(workspace.RootPath, "config.json"), workspace.RootPath);
        await store.SaveAsync(FluxVaultConfiguration.CreateDefault(workspace.RootPath) with { IsEnabled = false });
        var repository = RepositoryIntegrityTests.Create(workspace.RepositoryPath);
        await repository.CommitAsync(RepositoryIntegrityTests.Request("diagnostic inventory"));
        var operations = new FluxVaultOperations(store, new NoCapture(), metadataStoreFactory: _ => new InMemoryRepositoryMetadataStore(), repositoryFactory: _ => repository);
        var target = new Target(Path.Combine(workspace.RootPath, "diagnostics.json")) { Warning = "Permissions need review." };
        var identity = VaultId.New();
        var result = await operations.ExportDiagnosticsAsync(target, identity, 7, runtimeNotice: "Manual backup only. ");
        using var document = JsonDocument.Parse(target.Bytes!);
        var root = document.RootElement;
        Assert.Equal(1, root.GetProperty("formatVersion").GetInt32());
        Assert.Equal(identity.ToString(), root.GetProperty("vaultId").GetString());
        Assert.Equal(7, root.GetProperty("configurationRevision").GetInt64());
        Assert.False(root.GetProperty("status").GetProperty("configuration").GetProperty("isEnabled").GetBoolean());
        Assert.Single(root.GetProperty("status").GetProperty("recentVersions").EnumerateArray());
        Assert.StartsWith("Manual backup only.", root.GetProperty("status").GetProperty("lastMessage").GetString());
        Assert.Equal(target.OutputPath, result.OutputPath); Assert.Equal([target.Warning], result.Warnings);
        Assert.Equal(["prepare", "file", "flush", "publish", "dispose"], target.Trace);
        Assert.False(File.Exists(target.OutputPath));
    }

    [Fact]
    public async Task Status_failure_releases_target_without_preparing_or_publishing_output()
    {
        var operations = new FluxVaultOperations(new BrokenStore(), new NoCapture());
        var target = new Target("unused.json");
        await Assert.ThrowsAsync<IOException>(() => operations.ExportDiagnosticsAsync(target, VaultId.New(), 1));
        Assert.Equal(["dispose"], target.Trace);
    }

    [Fact]
    public async Task Cancellation_after_flush_never_publishes_and_releases_output()
    {
        using var workspace = TemporaryWorkspace.Create();
        var store = new FileFluxVaultConfigurationStore(Path.Combine(workspace.RootPath, "config.json"), workspace.RootPath);
        await store.SaveAsync(FluxVaultConfiguration.CreateDefault(workspace.RootPath));
        var operations = new FluxVaultOperations(store, new NoCapture(), metadataStoreFactory: _ => new InMemoryRepositoryMetadataStore(),
            repositoryFactory: _ => RepositoryIntegrityTests.Create(workspace.RepositoryPath));
        using var cancellation = new CancellationTokenSource();
        var target = new Target("unused.json") { OnFlush = cancellation.Cancel };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operations.ExportDiagnosticsAsync(target, VaultId.New(), 1, cancellation.Token));
        Assert.DoesNotContain("publish", target.Trace); Assert.Equal("dispose", target.Trace.Last());
    }

    [Fact]
    public async Task Serialization_write_failure_releases_unpublished_target()
    {
        using var workspace = TemporaryWorkspace.Create();
        var store = new FileFluxVaultConfigurationStore(Path.Combine(workspace.RootPath, "config.json"), workspace.RootPath);
        await store.SaveAsync(FluxVaultConfiguration.CreateDefault(workspace.RootPath));
        var operations = new FluxVaultOperations(store, new NoCapture(), metadataStoreFactory: _ => new InMemoryRepositoryMetadataStore(),
            repositoryFactory: _ => RepositoryIntegrityTests.Create(workspace.RepositoryPath));
        var target = new Target("unused.json") { WriteFails = true };
        await Assert.ThrowsAsync<IOException>(() => operations.ExportDiagnosticsAsync(target, VaultId.New(), 1));
        Assert.Equal(["prepare", "file", "dispose"], target.Trace);
    }

    private sealed class NoCapture : IFileCaptureProvider
    {
        public Task<FileCaptureResult> CaptureAsync(FileCaptureRequest request, CancellationToken cancellationToken = default) => throw new InvalidOperationException("Diagnostics cannot capture files.");
    }
    private sealed class BrokenStore : IFluxVaultConfigurationStore
    {
        public Task<FluxVaultConfiguration> LoadAsync(CancellationToken cancellationToken = default) => throw new IOException("Status unavailable");
        public Task SaveAsync(FluxVaultConfiguration configuration, CancellationToken cancellationToken = default) => throw new InvalidOperationException();
    }
    private sealed class Target(string path) : IRepositoryRestoreTarget
    {
        public string OutputPath => path;
        internal List<string> Trace { get; } = [];
        internal byte[]? Bytes; internal string? Warning; internal Action? OnFlush; internal bool WriteFails;
        public Task PrepareAsync(RepositoryEntryKind kind, CancellationToken cancellationToken) { Assert.Equal(RepositoryEntryKind.File, kind); Trace.Add("prepare"); return Task.CompletedTask; }
        public Task CreateDirectoryAsync(string relativePath, CancellationToken cancellationToken) => throw new InvalidOperationException();
        public Task<Stream> CreateFileAsync(string relativePath, CancellationToken cancellationToken) { Assert.Equal("", relativePath); Trace.Add("file"); return Task.FromResult<Stream>(WriteFails ? new BrokenStream() : new MemoryStream()); }
        public Task FlushFileAsync(Stream stream, CancellationToken cancellationToken) { Bytes = ((MemoryStream)stream).ToArray(); Trace.Add("flush"); OnFlush?.Invoke(); return Task.CompletedTask; }
        public Task<IReadOnlyList<string>> PublishAsync(CancellationToken cancellationToken) { cancellationToken.ThrowIfCancellationRequested(); Trace.Add("publish"); return Task.FromResult<IReadOnlyList<string>>(Warning is null ? [] : [Warning]); }
        public ValueTask DisposeAsync() { Trace.Add("dispose"); return ValueTask.CompletedTask; }
    }
    private sealed class BrokenStream : MemoryStream
    {
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) => ValueTask.FromException(new IOException("Write failed"));
    }
}
