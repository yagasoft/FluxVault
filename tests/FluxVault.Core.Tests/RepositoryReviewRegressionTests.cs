using System.Text;
using System.Text.Json;
using FluxVault.Abstractions.Configuration;
using FluxVault.Abstractions.Policies;
using FluxVault.Abstractions.Storage;
using FluxVault.Core.Chunking;
using FluxVault.Core.Content;
using FluxVault.Core.Storage;
using FluxVault.Core.Storage.Integrity;
using FluxVault.Core.Storage.Metadata;

namespace FluxVault.Core.Tests;

public sealed class RepositoryReviewRegressionTests
{
    [Theory]
    [InlineData("purge", "version")]
    [InlineData("retention", "version")]
    [InlineData("purge", "digest")]
    [InlineData("retention", "digest")]
    [InlineData("purge", "conflict")]
    [InlineData("retention", "conflict")]
    public async Task Maintenance_validates_all_inputs_before_any_deletion(string operation, string damage)
    {
        using var workspace = TemporaryWorkspace.Create();
        var inner = new InMemoryRepositoryMetadataStore();
        var repo = Create(workspace.RepositoryPath, inner);
        var now = DateTimeOffset.UtcNow;
        var first = await repo.CommitAsync(RepositoryIntegrityTests.Request("first bytes") with { CapturedAtUtc = now.AddDays(-10) });
        var second = await repo.CommitAsync(RepositoryIntegrityTests.Request("later bytes") with { CapturedAtUtc = now });
        var externalKey = Path.Combine(workspace.RootPath, "outside");
        await File.WriteAllTextAsync(externalKey + ".json", "external manifest");
        await File.WriteAllTextAsync(externalKey + ".chunk", "external payload");
        var damaged = first.Manifest with { ContentSignature = null };
        damaged = damage switch
        {
            "version" => damaged with { VersionId = externalKey },
            "digest" => damaged with { Chunks = [damaged.Chunks[0] with { Digest = externalKey }] },
            _ => damaged with { Chunks = [second.Manifest.Chunks[0] with { StoredLength = 1 }] }
        };
        var store = new ReadInterceptor(inner) { Listed = [second.Manifest, damaged] };
        repo = Create(workspace.RepositoryPath, store);
        var before = Directory.EnumerateFiles(workspace.RepositoryPath, "*", SearchOption.AllDirectories)
            .ToDictionary(path => path, File.ReadAllBytes);
        if (operation == "purge")
            await Assert.ThrowsAsync<RepositoryIntegrityException>(() => repo.PurgeAsync(new([new(first.Manifest.SourcePath, RepositoryPurgeScopeKind.File)])));
        else
            await Assert.ThrowsAsync<RepositoryIntegrityException>(() => repo.ApplyRetentionAsync(new(true, TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero, 1), now));
        Assert.Equal(0, store.DeleteCalls);
        Assert.Equal(2, (await inner.ListVersionsAsync()).Count);
        foreach (var (path, bytes) in before) Assert.Equal(bytes, await File.ReadAllBytesAsync(path));
        Assert.Equal("external manifest", await File.ReadAllTextAsync(externalKey + ".json"));
        Assert.Equal("external payload", await File.ReadAllTextAsync(externalKey + ".chunk"));
    }

    [Fact]
    public async Task Restore_preserves_a_destination_created_while_the_root_manifest_is_loading()
    {
        using var workspace = TemporaryWorkspace.Create();
        var inner = new InMemoryRepositoryMetadataStore();
        var captured = await Create(workspace.RepositoryPath, inner).CommitAsync(RepositoryIntegrityTests.Request("protected"));
        var destination = Path.Combine(workspace.RootPath, "late.txt");
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var store = new ReadInterceptor(inner) { BeforeRead = async () => { entered.SetResult(); await resume.Task; } };
        var restore = Create(workspace.RepositoryPath, store).RestoreAsync(captured.Manifest.VersionId, destination);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await File.WriteAllTextAsync(destination, "new work");
        resume.SetResult();
        await Assert.ThrowsAnyAsync<IOException>(() => restore);
        Assert.Equal("new work", await File.ReadAllTextAsync(destination));
    }

