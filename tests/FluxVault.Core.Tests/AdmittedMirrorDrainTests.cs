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

public sealed class AdmittedMirrorDrainTests
{
    [Fact]
    public async Task Admitted_drain_moves_verified_copies_and_reports_health_without_a_configuration_write()
    {
        using var workspace = TemporaryWorkspace.Create();
        var first = Path.Combine(workspace.RootPath, "first"); var second = Path.Combine(workspace.RootPath, "second");
        var durable = new FileFluxVaultConfigurationStore(Path.Combine(workspace.RootPath, "config.json"), workspace.RootPath);
        await durable.SaveAsync(FluxVaultConfiguration.CreateDefault(workspace.RootPath) with
        { RepositoryPath = workspace.RepositoryPath, MirrorSet = new([new("first", "First", first), new("second", "Second", second)]) });
        var original = await durable.LoadAsync();
        var store = new ReadOnlyStore(durable);
        var repository = new FileSystemChunkRepository(workspace.RepositoryPath, new FastCdcChunker(new()), new Blake3ContentHasher(),
            new ZstdChunkCodec(), original.MirrorSet);
        var bytes = Encoding.UTF8.GetBytes("recoverable working bytes after a drain");
        using var content = new MemoryStream(bytes, writable: false);
        var committed = await repository.CommitAsync(new("work", Path.Combine(workspace.RootPath, "work.docx"), DateTimeOffset.UtcNow,
            CaptureConsistency.CrashConsistent, CompressionPreference.Off, 0, content));
        var digest = Assert.Single(committed.Manifest.Chunks).Digest;
        var firstChunk = ChunkPath(first, digest); var secondChunk = ChunkPath(second, digest);
        File.Delete(secondChunk);
        File.Delete(Path.Combine(second, "chunks", digest[..2], digest + ".json"));
        var state = new FileRepositoryMaintenanceStateStore(Path.Combine(workspace.RootPath, "health.json"));
        var operations = new FluxVaultOperations(store, new NormalFileCaptureProvider(), state, repositoryFactory: _ => repository);

        var report = await operations.RunMirrorDrainRepositoryEffectsAsync("first");

        Assert.True(report.IsCompletedDrain);
        Assert.False(File.Exists(firstChunk)); Assert.True(File.Exists(secondChunk));
        Assert.Equal(0, store.SaveCalls);
        Assert.Equal(JsonSerializer.Serialize(original), JsonSerializer.Serialize(await durable.LoadAsync()));
        Assert.Equal(JsonSerializer.Serialize(report), JsonSerializer.Serialize((await state.LoadAsync()).LastMirrorRebalance));
        var recovered = Path.Combine(workspace.RootPath, "recovered.docx");
        await repository.RestoreAsync(committed.Manifest.VersionId, recovered);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(recovered));
    }

    [Theory]
    [InlineData(false, RepositoryHealthState.Healthy, 0, false, MirrorRebalanceOperation.Drain, "first", true)]
    [InlineData(true, RepositoryHealthState.Healthy, 0, false, MirrorRebalanceOperation.Drain, "first", false)]
    [InlineData(false, RepositoryHealthState.Warning, 0, false, MirrorRebalanceOperation.Drain, "first", false)]
    [InlineData(false, RepositoryHealthState.Healthy, 1, false, MirrorRebalanceOperation.Drain, "first", false)]
    [InlineData(false, RepositoryHealthState.Healthy, 0, true, MirrorRebalanceOperation.Drain, "first", false)]
    [InlineData(false, RepositoryHealthState.Healthy, 0, false, MirrorRebalanceOperation.Placement, "first", false)]
    [InlineData(false, RepositoryHealthState.Healthy, 0, false, MirrorRebalanceOperation.Drain, null, false)]
    public void Completion_requires_a_healthy_applied_drain_with_no_remaining_actions(bool preview, RepositoryHealthState health,
        int count, bool hiddenAction, MirrorRebalanceOperation operation, string? selector, bool complete)
    {
        MirrorRebalanceAction[] actions = hiddenAction ? [new(MirrorRebalanceActionKind.Unresolved, MirrorRebalanceArtefactKind.Chunk,
            "first", "First", "owned path", "digest", 1, "copy unavailable")] : [];
        var report = new MirrorRebalancePreviewReport(DateTimeOffset.UtcNow, health, 1, count, 0, 0, [], actions, operation, preview, selector);
        Assert.Equal(complete, report.IsCompletedDrain);
        Assert.DoesNotContain("isCompletedDrain", JsonSerializer.Serialize(report, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
    }

    private static string ChunkPath(string root, string digest) => Path.Combine(root, "chunks", digest[..2], digest + ".chunk");
    private sealed class ReadOnlyStore(IFluxVaultConfigurationStore inner) : IFluxVaultConfigurationStore
    {
        internal int SaveCalls;
        public Task<FluxVaultConfiguration> LoadAsync(CancellationToken cancellationToken = default) => inner.LoadAsync(cancellationToken);
        public Task SaveAsync(FluxVaultConfiguration configuration, CancellationToken cancellationToken = default)
        { SaveCalls++; throw new InvalidOperationException("Admitted repository execution cannot save configuration."); }
    }
}
