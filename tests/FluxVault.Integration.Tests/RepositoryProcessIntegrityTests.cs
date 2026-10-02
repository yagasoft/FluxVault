using System.Text.Json;
using FluxVault.Abstractions.Storage;
using FluxVault.Abstractions.Policies;
using FluxVault.Core.Chunking;
using FluxVault.Core.Content;
using FluxVault.Core.Storage;
using FluxVault.Integration.Tests.Fixtures;

namespace FluxVault.Integration.Tests;

public sealed class RepositoryProcessIntegrityTests
{
    [Fact]
    public async Task A_killed_lease_owner_releases_storage_without_deleting_the_lock_file()
    {
        using var fixture = new RepositoryProcessFixture();
        await using var owner = fixture.Start("hold");
        await owner.WaitForGateAsync();
        await using (var contender = fixture.Start("capture", "--source", "working/source.bin"))
        {
            var busy = await contender.CompleteAsync();
            Assert.Equal(3, busy.ExitCode);
            Assert.Contains("RepositoryBusy", busy.Error);
        }
        await owner.KillAsync();
        Assert.True(File.Exists(Path.Combine(fixture.Repository, ".fluxvault.lock")));
        await using var retry = fixture.Start("capture", "--source", "working/source.bin");
        var completed = await retry.CompleteAsync();
        Assert.Equal(0, completed.ExitCode);
        var result = ReadManifest(completed.Output);
        await VerifyRestore(fixture, result.VersionId);
    }

    [Fact]
    public async Task Process_death_between_pair_renames_records_no_version_and_preserves_the_partial_object()
    {
        using var fixture = new RepositoryProcessFixture();
        await using var writer = fixture.Start("capture", "--source", "working/source.bin", "--gate", "ObjectPayloadPublished");
        await writer.WaitForGateAsync();
        await writer.KillAsync();
        Assert.Empty(await Create(fixture).ListVersionsAsync());
        var stored = Directory.EnumerateFiles(fixture.Repository, "*.chunk", SearchOption.AllDirectories).Single();
        var before = await File.ReadAllBytesAsync(stored);
        await using var retry = fixture.Start("capture", "--source", "working/source.bin");
        var refused = await retry.CompleteAsync();
        Assert.Equal(3, refused.ExitCode);
        Assert.Equal(before, await File.ReadAllBytesAsync(stored));
        Assert.Empty(await Create(fixture).ListVersionsAsync());
    }

    [Theory]
    [InlineData("ObjectPayloadPublished")]
    [InlineData("DepartingPayloadDeleted")]
    public async Task Process_death_during_drain_preserves_verified_copies_and_allows_safe_retry(string point)
    {
        using var fixture = new RepositoryProcessFixture();
        FileVersionManifest captured;
        await using (var capture = fixture.Start("capture", "--source", "working/source.bin", "--mirrors", "2"))
        {
            var completed = await capture.CompleteAsync();
            Assert.Equal(0, completed.ExitCode);
            captured = ReadManifest(completed.Output);
        }
        var chunk = captured.Chunks[0];
        if (point == "ObjectPayloadPublished")
        {
            File.Delete(Path.Combine(fixture.Root, "second", "chunks", chunk.Digest[..2], chunk.Digest + ".chunk"));
            File.Delete(Path.Combine(fixture.Root, "second", "chunks", chunk.Digest[..2], chunk.Digest + ".json"));
        }
        await using (var drain = fixture.Start("drain", "--mirrors", "2", "--gate", point))
        {
            await drain.WaitForGateAsync();
            await drain.KillAsync();
        }
        await VerifyRestore(fixture, captured.VersionId);
        await using var retry = fixture.Start("drain", "--mirrors", "2");
        var result = await retry.CompleteAsync();
        Assert.Equal(0, result.ExitCode);
        Assert.Equal(RepositoryHealthState.Healthy, JsonSerializer.Deserialize<MirrorRebalancePreviewReport>(result.Output)!.HealthState);
        Assert.Equal(await File.ReadAllBytesAsync(fixture.Source), await File.ReadAllBytesAsync(
            Path.Combine(fixture.Root, "second", "chunks", chunk.Digest[..2], chunk.Digest + ".chunk")));
        Assert.False(File.Exists(Path.Combine(fixture.Root, "first", "chunks", chunk.Digest[..2], chunk.Digest + ".chunk")));
    }

    [Theory]
    [InlineData("capture")]
    [InlineData("purge")]
    [InlineData("retention")]
    public async Task Restore_holds_its_lease_until_destination_publication(string competingMode)
    {
        using var fixture = new RepositoryProcessFixture();
        var repo = Create(fixture);
        var captured = await repo.CommitAsync(new("fixture", fixture.Source, DateTimeOffset.UtcNow, CaptureConsistency.BestEffort,
            CompressionPreference.Off, 128, File.OpenRead(fixture.Source)));
        await using var restore = fixture.Start("restore", "--version", captured.Manifest.VersionId,
            "--output", "restored.bin", "--gate", "BeforeRestorePublication");
        await restore.WaitForGateAsync();
        await using var contender = fixture.Start(competingMode, "--source", "working/source.bin");
        var busy = await contender.CompleteAsync();
        Assert.Equal(3, busy.ExitCode);
        Assert.Contains("RepositoryBusy", busy.Error);
        Assert.False(File.Exists(Path.Combine(fixture.Root, "restored.bin")));
        await restore.KillAsync();
        await VerifyRestore(fixture, captured.Manifest.VersionId);
    }