    [Fact]
    public async Task A_stale_signature_rejects_reordered_valid_chunks()
    {
        using var workspace = TemporaryWorkspace.Create();
        var repo = RepositoryIntegrityTests.Create(workspace.RepositoryPath);
        var a = (await repo.CommitAsync(RepositoryIntegrityTests.Request("aaaa"))).Manifest;
        var b = (await repo.CommitAsync(RepositoryIntegrityTests.Request("bbbb"))).Manifest;
        var combined = a with { LogicalLength = 8, Chunks = [a.Chunks[0], b.Chunks[0] with { Offset = 4 }], ContentSignature = null };
        combined = combined with { ContentSignature = RepositoryMetadataStoreHelpers.GetContentSignature(combined) };
        await FolderRestoreIntegrityTests.ReplaceManifest(workspace, combined);
        var output = Path.Combine(workspace.RootPath, "restored.txt");
        await repo.RestoreAsync(combined.VersionId, output);
        Assert.Equal("aaaabbbb", await File.ReadAllTextAsync(output));
        await FolderRestoreIntegrityTests.ReplaceManifest(workspace, combined with { Chunks = [b.Chunks[0], a.Chunks[0] with { Offset = 4 }] });
        await Assert.ThrowsAsync<RepositoryIntegrityException>(() => repo.RestoreAsync(combined.VersionId, output));
        Assert.Equal("aaaabbbb", await File.ReadAllTextAsync(output));
    }

