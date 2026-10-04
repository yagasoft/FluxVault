using System.Text;
using System.Text.Json;
using FluxVault.Abstractions.Configuration;
using FluxVault.Abstractions.Policies;
using FluxVault.Abstractions.Storage;
using FluxVault.Core.Capture;
using FluxVault.Core.Chunking;
using FluxVault.Core.Configuration;
using FluxVault.Core.Content;
using FluxVault.Core.Service;
using FluxVault.Core.Storage;

namespace FluxVault.Core.Tests;

public sealed class CommittedConfigurationTests
{
    [Fact]
    public async Task Applying_committed_configuration_purges_only_removed_history_without_a_second_save()
    {
        using var workspace = TemporaryWorkspace.Create();
        var committed = FluxVaultConfiguration.CreateDefault(workspace.RootPath) with { RepositoryPath = workspace.RepositoryPath };
        var durable = new FileFluxVaultConfigurationStore(Path.Combine(workspace.RootPath, "config.json"), workspace.RootPath);
        await durable.SaveAsync(committed);
        committed = await durable.LoadAsync();
        var store = new ReadOnlyCommittedStore(durable);
        var repository = new FileSystemChunkRepository(workspace.RepositoryPath, new FastCdcChunker(new()), new Blake3ContentHasher(), new ZstdChunkCodec());
        var removed = await repository.CommitAsync(Request(Path.Combine(workspace.RootPath, "cad", "drawing.dwg"), "removed working bytes"));
        var kept = await repository.CommitAsync(Request(Path.Combine(workspace.RootPath, "office", "document.docx"), "retained working bytes"));
        var operations = new FluxVaultOperations(store, new NormalFileCaptureProvider(), repositoryFactory: _ => repository);

        var purge = await operations.ApplyCommittedConfigurationAsync(committed, true,
            [new(removed.Manifest.SourcePath, RepositoryPurgeScopeKind.File)],
            [new(kept.Manifest.SourcePath, RepositoryPurgeScopeKind.File)]);

        Assert.NotNull(purge);
        Assert.True(purge.Success);
        Assert.Equal(1, purge.PurgedVersionCount);
        Assert.Equal(0, store.SaveCalls);
        Assert.Equal(JsonSerializer.Serialize(committed), JsonSerializer.Serialize(await durable.LoadAsync()));
        Assert.Equal(kept.Manifest.VersionId, Assert.Single(await repository.ListVersionsAsync()).VersionId);
        var recovered = Path.Combine(workspace.RootPath, "recovered.docx");
        await repository.RestoreAsync(kept.Manifest.VersionId, recovered);
        Assert.Equal("retained working bytes", await File.ReadAllTextAsync(recovered));
    }

    [Fact]
    public async Task Applying_committed_configuration_reports_purge_failure_without_undoing_the_save()
    {
        using var workspace = TemporaryWorkspace.Create();
        var durable = new FileFluxVaultConfigurationStore(Path.Combine(workspace.RootPath, "config.json"), workspace.RootPath);
        await durable.SaveAsync(FluxVaultConfiguration.CreateDefault(workspace.RootPath) with { IsEnabled = false });
        var committed = await durable.LoadAsync();
        var store = new ReadOnlyCommittedStore(durable);
        var operations = new FluxVaultOperations(store, new NormalFileCaptureProvider(),
            repositoryFactory: _ => throw new IOException("metadata offline"));

        var purge = await operations.ApplyCommittedConfigurationAsync(committed, true,
            [new(Path.Combine(workspace.RootPath, "cad"), RepositoryPurgeScopeKind.RecursiveFolder)], []);

        Assert.NotNull(purge);
        Assert.False(purge.Success);
        Assert.Contains("metadata offline", purge.ErrorMessage);
        Assert.Equal(0, store.SaveCalls);
        Assert.False((await durable.LoadAsync()).IsEnabled);
        Assert.Contains("Configuration saved, but purge failed", (await operations.GetStatusAsync()).LastMessage);
    }

    [Fact]
    public async Task Cancellation_after_configuration_commit_is_propagated_without_saving_again()
    {
        using var workspace = TemporaryWorkspace.Create();
        var durable = new FileFluxVaultConfigurationStore(Path.Combine(workspace.RootPath, "config.json"), workspace.RootPath);
        await durable.SaveAsync(FluxVaultConfiguration.CreateDefault(workspace.RootPath) with { IsEnabled = false });
        var committed = await durable.LoadAsync();
        var store = new ReadOnlyCommittedStore(durable);
        using var cancellation = new CancellationTokenSource();
        var operations = new FluxVaultOperations(store, new NormalFileCaptureProvider(), repositoryFactory: _ =>
        {
            cancellation.Cancel();
            throw new OperationCanceledException(cancellation.Token);
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operations.ApplyCommittedConfigurationAsync(committed, true,
            [new(Path.Combine(workspace.RootPath, "cad"), RepositoryPurgeScopeKind.RecursiveFolder)], [], cancellation.Token));

        Assert.Equal(0, store.SaveCalls);
        Assert.False((await durable.LoadAsync()).IsEnabled);
    }

    private static FileCommitRequest Request(string path, string content) => new("work", path, DateTimeOffset.UtcNow,
        CaptureConsistency.BestEffort, CompressionPreference.Off, 1, new MemoryStream(Encoding.UTF8.GetBytes(content)));

    private sealed class ReadOnlyCommittedStore(IFluxVaultConfigurationStore inner) : IFluxVaultConfigurationStore
    {
        internal int SaveCalls;
        public Task<FluxVaultConfiguration> LoadAsync(CancellationToken cancellationToken = default) => inner.LoadAsync(cancellationToken);
        public Task SaveAsync(FluxVaultConfiguration configuration, CancellationToken cancellationToken = default)
        {
            SaveCalls++;
            throw new InvalidOperationException("The configuration was already committed by catalogue CAS.");
        }
    }
}