    [Fact]
    public async Task A_foreign_owned_mirror_is_rejected_before_acknowledging_capture()
    {
        using var fixture = new RepositoryProcessFixture();
        var repo = Create(fixture);
        var captured = await repo.CommitAsync(new("fixture", fixture.Source, DateTimeOffset.UtcNow, CaptureConsistency.BestEffort,
            CompressionPreference.Off, 128, File.OpenRead(fixture.Source)));
        await using (var foreign = fixture.Start("capture", "--source", "working/source.bin", "--repository-name", "first"))
            Assert.Equal(0, (await foreign.CompleteAsync()).ExitCode);
        await using var contender = fixture.Start("capture", "--source", "working/source.bin", "--mirrors", "2");
        var result = await contender.CompleteAsync();
        Assert.Equal(3, result.ExitCode);
        Assert.Contains("OwnershipMismatch", result.Error);
        Assert.Single(await repo.ListVersionsAsync());
        await VerifyRestore(fixture, captured.Manifest.VersionId);
    }

    [Fact]
    public async Task A_junction_alias_of_primary_storage_is_rejected_in_another_process()
    {
        using var fixture = new RepositoryProcessFixture();
        var repo = Create(fixture);
        var captured = await repo.CommitAsync(new("fixture", fixture.Source, DateTimeOffset.UtcNow, CaptureConsistency.BestEffort,
            CompressionPreference.Off, 128, File.OpenRead(fixture.Source)));
        var alias = Path.Combine(fixture.Root, "alias");
        var start = new System.Diagnostics.ProcessStartInfo("cmd.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in new[] { "/c", "mklink", "/J", alias, fixture.Repository }) start.ArgumentList.Add(argument);
        using var junction = System.Diagnostics.Process.Start(start)!;
        var output = await junction.StandardOutput.ReadToEndAsync();
        var error = await junction.StandardError.ReadToEndAsync();
        await junction.WaitForExitAsync();
        Assert.True(junction.ExitCode == 0, output + error);
        await using var contender = fixture.Start("capture", "--source", "working/source.bin", "--repository-name", "alias");
        var result = await contender.CompleteAsync();
        Assert.Equal(3, result.ExitCode);
        Assert.Contains("OwnershipMismatch", result.Error);
        await VerifyRestore(fixture, captured.Manifest.VersionId);
        Directory.Delete(alias); // Remove this generated junction itself, without recursion.
    }

    [Theory]
    [InlineData("capture", "repair")]
    [InlineData("drain", "capture")]
    public async Task Capture_repair_and_drain_share_the_same_process_lease_protocol(string owningMode, string competingMode)
    {
        using var fixture = new RepositoryProcessFixture();
        FileVersionManifest captured;
        await using (var seed = fixture.Start("capture", "--source", "working/source.bin", "--mirrors", "2"))
        { var result = await seed.CompleteAsync(); Assert.Equal(0, result.ExitCode); captured = ReadManifest(result.Output); }
        await File.WriteAllTextAsync(Path.Combine(fixture.Root, "working", "new.bin"), "different protected bytes");
        if (owningMode == "drain")
        {
            var chunk = captured.Chunks[0];
            File.Delete(Path.Combine(fixture.Root, "second", "chunks", chunk.Digest[..2], chunk.Digest + ".chunk"));
            File.Delete(Path.Combine(fixture.Root, "second", "chunks", chunk.Digest[..2], chunk.Digest + ".json"));
        }
        await using var owner = fixture.Start(owningMode, "--source", "working/new.bin", "--mirrors", "2", "--gate", "ObjectPayloadPublished");
        await owner.WaitForGateAsync();
        await using (var competing = fixture.Start(competingMode, "--source", "working/source.bin", "--mirrors", "2"))
        {
            var result = await competing.CompleteAsync();
            Assert.Equal(3, result.ExitCode);
            Assert.Contains("RepositoryBusy", result.Error);
        }
        await owner.KillAsync();
        await VerifyRestore(fixture, captured.VersionId);
        await using var retry = fixture.Start(competingMode, "--source", "working/source.bin", "--mirrors", "2");
        Assert.Equal(0, (await retry.CompleteAsync()).ExitCode);
        await VerifyRestore(fixture, captured.VersionId);
    }

    private static FileVersionManifest ReadManifest(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.GetProperty("Manifest").Deserialize<FileVersionManifest>()!;
    }

    private static FileSystemChunkRepository Create(RepositoryProcessFixture fixture) => new(fixture.Repository,
        new FastCdcChunker(new ChunkingOptions(64 * 1024, 256 * 1024, 1024 * 1024)), new Blake3ContentHasher(), new ZstdChunkCodec());
    private static async Task VerifyRestore(RepositoryProcessFixture fixture, string version)
    {
        var output = Path.Combine(fixture.Root, "verified.bin");
        await Create(fixture).RestoreAsync(version, output);
        Assert.Equal(await File.ReadAllBytesAsync(fixture.Source), await File.ReadAllBytesAsync(output));
    }
}
