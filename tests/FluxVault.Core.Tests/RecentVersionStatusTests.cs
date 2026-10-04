using FluxVault.Abstractions.Capture;
using FluxVault.Abstractions.Configuration;
using FluxVault.Abstractions.Storage;
using FluxVault.Core.Configuration;
using FluxVault.Core.Chunking;
using FluxVault.Core.Content;
using FluxVault.Core.Service;
using FluxVault.Core.Storage;
using FluxVault.Core.Storage.Metadata;

namespace FluxVault.Core.Tests;

public sealed class RecentVersionStatusTests
{
    [Fact]
    public async Task In_memory_recent_versions_preserve_exact_timestamp_and_ordinal_ties()
    {
        var store = new InMemoryRepositoryMetadataStore();
        var captured = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var template = new FileVersionManifest(new string('f', 32), "work", Path.GetFullPath("recent.docx"), captured,
            CaptureConsistency.BestEffort, 0, []);
        await store.RecordVersionsAsync([template, template with { VersionId = new string('0', 32), CapturedAtUtc = captured.AddTicks(1) },
            template with { VersionId = "a" + new string('0', 31) }, template with { VersionId = "B" + new string('0', 31) }]);

        Assert.Equal((await store.ListVersionsAsync()).Take(3), await ((IRepositoryMetadataStore)store).ListRecentVersionsAsync(3));
        Assert.Equal(new string('0', 32), (await ((IRepositoryMetadataStore)store).ListRecentVersionsAsync(1)).Single().VersionId);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => ((IRepositoryMetadataStore)store).ListRecentVersionsAsync(0));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ((IRepositoryMetadataStore)store).ListRecentVersionsAsync(1, new CancellationToken(true)));
    }

    [Fact]
    public async Task Legacy_repository_recent_read_does_not_reenter_its_lease_and_obeys_cancellation()
    {
        using var workspace = TemporaryWorkspace.Create();
        var repository = new FileSystemChunkRepository(workspace.RepositoryPath, new FastCdcChunker(new()), new Blake3ContentHasher(), new ZstdChunkCodec());
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        Assert.Empty(await repository.ListRecentVersionsAsync(1, deadline.Token));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => repository.ListRecentVersionsAsync(-1));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => repository.ListRecentVersionsAsync(1, new CancellationToken(true)));
    }

    [Fact]
    public async Task Overview_status_requests_only_its_existing_fifty_recent_versions_through_the_real_repository()
    {
        using var workspace = TemporaryWorkspace.Create();
        Directory.CreateDirectory(workspace.RepositoryPath);
        var store = new FileFluxVaultConfigurationStore(Path.Combine(workspace.RootPath, "config.json"), workspace.RootPath);
        await store.SaveAsync(FluxVaultConfiguration.CreateDefault(workspace.RootPath) with { RepositoryPath = workspace.RepositoryPath });
        var metadata = new RecentOnlyMetadata(workspace.RootPath);
        var operations = new FluxVaultOperations(store, new NoCapture(), metadataStoreFactory: _ => metadata);

        var status = await operations.GetStatusAsync();
        var cached = await operations.GetStatusAsync();

        Assert.True(status.HasVersionInventory);
        Assert.Equal(50, status.RecentVersions.Count);
        Assert.Equal(metadata.Versions.Take(50).Select(version => version.VersionId), status.RecentVersions.Select(version => version.VersionId));
        Assert.Equal(status.RecentVersions, cached.RecentVersions);
        Assert.Equal([50], metadata.RecentRequests);
        Assert.Equal(0, metadata.FullHistoryReads);
    }

    [Fact]
    public async Task Protected_status_omits_current_inventory_without_reading_it_and_keeps_recent_availability_separate()
    {
        using var workspace = TemporaryWorkspace.Create(); Directory.CreateDirectory(workspace.RepositoryPath);
        var store = new FileFluxVaultConfigurationStore(Path.Combine(workspace.RootPath,"config.json"),workspace.RootPath);
        await store.SaveAsync(FluxVaultConfiguration.CreateDefault(workspace.RootPath) with {RepositoryPath=workspace.RepositoryPath});
        var metadata=new RecentOnlyMetadata(workspace.RootPath){RefuseCurrentRead=true};
        var operations=new FluxVaultOperations(store,new NoCapture(),metadataStoreFactory:_=>metadata,pageCurrentEntriesForStatus:true);
        var full=await operations.GetStatusAsync(); var fast=await operations.GetStatusAsync(FluxVault.Abstractions.Ipc.FluxVaultStatusDetailLevel.Fast);
        Assert.True(full.UsesPagedCurrentEntries); Assert.True(full.HasVersionInventory); Assert.Null(full.TrackedEntries);
        Assert.True(fast.UsesPagedCurrentEntries); Assert.True(fast.HasVersionInventory); Assert.Null(fast.TrackedEntries);
        Assert.Equal(50,full.RecentVersions.Count); Assert.Equal(0,metadata.FullHistoryReads);
    }

    private sealed class RecentOnlyMetadata(string root) : IRepositoryMetadataStore
    {
        internal IReadOnlyList<RepositoryVersionSummary> Versions { get; } = Enumerable.Range(1, 80).Reverse().Select(index =>
            new RepositoryVersionSummary(index.ToString("x32"), Path.Combine(root, "work", "document.docx"),
                new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero).AddMinutes(index), CaptureConsistency.BestEffort, 0, 0)).ToArray();
        internal List<int> RecentRequests { get; } = [];
        internal int FullHistoryReads;
        internal bool RefuseCurrentRead;
        public Task<IReadOnlyList<RepositoryVersionSummary>> ListRecentVersionsAsync(int maximumCount, CancellationToken cancellationToken = default)
        { RecentRequests.Add(maximumCount); return Task.FromResult<IReadOnlyList<RepositoryVersionSummary>>(Versions.Take(maximumCount).ToArray()); }
        public Task<IReadOnlyList<RepositoryVersionSummary>> ListVersionsAsync(CancellationToken cancellationToken = default)
        { FullHistoryReads++; throw new InvalidOperationException("Overview must not load complete historical manifests."); }
        public Task<IReadOnlyList<RepositoryVersionSummary>> ListLatestEntriesAsync(CancellationToken cancellationToken = default) =>
            RefuseCurrentRead ? throw new InvalidOperationException("Protected status must not materialise current inventory.") : Task.FromResult<IReadOnlyList<RepositoryVersionSummary>>([]);
        public Task<ChunkDescriptor?> FindChunkDescriptorAsync(string digest, CancellationToken cancellationToken = default) => Task.FromResult<ChunkDescriptor?>(null);
        public Task RecordVersionAsync(FileVersionManifest manifest, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class NoCapture : IFileCaptureProvider
    {
        public Task<FileCaptureResult> CaptureAsync(FileCaptureRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