    [Theory]
    [InlineData(ChunkEncoding.Zstd, true)]
    [InlineData(ChunkEncoding.Zstd, false)]
    [InlineData(ChunkEncoding.Lz4, true)]
    [InlineData(ChunkEncoding.Lz4, false)]
    [InlineData(ChunkEncoding.Brotli, true)]
    [InlineData(ChunkEncoding.Brotli, false)]
    [InlineData(ChunkEncoding.Lzma, true)]
    [InlineData(ChunkEncoding.Lzma, false)]
    public async Task Compressed_corruption_is_reported_or_repaired_from_a_verified_donor(ChunkEncoding encoding, bool hasDonor)
    {
        using var workspace = TemporaryWorkspace.Create();
        var mirror = Path.Combine(workspace.RootPath, "mirror");
        var set = hasDonor ? MirrorSetConfiguration.FromLegacyPath(mirror) : null;
        var repo = RepositoryIntegrityTests.Create(workspace.RepositoryPath, set);
        var text = new string('a', 200);
        var captured = await repo.CommitAsync(RepositoryIntegrityTests.Request(text) with { Compression = encoding switch { ChunkEncoding.Zstd => CompressionPreference.Zstd, ChunkEncoding.Lz4 => CompressionPreference.Lz4, ChunkEncoding.Brotli => CompressionPreference.Brotli, _ => CompressionPreference.Lzma }, MinimumCompressionBytes = 1 });
        var chunk = Assert.Single(captured.Manifest.Chunks);
        Assert.Equal(encoding, chunk.Encoding);
        var bytes = await File.ReadAllBytesAsync(RepositoryIntegrityTests.ChunkPath(workspace.RepositoryPath, chunk.Digest));
        Array.Fill(bytes, (byte)0xff);
        await File.WriteAllBytesAsync(RepositoryIntegrityTests.ChunkPath(workspace.RepositoryPath, chunk.Digest), bytes);
        var report = await repo.ScrubAsync(true);
        Assert.Equal(hasDonor ? RepositoryHealthState.Healthy : RepositoryHealthState.Critical, report.HealthState);
        if (hasDonor)
        {
            var output = Path.Combine(workspace.RootPath, "restored.txt");
            await repo.RestoreAsync(captured.Manifest.VersionId, output);
            Assert.Equal(text, await File.ReadAllTextAsync(output));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Scrub_checks_required_mirrors_after_repairing_the_primary(bool insufficientHeadroom)
    {
        using var workspace = TemporaryWorkspace.Create();
        var set = new MirrorSetConfiguration([new("a", "A", Path.Combine(workspace.RootPath, "a"), true),
            new("b", "B", Path.Combine(workspace.RootPath, "b"), true)]);
        var repo = RepositoryIntegrityTests.Create(workspace.RepositoryPath, set);
        var captured = await repo.CommitAsync(RepositoryIntegrityTests.Request("protected"));
        var digest = captured.Manifest.Chunks[0].Digest;
        foreach (var root in new[] { workspace.RepositoryPath, set.Nodes[1].Path })
            await File.WriteAllTextAsync(RepositoryIntegrityTests.ChunkPath(root, digest), "corrupted");
        if (insufficientHeadroom)
        {
            set = set with { Nodes = [set.Nodes[0], set.Nodes[1] with { CapacityBudgetBytes = Directory.EnumerateFiles(set.Nodes[1].Path, "*", SearchOption.AllDirectories).Sum(path => new FileInfo(path).Length) }] };
            repo = RepositoryIntegrityTests.Create(workspace.RepositoryPath, set);
        }
        var report = await repo.ScrubAsync(true);
        if (insufficientHeadroom)
        {
            Assert.NotEqual(RepositoryHealthState.Healthy, report.HealthState);
            Assert.Contains(report.Issues, issue => issue.RepairAction == RepositoryRepairAction.None);
        }
        else
        {
            Assert.Equal(RepositoryHealthState.Healthy, report.HealthState);
            Assert.Contains(report.Issues, issue => issue.RepairAction == RepositoryRepairAction.RepairedMirrorFromPrimary);
            Assert.Equal("protected", await File.ReadAllTextAsync(RepositoryIntegrityTests.ChunkPath(set.Nodes[1].Path, digest)));
        }
    }

    [Fact]
    public void Zstd_rejects_a_declared_window_larger_than_the_limit_before_decoding()
    {
        // Valid non-single-segment Zstandard frame, raw final block "abc", 64 MiB window.
        byte[] frame = [0x28, 0xb5, 0x2f, 0xfd, 0, 0x80, 0x19, 0, 0, 97, 98, 99];
        var error = Assert.Throws<RepositoryIntegrityException>(() => new ZstdChunkCodec().Decompress(frame, 3, ChunkEncoding.Zstd));
        Assert.Equal(RepositoryIntegrityFailure.LimitExceeded, error.Code);
    }

    [Theory]
    [InlineData(ChunkEncoding.Raw)]
    [InlineData(ChunkEncoding.Zstd)]
    [InlineData(ChunkEncoding.Lz4)]
    [InlineData(ChunkEncoding.Brotli)]
    [InlineData(ChunkEncoding.Lzma)]
    public void Decoder_rejects_truncated_payloads(ChunkEncoding encoding)
    {
        var codec = new ZstdChunkCodec();
        var encoded = codec.Compress(Encoding.UTF8.GetBytes(new string('x', 200)), encoding, 3);
        var error = Assert.Throws<RepositoryIntegrityException>(() => codec.Decompress(encoded.AsSpan(0, encoded.Length / 2), 200, encoding));
        Assert.Equal(RepositoryIntegrityFailure.CorruptObject, error.Code);
    }

    [Fact]
    public async Task Reused_graph_nodes_consume_the_cumulative_metadata_budget_on_every_expansion()
    {
        using var workspace = TemporaryWorkspace.Create();
        var (_, folder, child) = await FolderRestoreIntegrityTests.Seed(workspace);
        var first = folder.FolderEntries![0];
        folder = folder with { LogicalLength = child.LogicalLength * 2, ContentSignature = null, FolderEntries = [first, first with { Name = "second.txt" }] };
        var destination = Path.Combine(workspace.RootPath, "restored");
        long limit = RepositoryManifestSize.Measure(folder) + 2L * destination.Length +
            2L * RepositoryManifestSize.Measure(child) +
            2L * Path.Combine(destination, "a.txt").Length + 2L * Path.Combine(destination, "second.txt").Length - 1;
        var limits = new RepositoryIntegrityLimits(MaxRestoreMetadataBytes: limit);
        var reader = new VerifiedChunkReader(new Blake3ContentHasher(), new ZstdChunkCodec(), limits);
        var reads = 0;
        var graph = new RestoreGraphValidator(reader, limits, (_, _) => { reads++; return Task.FromResult(child); });
        var error = await Assert.ThrowsAsync<RepositoryIntegrityException>(() => graph.BuildAsync(folder, destination, default));
        Assert.Equal(RepositoryIntegrityFailure.LimitExceeded, error.Code);
        Assert.Equal(1, reads);
        Assert.False(Directory.Exists(destination));
    }

    [Theory]
    [InlineData(ChunkEncoding.Raw)]
    [InlineData(ChunkEncoding.Zstd)]
    [InlineData(ChunkEncoding.Lz4)]
    [InlineData(ChunkEncoding.Brotli)]
    [InlineData(ChunkEncoding.Lzma)]
    public void Oversized_declared_output_is_rejected_without_a_large_allocation(ChunkEncoding encoding)
    {
        var codec = new ZstdChunkCodec();
        var before = GC.GetAllocatedBytesForCurrentThread();
        var error = Assert.Throws<RepositoryIntegrityException>(() => codec.Decompress([1], int.MaxValue, encoding));
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(RepositoryIntegrityFailure.LimitExceeded, error.Code);
        Assert.True(allocated < 64 * 1024, $"Oversized declared output allocated {allocated} bytes.");
    }

    [Fact]
    public async Task Maintenance_rejects_a_legacy_manifest_whose_identity_disagrees_with_its_filename()
    {
        using var workspace = TemporaryWorkspace.Create();
        var repo = RepositoryIntegrityTests.Create(workspace.RepositoryPath);
        var captured = await repo.CommitAsync(RepositoryIntegrityTests.Request("protected"));
        var path = Path.Combine(workspace.RepositoryPath, "manifests", captured.Manifest.VersionId + ".json");
        var changed = captured.Manifest with { VersionId = Guid.NewGuid().ToString("N") };
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(changed, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        await Assert.ThrowsAsync<RepositoryIntegrityException>(() => repo.PurgeAsync(new([new(changed.SourcePath, RepositoryPurgeScopeKind.File)])));
        Assert.True(File.Exists(path));
        Assert.Equal("protected", await File.ReadAllTextAsync(RepositoryIntegrityTests.ChunkPath(workspace.RepositoryPath, changed.Chunks[0].Digest)));
    }

    [Fact]
    public async Task A_store_without_deletion_support_cannot_silently_delete_referenced_files()
    {
        using var workspace = TemporaryWorkspace.Create();
        var inner = new InMemoryRepositoryMetadataStore();
        var captured = await Create(workspace.RepositoryPath, inner).CommitAsync(RepositoryIntegrityTests.Request("protected"));
        var repo = Create(workspace.RepositoryPath, new ReadOnlyMetadataStore(inner));
        await Assert.ThrowsAsync<NotSupportedException>(() => repo.PurgeAsync(new([new(captured.Manifest.SourcePath, RepositoryPurgeScopeKind.File)])));
        Assert.Single(await inner.ListVersionsAsync());
        Assert.Equal("protected", await File.ReadAllTextAsync(RepositoryIntegrityTests.ChunkPath(workspace.RepositoryPath, captured.Manifest.Chunks[0].Digest)));
    }

    private sealed class ReadOnlyMetadataStore(InMemoryRepositoryMetadataStore inner) : IRepositoryMetadataStore
    {
        public Task RecordVersionAsync(FileVersionManifest manifest, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ChunkDescriptor?> FindChunkDescriptorAsync(string digest, CancellationToken cancellationToken = default) => inner.FindChunkDescriptorAsync(digest, cancellationToken);
        public Task<IReadOnlyList<FileVersionManifest>> ListManifestsAsync(CancellationToken cancellationToken = default) => inner.ListManifestsAsync(cancellationToken);
        public Task<IReadOnlyList<RepositoryVersionSummary>> ListVersionsAsync(CancellationToken cancellationToken = default) => inner.ListVersionsAsync(cancellationToken);
        public Task<IReadOnlyList<RepositoryVersionSummary>> ListLatestEntriesAsync(CancellationToken cancellationToken = default) => inner.ListLatestEntriesAsync(cancellationToken);
    }

    [Theory]
    [InlineData("scrub")]
    [InlineData("repair")]
    public async Task Manifest_repair_refuses_a_donor_with_the_wrong_identity(string operation)
    {
        using var workspace = TemporaryWorkspace.Create();
        var mirror = Path.Combine(workspace.RootPath, "mirror");
        var repo = RepositoryIntegrityTests.Create(workspace.RepositoryPath, MirrorSetConfiguration.FromLegacyPath(mirror));
        var captured = await repo.CommitAsync(RepositoryIntegrityTests.Request("protected"));
        var name = Path.Combine("manifests", captured.Manifest.VersionId + ".json");
        await File.WriteAllTextAsync(Path.Combine(workspace.RepositoryPath, name), "bad");
        await File.WriteAllTextAsync(Path.Combine(mirror, name), JsonSerializer.Serialize(captured.Manifest with { VersionId = Guid.NewGuid().ToString("N") }, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        var health = operation == "scrub" ? (await repo.ScrubAsync(true)).HealthState : (await repo.RunMirrorRepairAsync()).HealthState;
        Assert.Equal(RepositoryHealthState.Critical, health);
        Assert.Equal("bad", await File.ReadAllTextAsync(Path.Combine(workspace.RepositoryPath, name)));
    }

    [Theory]
    [InlineData(ChunkEncoding.Lz4)]
    [InlineData(ChunkEncoding.Lzma)]
    [InlineData(ChunkEncoding.Brotli)]
    public void Supported_stream_decoders_enforce_their_window_budget(ChunkEncoding encoding)
    {
        var codec = new ZstdChunkCodec();
        var encoded = codec.Compress(Encoding.UTF8.GetBytes(new string('x', 200)), encoding, 3);
        var error = Assert.Throws<RepositoryIntegrityException>(() => codec.Decompress(encoded, 200, encoding,
            new RepositoryIntegrityLimits(MaxDecoderWindowBytes: 1024)));
        Assert.Equal(RepositoryIntegrityFailure.LimitExceeded, error.Code);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_destination_io_failure_before_publication_preserves_existing_or_absent_output(bool exists)
    {
        using var workspace = TemporaryWorkspace.Create();
        var repo = RepositoryPublicationTests.Create(workspace.RepositoryPath, new((point, _) =>
        { if (point == RepositoryFaultPoint.BeforeRestorePublication) throw new IOException("injected full destination", unchecked((int)0x80070070)); }));
        var captured = await repo.CommitAsync(RepositoryIntegrityTests.Request("protected"));
        var output = Path.Combine(workspace.RootPath, "restored.txt");
        if (exists) await File.WriteAllTextAsync(output, "new work");
        await Assert.ThrowsAsync<IOException>(() => repo.RestoreAsync(captured.Manifest.VersionId, output));
        if (exists) Assert.Equal("new work", await File.ReadAllTextAsync(output));
        else Assert.False(File.Exists(output));
        Assert.Empty(Directory.EnumerateFiles(workspace.RootPath, "*.tmp", SearchOption.AllDirectories));
    }

    [Theory]
    [InlineData(false, "manifest")]
    [InlineData(true, "manifest")]
    [InlineData(false, "chunks")]
    [InlineData(true, "chunks")]
    public async Task Capture_rejects_a_version_outside_its_restore_support_limits(bool useMetadata, string budget)
    {
        using var workspace = TemporaryWorkspace.Create();
        var store = useMetadata ? new InMemoryRepositoryMetadataStore() : null;
        var limits = budget == "manifest" ? new RepositoryIntegrityLimits(MaxManifestBytes: 32)
            : new RepositoryIntegrityLimits(MaxExpandedChunkReferences: 1);
        var repo = new FileSystemChunkRepository(workspace.RepositoryPath, new FastCdcChunker(new ChunkingOptions(128, 256, 512)),
            new Blake3ContentHasher(), new ZstdChunkCodec(), null, store, limits);
        var request = RepositoryIntegrityTests.Request(new string('x', 1024));
        var error = await Assert.ThrowsAsync<RepositoryIntegrityException>(() => repo.CommitAsync(request));
        Assert.Equal(RepositoryIntegrityFailure.LimitExceeded, error.Code);
        Assert.Empty(await repo.ListVersionsAsync());
        // Complete unreferenced objects may remain; no version can acknowledge them.
        var reopened = new FileSystemChunkRepository(workspace.RepositoryPath, new FastCdcChunker(new ChunkingOptions(128, 256, 512)),
            new Blake3ContentHasher(), new ZstdChunkCodec(), null, store);
        Assert.Empty(await reopened.ListVersionsAsync());
        Assert.NotNull(await reopened.CommitAsync(RepositoryIntegrityTests.Request(new string('x', 1024))));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Folder_capture_rejects_a_cascade_crossing_its_expansion_budget(bool useMetadata)
    {
        using var workspace = TemporaryWorkspace.Create();
        var store = useMetadata ? new InMemoryRepositoryMetadataStore() : null;
        var repo = new FileSystemChunkRepository(workspace.RepositoryPath, new FastCdcChunker(new ChunkingOptions(128, 256, 512)),
            new Blake3ContentHasher(), new ZstdChunkCodec(), null, store, new RepositoryIntegrityLimits(MaxExpandedEntries: 2));
        var folder = Path.Combine(workspace.RootPath, "working");
        var first = await repo.CommitAsync(RepositoryIntegrityTests.Request("first") with
            { SourcePath = Path.Combine(folder, "first.txt"), WatchedFolderPath = folder });
        var before = await repo.ListVersionsAsync();
        var error = await Assert.ThrowsAsync<RepositoryIntegrityException>(() => repo.CommitAsync(RepositoryIntegrityTests.Request("second") with
            { SourcePath = Path.Combine(folder, "second.txt"), WatchedFolderPath = folder }));
        Assert.Equal(RepositoryIntegrityFailure.LimitExceeded, error.Code);
        Assert.Equal(before.Select(x => x.VersionId), (await repo.ListVersionsAsync()).Select(x => x.VersionId));
        var output = Path.Combine(workspace.RootPath, "restored.txt");
        await repo.RestoreAsync(first.Manifest.VersionId, output);
        Assert.Equal("first", await File.ReadAllTextAsync(output));
    }

    [Theory]
    [InlineData("scrub", "length")]
    [InlineData("scrub", "offset")]
    [InlineData("scrub", "signature")]
    [InlineData("repair", "length")]
    [InlineData("repair", "offset")]
    [InlineData("repair", "signature")]
    public async Task Health_and_mirror_repair_reject_semantic_manifest_corruption_and_preserve_a_good_mirror(string operation, string damage)
    {
        using var workspace = TemporaryWorkspace.Create();
        var mirror = Path.Combine(workspace.RootPath, "mirror");
        var mirrors = MirrorSetConfiguration.FromLegacyPath(mirror);
        var repo = RepositoryIntegrityTests.Create(workspace.RepositoryPath, mirrors);
        var captured = await repo.CommitAsync(RepositoryIntegrityTests.Request("protected"));
        var name = Path.Combine("manifests", captured.Manifest.VersionId + ".json");
        var mirrorBytes = await File.ReadAllBytesAsync(Path.Combine(mirror, name));
        var damaged = damage switch
        {
            "length" => captured.Manifest with { LogicalLength = captured.Manifest.LogicalLength + 1 },
            "offset" => captured.Manifest with { Chunks = [captured.Manifest.Chunks[0] with { Offset = 1 }] },
            _ => captured.Manifest with { ContentSignature = new string('0', 64) }
        };
        await File.WriteAllTextAsync(Path.Combine(workspace.RepositoryPath, name), JsonSerializer.Serialize(damaged, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        var health = operation == "scrub" ? (await repo.ScrubAsync(false)).HealthState
            : (await repo.RunMirrorRepairAsync(mirrors.EnabledNodes[0].Id)).HealthState;
        Assert.Equal(RepositoryHealthState.Critical, health);
        Assert.Equal(mirrorBytes, await File.ReadAllBytesAsync(Path.Combine(mirror, name)));
        var output = Path.Combine(workspace.RootPath, "restored.txt");
        await Assert.ThrowsAsync<RepositoryIntegrityException>(() => repo.RestoreAsync(captured.Manifest.VersionId, output));
        Assert.False(File.Exists(output));
    }

    [Theory]
    [InlineData(@"C:\fresh\repository", DriveType.Fixed, "NTFS", true)]
    [InlineData(@"E:\repository", DriveType.Removable, "ntfs", true)]
    [InlineData(@"\\server\share\repository", DriveType.Network, "NTFS", false)]
    [InlineData(@"Z:\repository", DriveType.Network, "NTFS", false)]
    [InlineData(@"E:\repository", DriveType.Fixed, "exFAT", false)]
    [InlineData(@"E:\repository", DriveType.Fixed, "ReFS", false)]
    [InlineData(@"Q:\repository", DriveType.NoRootDirectory, "NTFS", false)]
    [InlineData(@"\\?\C:\repository", DriveType.Fixed, "NTFS", false)]
    public void Root_support_classification_rejects_external_or_unsupported_storage_without_accessing_it(
        string path, DriveType driveType, string format, bool expected)
    {
        Assert.Equal(expected, StorageRootPolicy.IsSupported(path, driveType, format));
    }

    [Theory]
    [InlineData("BestEffort")]
    [InlineData("None")]
    public async Task Unavailable_optional_volumes_do_not_block_primary_operations(string leaseMode)
    {
        var mode = Enum.Parse<MirrorLeaseMode>(leaseMode);
        using var workspace = TemporaryWorkspace.Create();
        var mirror = Path.Combine(workspace.RootPath, "unavailable-mirror");
        var probes = new List<string>();
        await using var lease = await RepositoryLeaseSet.AcquireAsync(workspace.RepositoryPath, [mirror], mode, default, path =>
        {
            probes.Add(path);
            if (path == mirror) throw new DirectoryNotFoundException("injected unplugged volume");
            StorageRootPolicy.Validate(path);
        });
        Assert.Single(probes, path => path == workspace.RepositoryPath);
        Assert.Empty(lease.MirrorRoots);
        if (mode == MirrorLeaseMode.None) Assert.DoesNotContain(mirror, probes);
        else Assert.Single(lease.Warnings);
        Assert.False(Directory.Exists(mirror));
    }

    [Fact]
    public void PostgreSql_manifest_json_must_match_the_selected_row_identity()
    {
        var manifest = new FileVersionManifest(Guid.NewGuid().ToString("N"), "fixture", @"C:\working\source.txt",
            DateTimeOffset.UtcNow, CaptureConsistency.BestEffort, 0, []);
        var json = JsonSerializer.Serialize(manifest, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var error = Assert.Throws<RepositoryIntegrityException>(() => PostgreSqlRepositoryMetadataStore.DeserializeManifest(json, Guid.NewGuid().ToString("N")));
        Assert.Equal(RepositoryIntegrityFailure.InvalidManifest, error.Code);
        Assert.Equal(manifest.VersionId, PostgreSqlRepositoryMetadataStore.DeserializeManifest(json, manifest.VersionId).VersionId);
    }

    [Fact]
    public async Task PostgreSql_publication_rejects_its_manifest_limit_before_any_connection()
    {
        using var workspace = TemporaryWorkspace.Create();
        var config = MetadataStoreConfiguration.CreateDefault(workspace.RootPath) with
            { Host = "127.0.0.1", Port = 1, DatabaseName = "fv_test_not_connected", Username = "fv_test" };
        var store = new PostgreSqlRepositoryMetadataStore(config, "fixture", new(MaxManifestBytes: 32));
        var manifest = new FileVersionManifest(Guid.NewGuid().ToString("N"), "fixture", @"C:\working\source.txt",
            DateTimeOffset.UtcNow, CaptureConsistency.BestEffort, 0, []);
        var error = await Assert.ThrowsAsync<RepositoryIntegrityException>(() => store.RecordVersionAsync(manifest));
        Assert.Equal(RepositoryIntegrityFailure.LimitExceeded, error.Code);
    }

    [Fact]
    public async Task Folder_signature_must_agree_with_the_validated_child_sequence()
    {
        using var workspace = TemporaryWorkspace.Create();
        var (repo, folder, _) = await FolderRestoreIntegrityTests.Seed(workspace);
        var path = Path.Combine(workspace.RepositoryPath, "manifests", folder.VersionId + ".json");
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(folder with { ContentSignature = new string('0', 64) },
            new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        var output = Path.Combine(workspace.RootPath, "restored-folder");
        await Assert.ThrowsAsync<RepositoryIntegrityException>(() => repo.RestoreAsync(folder.VersionId, output));
        Assert.False(Directory.Exists(output));
    }

    private static FileSystemChunkRepository Create(string root, IRepositoryMetadataStore store) => new(root,
        new FastCdcChunker(new ChunkingOptions(128, 256, 512)), new Blake3ContentHasher(), new ZstdChunkCodec(), null, store);

    private sealed class ReadInterceptor(InMemoryRepositoryMetadataStore inner) : IRepositoryMetadataStore
    {
        internal IReadOnlyList<FileVersionManifest>? Listed { get; init; }
        internal Func<Task>? BeforeRead { get; init; }
        internal int DeleteCalls { get; private set; }
        public Task RecordVersionAsync(FileVersionManifest manifest, CancellationToken cancellationToken = default) => inner.RecordVersionAsync(manifest, cancellationToken);
        public Task<ChunkDescriptor?> FindChunkDescriptorAsync(string digest, CancellationToken cancellationToken = default) => inner.FindChunkDescriptorAsync(digest, cancellationToken);
        public async Task<FileVersionManifest> ReadManifestAsync(string versionId, CancellationToken cancellationToken = default)
        { if (BeforeRead is not null) await BeforeRead(); return await inner.ReadManifestAsync(versionId, cancellationToken); }
        public Task<IReadOnlyList<FileVersionManifest>> ListManifestsAsync(CancellationToken cancellationToken = default) => Listed is null ? inner.ListManifestsAsync(cancellationToken) : Task.FromResult(Listed);
        public Task<IReadOnlyList<RepositoryVersionSummary>> ListVersionsAsync(CancellationToken cancellationToken = default) => inner.ListVersionsAsync(cancellationToken);
        public Task<IReadOnlyList<RepositoryVersionSummary>> ListLatestEntriesAsync(CancellationToken cancellationToken = default) => inner.ListLatestEntriesAsync(cancellationToken);
        public Task DeleteVersionsAsync(IReadOnlyCollection<string> ids, CancellationToken cancellationToken = default) { DeleteCalls++; return inner.DeleteVersionsAsync(ids, cancellationToken); }
        public Task<IReadOnlyDictionary<string, long>> CountChunkReferencesAsync(IReadOnlyCollection<string> digests, CancellationToken cancellationToken = default) => inner.CountChunkReferencesAsync(digests, cancellationToken);
    }
}
