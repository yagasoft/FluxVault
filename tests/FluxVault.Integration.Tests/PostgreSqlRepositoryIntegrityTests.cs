using System.Security.Cryptography;
using System.Text;
using FluxVault.Abstractions.Policies;
using FluxVault.Abstractions.Storage;
using FluxVault.Core.Chunking;
using FluxVault.Core.Content;
using FluxVault.Core.Storage;
using FluxVault.Core.Storage.Integrity;
using FluxVault.Core.Storage.Metadata;
using FluxVault.Integration.Tests.Fixtures;
using Npgsql;

namespace FluxVault.Integration.Tests;

[Trait("Category", "RequiresPostgreSql")]
public sealed class PostgreSqlRepositoryIntegrityTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Real_metadata_capture_and_restore_verify_independent_hashes(bool compressed)
    {
        await using var fixture = await DisposablePostgreSqlFixture.CreateAsync();
        var repo = Create(fixture);
        var payload = Encoding.UTF8.GetBytes(new string('a', 200_000));
        var captured = await repo.CommitAsync(Request(fixture, payload, compressed));
        Assert.Equal(1, await fixture.ScalarAsync("SELECT count(*) FROM fluxvault.versions"));
        Assert.True(await fixture.ScalarAsync("SELECT count(*) FROM fluxvault.version_chunks") > 0);
        await VerifyRestore(fixture, captured.Manifest.VersionId, payload);
    }

    [Theory]
    [InlineData("purge")]
    [InlineData("retention")]
    public async Task Final_reference_removal_allows_recapture_under_a_new_compression_policy(string operation)
    {
        await using var fixture = await DisposablePostgreSqlFixture.CreateAsync();
        var repo = Create(fixture);
        var payload = Encoding.UTF8.GetBytes(new string('a', 200_000));
        var first = await repo.CommitAsync(Request(fixture, payload, false) with { CapturedAtUtc = DateTimeOffset.UtcNow.AddDays(-10) });
        if (operation == "purge") await repo.PurgeAsync(new([new(fixture.Files.Source, RepositoryPurgeScopeKind.File)]));
        else
        {
            await repo.CommitAsync(Request(fixture, Encoding.UTF8.GetBytes("replacement content"), false));
            await repo.ApplyRetentionAsync(new(true, TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero, 1), DateTimeOffset.UtcNow);
        }
        foreach (var digest in first.Manifest.Chunks.Select(chunk => chunk.Digest).Distinct())
        {
            Assert.Null(await fixture.Store.FindChunkDescriptorAsync(digest));
            Assert.False(File.Exists(ChunkPath(fixture, digest)));
            Assert.Equal(0, await fixture.ScalarAsync($"SELECT count(*) FROM fluxvault.chunks WHERE digest = '{digest}'"));
        }
        var recaptured = await repo.CommitAsync(Request(fixture, payload, true));
        Assert.All(recaptured.Manifest.Chunks, chunk => Assert.Equal(ChunkEncoding.Zstd, chunk.Encoding));
        await VerifyRestore(fixture, recaptured.Manifest.VersionId, payload);
    }

    [Theory]
    [InlineData("logical", false)]
    [InlineData("stored", false)]
    [InlineData("encoding", false)]
    [InlineData("logical", true)]
    public async Task Descriptor_conflicts_roll_back_the_entire_metadata_transaction(string field, bool sameVersion)
    {
        await using var fixture = await DisposablePostgreSqlFixture.CreateAsync();
        var captured = await Create(fixture).CommitAsync(Request(fixture, Encoding.UTF8.GetBytes("protected"), false));
        var original = captured.Manifest;
        var changed = original with { VersionId = sameVersion ? original.VersionId : Guid.NewGuid().ToString("N"),
            ContentSignature = null, Chunks = [field switch
            {
                "logical" => original.Chunks[0] with { Length = original.Chunks[0].Length + 1 },
                "stored" => original.Chunks[0] with { StoredLength = original.Chunks[0].StoredLength + 1 },
                _ => original.Chunks[0] with { Encoding = ChunkEncoding.Zstd }
            }] };
        await Assert.ThrowsAsync<RepositoryIntegrityException>(() => fixture.Store.RecordVersionAsync(changed));
        Assert.Equal(1, await fixture.ScalarAsync("SELECT count(*) FROM fluxvault.versions"));
        var retained = await fixture.Store.ReadManifestAsync(original.VersionId);
        Assert.Equal(original.Chunks, retained.Chunks);
        await VerifyRestore(fixture, original.VersionId, Encoding.UTF8.GetBytes("protected"));
    }

    [Fact]
    public async Task Mixed_case_digest_keys_cannot_hide_acknowledged_descriptors_or_references()
    {
        await using var fixture = await DisposablePostgreSqlFixture.CreateAsync();
        var first = SyntheticManifest(fixture) with { Chunks = [new(new string('A', 64), 0, 10, 10, ChunkEncoding.Raw)] };
        await fixture.Store.RecordVersionAsync(first);
        var descriptor = await fixture.Store.FindChunkDescriptorAsync(new string('a', 64));
        Assert.NotNull(descriptor);
        Assert.Equal(10, descriptor.LogicalLength);
        var counts = await fixture.Store.CountChunkReferencesAsync([new string('A', 64)]);
        Assert.Equal(1, counts[new string('a', 64)]);
        var conflicting = first with { VersionId = Guid.NewGuid().ToString("N"), Chunks = [new(new string('a', 64), 0, 11, 10, ChunkEncoding.Raw)] };
        await Assert.ThrowsAsync<RepositoryIntegrityException>(() => fixture.Store.RecordVersionAsync(conflicting));
        Assert.Equal(1, await fixture.ScalarAsync("SELECT count(*) FROM fluxvault.chunks"));
        Assert.Equal(1, await fixture.ScalarAsync("SELECT count(*) FROM fluxvault.version_chunks"));
    }

    [Fact]
    public async Task Identical_replay_is_idempotent_and_changed_version_identity_rolls_back_the_batch()
    {
        await using var fixture = await DisposablePostgreSqlFixture.CreateAsync();
        var captured = await Create(fixture).CommitAsync(Request(fixture, Encoding.UTF8.GetBytes("protected"), false));
        await fixture.Store.RecordVersionAsync(captured.Manifest);
        Assert.Equal(1, await fixture.ScalarAsync("SELECT count(*) FROM fluxvault.versions"));
        Assert.Equal(1, await fixture.ScalarAsync("SELECT count(*) FROM fluxvault.version_chunks"));
        Assert.Equal(1, await fixture.ScalarAsync("SELECT count(*) FROM fluxvault.metadata_outbox"));
        var newVersion = captured.Manifest with { VersionId = Guid.NewGuid().ToString("N") };
        var replacement = captured.Manifest with { Chunks = [captured.Manifest.Chunks[0] with { Digest = new string('b', 64) }] };
        await Assert.ThrowsAsync<RepositoryIntegrityException>(() => fixture.Store.RecordVersionsAsync([newVersion, replacement]));
        Assert.Equal(1, await fixture.ScalarAsync("SELECT count(*) FROM fluxvault.versions"));
        Assert.Equal(1, await fixture.ScalarAsync("SELECT count(*) FROM fluxvault.version_chunks"));
        Assert.Equal(1, await fixture.ScalarAsync("SELECT count(*) FROM fluxvault.metadata_outbox"));
        Assert.Equal(1, await fixture.ScalarAsync("SELECT count(*) FROM fluxvault.chunks"));
        await VerifyRestore(fixture, captured.Manifest.VersionId, Encoding.UTF8.GetBytes("protected"));
    }

    [Fact]
    public async Task Conflicting_logical_lengths_within_one_batch_are_rejected_atomically()
    {
        await using var fixture = await DisposablePostgreSqlFixture.CreateAsync();
        var first = SyntheticManifest(fixture);
        var second = first with { VersionId = Guid.NewGuid().ToString("N"), Chunks = [first.Chunks[0] with { Length = 11 }] };
        await Assert.ThrowsAsync<RepositoryIntegrityException>(() => fixture.Store.RecordVersionsAsync([first, second]));
        Assert.Equal(0, await fixture.ScalarAsync("SELECT count(*) FROM fluxvault.versions"));
        Assert.Equal(0, await fixture.ScalarAsync("SELECT count(*) FROM fluxvault.chunks"));
    }

    [Fact]
    public async Task Concurrent_logical_descriptors_admit_only_one_acknowledged_tuple()
    {
        await using var fixture = await DisposablePostgreSqlFixture.CreateAsync();
        var first = SyntheticManifest(fixture);
        var second = first with { VersionId = Guid.NewGuid().ToString("N"), Chunks = [first.Chunks[0] with { Length = 11 }] };
        var results = await Task.WhenAll(Record(first), Record(second));
        Assert.Single(results, exception => exception is null);
        Assert.Single(results, exception => exception is RepositoryIntegrityException);
        Assert.Equal(1, await fixture.ScalarAsync("SELECT count(*) FROM fluxvault.versions"));
        Assert.Equal(1, await fixture.ScalarAsync("SELECT count(*) FROM fluxvault.version_chunks"));
        async Task<Exception?> Record(FileVersionManifest manifest)
        { try { await fixture.Store.RecordVersionAsync(manifest); return null; } catch (Exception exception) { return exception; } }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Record_and_delete_transactions_preserve_shared_catalogue_and_retire_only_affected_locations(bool recordFirst)
    {
        await using var fixture = await DisposablePostgreSqlFixture.CreateAsync();
        var store = fixture.Store;
        await store.InitializeAsync();
        var captured = await Create(fixture).CommitAsync(Request(fixture, Encoding.UTF8.GetBytes("protected"), false));
        var original = captured.Manifest;
        var next = original with { VersionId = Guid.NewGuid().ToString("N"), SourcePath = fixture.Files.Source + ".next" };
        var digest = original.Chunks[0].Digest;
        await fixture.ExecuteAsync($"""
            INSERT INTO fluxvault.mirror_nodes VALUES ('fixture', 'Fixture', 'fixture', true, 0, NULL);
            INSERT INTO fluxvault.chunk_locations VALUES ('{digest}', 'fixture', {original.Chunks[0].StoredLength}, now(), 'Healthy');
            INSERT INTO fluxvault.chunks VALUES ('{new string('b', 64)}', 10, 'Raw', now());
            INSERT INTO fluxvault.chunk_locations VALUES ('{new string('b', 64)}', 'fixture', 10, now(), 'Healthy');
            """);
        await using var barrier = await HoldMutationAsync(fixture, recordFirst ? "INSERT" : "DELETE", recordFirst ? next.VersionId : original.VersionId);
        var leading = recordFirst ? store.RecordVersionAsync(next) : store.DeleteVersionsAsync([original.VersionId]);
        await WaitForAdvisoryWaiters(fixture, 1);
        var following = recordFirst ? store.DeleteVersionsAsync([original.VersionId]) : store.RecordVersionAsync(next);
        try { await WaitForAdvisoryWaiters(fixture, 2); }
        finally { await ReleaseMutationAsync(barrier); }
        await Task.WhenAll(leading, following).WaitAsync(TimeSpan.FromSeconds(20));
        Assert.Equal(1, await fixture.ScalarAsync("SELECT count(*) FROM fluxvault.versions"));
        Assert.Equal(1, await fixture.ScalarAsync($"SELECT count(*) FROM fluxvault.chunks WHERE digest = '{digest}'"));
        Assert.Equal(recordFirst ? 1 : 0, await fixture.ScalarAsync($"SELECT count(*) FROM fluxvault.chunk_locations WHERE digest = '{digest}'"));
        Assert.Equal(1, await fixture.ScalarAsync($"SELECT count(*) FROM fluxvault.chunks WHERE digest = '{new string('b', 64)}'"));
        Assert.Equal(1, await fixture.ScalarAsync($"SELECT count(*) FROM fluxvault.chunk_locations WHERE digest = '{new string('b', 64)}'"));
        await VerifyRestore(fixture, next.VersionId, Encoding.UTF8.GetBytes("protected"));
    }

    [Fact]
    public async Task Cancelling_a_mutation_lock_waiter_leaves_no_partial_rows_and_releases_the_connection()
    {
        await using var fixture = await DisposablePostgreSqlFixture.CreateAsync();
        var store = fixture.Store;
        await store.InitializeAsync();
        var first = SyntheticManifest(fixture);
        var next = first with { VersionId = Guid.NewGuid().ToString("N") };
        await using var barrier = await HoldMutationAsync(fixture, "INSERT", first.VersionId);
        var leading = store.RecordVersionAsync(first);
        await WaitForAdvisoryWaiters(fixture, 1);
        using var cancellation = new CancellationTokenSource();
        var waiting = store.RecordVersionAsync(next, cancellation.Token);
        try
        {
            await WaitForAdvisoryWaiters(fixture, 2);
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
            Assert.Equal(0, await fixture.ScalarAsync("SELECT count(*) FROM fluxvault.versions"));
        }
        finally { await ReleaseMutationAsync(barrier); }
        await leading.WaitAsync(TimeSpan.FromSeconds(20));
        await store.RecordVersionAsync(next).WaitAsync(TimeSpan.FromSeconds(20));
        Assert.Equal(2, await fixture.ScalarAsync("SELECT count(*) FROM fluxvault.versions"));
    }

    private static FileVersionManifest SyntheticManifest(DisposablePostgreSqlFixture fixture) => new(Guid.NewGuid().ToString("N"), "fixture",
        fixture.Files.Source, DateTimeOffset.UtcNow, CaptureConsistency.BestEffort, 10, [new(new string('a', 64), 0, 10, 10, ChunkEncoding.Raw)]);

    // A distinct test-only advisory lock pauses a transaction inside an actual SQL trigger.
    private const long BarrierKey = 426_001;
    private static async Task<NpgsqlConnection> HoldMutationAsync(DisposablePostgreSqlFixture fixture, string operation, string versionId)
    {
        await fixture.ExecuteAsync($"""
            CREATE FUNCTION fluxvault.fixture_barrier() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN
                IF {(operation == "INSERT" ? "NEW" : "OLD")}.version_id = '{versionId}' THEN
                    PERFORM pg_advisory_xact_lock({BarrierKey});
                END IF;
                RETURN {(operation == "INSERT" ? "NEW" : "OLD")};
            END $$;
            CREATE TRIGGER fixture_barrier BEFORE {operation} ON fluxvault.versions FOR EACH ROW EXECUTE FUNCTION fluxvault.fixture_barrier();
            """);
        var connection = fixture.Connection();
        try
        {
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand($"SELECT pg_advisory_lock({BarrierKey})", connection);
            await command.ExecuteNonQueryAsync();
            return connection;
        }
        catch { await connection.DisposeAsync(); throw; }
    }
    private static async Task ReleaseMutationAsync(NpgsqlConnection connection)
    {
        await using var command = new NpgsqlCommand($"SELECT pg_advisory_unlock({BarrierKey})", connection);
        await command.ExecuteNonQueryAsync();
    }
    private static async Task WaitForAdvisoryWaiters(DisposablePostgreSqlFixture fixture, int count)
    {
        var timeout = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < timeout)
        {
            if (await fixture.ScalarAsync("SELECT count(*) FROM pg_locks WHERE database = (SELECT oid FROM pg_database WHERE datname = current_database()) AND locktype = 'advisory' AND NOT granted") >= count) return;
            await Task.Delay(25);
        }
        throw new TimeoutException("The expected metadata transactions did not reach the controlled lock barrier.");
    }

    [Fact]
    public async Task A_database_statement_failure_leaves_no_acknowledgement_and_preserves_verifiable_objects()
    {
        await using var fixture = await DisposablePostgreSqlFixture.CreateAsync();
        await fixture.ExecuteAsync("""
            CREATE FUNCTION fluxvault.fixture_reject() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN RAISE EXCEPTION 'fixture transaction failure'; END $$;
            CREATE TRIGGER fixture_reject BEFORE INSERT ON fluxvault.version_chunks FOR EACH ROW EXECUTE FUNCTION fluxvault.fixture_reject();
            """);
        var payload = Encoding.UTF8.GetBytes("protected");
        await Assert.ThrowsAsync<PostgresException>(() => Create(fixture).CommitAsync(Request(fixture, payload, false)));
        Assert.Equal(0, await fixture.ScalarAsync("SELECT count(*) FROM fluxvault.versions"));
        Assert.Equal(0, await fixture.ScalarAsync("SELECT count(*) FROM fluxvault.version_chunks"));
        var path = Directory.EnumerateFiles(fixture.Files.Repository, "*.chunk", SearchOption.AllDirectories).Single();
        Assert.Equal(payload, await File.ReadAllBytesAsync(path));
        await fixture.ExecuteAsync("DROP TRIGGER fixture_reject ON fluxvault.version_chunks; DROP FUNCTION fluxvault.fixture_reject();");
        var retry = await Create(fixture).CommitAsync(Request(fixture, payload, false));
        await VerifyRestore(fixture, retry.Manifest.VersionId, payload);
    }

    [Fact]
    public async Task Concurrent_descriptor_recording_cannot_publish_two_representations()
    {
        await using var fixture = await DisposablePostgreSqlFixture.CreateAsync();
        var first = new FileVersionManifest(Guid.NewGuid().ToString("N"), "fixture", fixture.Files.Source, DateTimeOffset.UtcNow,
            CaptureConsistency.BestEffort, 10, [new(new string('a', 64), 0, 10, 10, ChunkEncoding.Raw)]);
        var second = first with { VersionId = Guid.NewGuid().ToString("N"), SourcePath = fixture.Files.Source + ".other", Chunks = [first.Chunks[0] with { Encoding = ChunkEncoding.Zstd }] };
        var results = await Task.WhenAll(Record(first), Record(second));
        Assert.Single(results, exception => exception is null);
        Assert.Single(results, exception => exception is RepositoryIntegrityException);
        Assert.Equal(1, await fixture.ScalarAsync("SELECT count(*) FROM fluxvault.versions"));
        Assert.Equal(1, await fixture.ScalarAsync("SELECT count(*) FROM fluxvault.version_chunks"));
        async Task<Exception?> Record(FileVersionManifest manifest)
        { try { await fixture.Store.RecordVersionAsync(manifest); return null; } catch (Exception exception) { return exception; } }
    }

    [Fact]
    public async Task Oversized_postgresql_json_is_rejected_before_client_deserialisation()
    {
        await using var fixture = await DisposablePostgreSqlFixture.CreateAsync();
        var captured = await Create(fixture).CommitAsync(Request(fixture, Encoding.UTF8.GetBytes("protected"), false));
        var bounded = new PostgreSqlRepositoryMetadataStore(fixture.Configuration, "fixture-device", new(MaxManifestBytes: 32));
        var error = await Assert.ThrowsAsync<RepositoryIntegrityException>(() => bounded.ReadManifestAsync(captured.Manifest.VersionId));
        Assert.Equal(RepositoryIntegrityFailure.LimitExceeded, error.Code);
        await Assert.ThrowsAsync<RepositoryIntegrityException>(() => bounded.ListManifestsAsync());
        await Assert.ThrowsAsync<RepositoryIntegrityException>(() => bounded.ListVersionsAsync());
    }

    [Fact]
    public async Task Process_death_after_database_commit_preserves_versions_with_an_unknown_acknowledgement()
    {
        await using var fixture = await DisposablePostgreSqlFixture.CreateAsync();
        await using var child = fixture.Files.Start("capture", ["--source", "working/source.bin", "--gate", "AfterMetadataRecorded", .. fixture.HostArguments]);
        await child.WaitForGateAsync();
        var committed = Assert.Single(await fixture.Store.ListVersionsAsync());
        await child.KillAsync();
        Assert.Equal(1, await fixture.ScalarAsync("SELECT count(*) FROM fluxvault.versions"));
        Assert.Equal(1, await fixture.ScalarAsync("SELECT count(*) FROM fluxvault.version_chunks"));
        await VerifyRestore(fixture, committed.VersionId, await File.ReadAllBytesAsync(fixture.Files.Source));
        var retry = await Create(fixture).CommitAsync(Request(fixture, await File.ReadAllBytesAsync(fixture.Files.Source), false));
        Assert.Equal(0, retry.NewChunkCount);
        Assert.Equal(2, await fixture.ScalarAsync("SELECT count(*) FROM fluxvault.versions"));
        await VerifyRestore(fixture, committed.VersionId, await File.ReadAllBytesAsync(fixture.Files.Source));
    }

    [Fact]
    public async Task PostgreSql_manifest_row_identity_tampering_is_rejected_without_destination_publication()
    {
        await using var fixture = await DisposablePostgreSqlFixture.CreateAsync();
        var captured = await Create(fixture).CommitAsync(Request(fixture, Encoding.UTF8.GetBytes("protected"), false));
        var replacement = Guid.NewGuid().ToString("N");
        await fixture.ExecuteAsync($"UPDATE fluxvault.versions SET manifest_json = jsonb_set(manifest_json, '{{versionId}}', '\"{replacement}\"'::jsonb)");
        var output = Path.Combine(fixture.Files.Root, "sentinel.txt");
        await File.WriteAllTextAsync(output, "new work");
        var error = await Assert.ThrowsAsync<RepositoryIntegrityException>(() => Create(fixture).RestoreAsync(captured.Manifest.VersionId, output));
        Assert.Equal(RepositoryIntegrityFailure.InvalidManifest, error.Code);
        Assert.Equal("new work", await File.ReadAllTextAsync(output));
        Assert.Equal(1, await fixture.ScalarAsync("SELECT count(*) FROM fluxvault.version_chunks"));
    }

    internal static FileSystemChunkRepository Create(DisposablePostgreSqlFixture fixture) => new(fixture.Files.Repository,
        new FastCdcChunker(new ChunkingOptions(64 * 1024, 256 * 1024, 1024 * 1024)), new Blake3ContentHasher(), new ZstdChunkCodec(), null, fixture.Store);
    internal static FileCommitRequest Request(DisposablePostgreSqlFixture fixture, byte[] payload, bool compressed) => new("fixture", fixture.Files.Source,
        DateTimeOffset.UtcNow, CaptureConsistency.BestEffort, compressed ? CompressionPreference.Zstd : CompressionPreference.Off, 1, new MemoryStream(payload));
    private static string ChunkPath(DisposablePostgreSqlFixture fixture, string digest) => Path.Combine(fixture.Files.Repository, "chunks", digest[..2], digest + ".chunk");
    private static async Task VerifyRestore(DisposablePostgreSqlFixture fixture, string version, byte[] expected)
    {
        var output = Path.Combine(fixture.Files.Root, "verified.bin");
        var result = await Create(fixture).RestoreAsync(version, output);
        Assert.Equal(expected.Length, result.VerifiedLogicalBytes);
        await using var stream = File.OpenRead(output);
        Assert.Equal(SHA256.HashData(expected), await SHA256.HashDataAsync(stream));
    }
}
