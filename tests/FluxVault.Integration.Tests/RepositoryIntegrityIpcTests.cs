using System.Security.Cryptography;
using FluxVault.Abstractions.Ipc;
using FluxVault.Abstractions.Storage;
using FluxVault.Core.Ipc;
using FluxVault.Integration.Tests.Fixtures;
using FluxVault.Windows.Security;
using System.Security.Principal;

namespace FluxVault.Integration.Tests;

[Trait("Category", "RequiresPostgreSql")]
public sealed class RepositoryIntegrityIpcTests
{
    [Theory]
    [InlineData("Off", false)]
    [InlineData("Zstd", false)]
    [InlineData("Off", true)]
    public async Task Private_pipe_capture_list_restore_returns_verified_results(string compression, bool hintFailure)
    {
        await using var fixture = await DisposablePostgreSqlFixture.CreateAsync();
        await File.WriteAllTextAsync(fixture.Files.Source, new string('a', 200_000));
        var pipe = "FluxVault.Integrity." + Guid.NewGuid().ToString("N");
        await using var server = fixture.Files.Start("serve", fixture.HostArguments.Concat(new[] { "--pipe", pipe, "--compression", compression,
            "--fault", hintFailure ? "hint-failure" : "none" }).ToArray());
        await server.WaitForReadyAsync();
        var client = CreateClient(pipe);
        Assert.True((await client.SendAsync(FluxVaultIpcRequest.RunBackupNow())).Success);
        var coldStatus = await client.SendAsync(FluxVaultIpcRequest.GetStatus(statusDetailLevel: FluxVaultStatusDetailLevel.Fast));
        Assert.True(coldStatus.Success);
        Assert.False(coldStatus.Status!.HasVersionInventory);
        Assert.Empty(coldStatus.Status.RecentVersions);
        var fullStatus = await client.SendAsync(FluxVaultIpcRequest.GetStatus());
        Assert.True(fullStatus.Success);
        Assert.True(fullStatus.Status!.HasVersionInventory);
        Assert.Contains(fullStatus.Status.RecentVersions, version => version.SourcePath == fixture.Files.Source);
        var warmStatus = await client.SendAsync(FluxVaultIpcRequest.GetStatus(statusDetailLevel: FluxVaultStatusDetailLevel.Fast));
        Assert.True(warmStatus.Success);
        Assert.True(warmStatus.Status!.HasVersionInventory);
        Assert.Equal(fullStatus.Status.RecentVersions.Select(version => version.VersionId), warmStatus.Status.RecentVersions.Select(version => version.VersionId));
        var listed = await client.SendAsync(FluxVaultIpcRequest.ListVersions());
        Assert.True(listed.Success);
        var version = Assert.Single(listed.Versions!, version => version.EntryKind == RepositoryEntryKind.File && version.SourcePath == fixture.Files.Source);
        Assert.Contains(listed.Versions!, version => version.EntryKind == RepositoryEntryKind.Folder && version.SourcePath == Path.GetDirectoryName(fixture.Files.Source));
        var output = Path.Combine(fixture.Files.Root, "restored.bin");
        var response = await client.SendAsync(FluxVaultIpcRequest.RestoreVersion(version.VersionId, output));
        Assert.True(response.Success, response.ErrorMessage);
        Assert.Equal(200_000, response.RestoreResult!.VerifiedLogicalBytes);
        Assert.Equal(1, response.RestoreResult.RestoredFileCount);
        if (hintFailure) Assert.Contains(response.RestoreResult.Warnings, warning => warning.Contains("lineage"));
        else Assert.Empty(response.RestoreResult.Warnings);
        Assert.Equal(await Hash(fixture.Files.Source), await Hash(output));
        var afterRestoreStatus = await client.SendAsync(FluxVaultIpcRequest.GetStatus(statusDetailLevel: FluxVaultStatusDetailLevel.Fast));
        Assert.True(afterRestoreStatus.Success);
        Assert.False(afterRestoreStatus.Status!.HasVersionInventory);
        Assert.Equal(1, await fixture.ScalarAsync("SELECT count(*) FROM fluxvault.versions WHERE entry_kind = 'File'"));
    }

    [Theory]
    [InlineData("Off")]
    [InlineData("Zstd")]
    public async Task Corruption_through_private_pipe_preserves_destination_and_refuses_false_capture(string compression)
    {
        await using var fixture = await DisposablePostgreSqlFixture.CreateAsync();
        await File.WriteAllTextAsync(fixture.Files.Source, new string('a', 200_000));
        var captured = await PostgreSqlRepositoryIntegrityTests.Create(fixture).CommitAsync(
            PostgreSqlRepositoryIntegrityTests.Request(fixture, await File.ReadAllBytesAsync(fixture.Files.Source), compression == "Zstd"));
        var chunk = captured.Manifest.Chunks[0];
        var path = Path.Combine(fixture.Files.Repository, "chunks", chunk.Digest[..2], chunk.Digest + ".chunk");
        var stored = await File.ReadAllBytesAsync(path);
        stored[0] ^= 0xff;
        await File.WriteAllBytesAsync(path, stored);
        var pipe = "FluxVault.Integrity." + Guid.NewGuid().ToString("N");
        await using var server = fixture.Files.Start("serve", fixture.HostArguments.Concat(new[] { "--pipe", pipe }).ToArray());
        await server.WaitForReadyAsync();
        var output = Path.Combine(fixture.Files.Root, "restored.bin");
        await File.WriteAllTextAsync(output, "new work");
        var response = await CreateClient(pipe).SendAsync(FluxVaultIpcRequest.RestoreVersion(captured.Manifest.VersionId, output));
        Assert.False(response.Success);
        Assert.Null(response.RestoreResult);
        Assert.Equal("new work", await File.ReadAllTextAsync(output));
        await using var capture = fixture.Files.Start("capture", fixture.HostArguments.Concat(new[] { "--source", "working/source.bin" }).ToArray());
        var refused = await capture.CompleteAsync();
        Assert.Equal(3, refused.ExitCode);
        Assert.Equal(1, await fixture.ScalarAsync("SELECT count(*) FROM fluxvault.versions"));
    }

    [Fact]
    public async Task Fast_status_answers_while_a_real_restore_holds_the_content_lease()
    {
        await using var fixture = await DisposablePostgreSqlFixture.CreateAsync();
        var captured = await PostgreSqlRepositoryIntegrityTests.Create(fixture).CommitAsync(
            PostgreSqlRepositoryIntegrityTests.Request(fixture, await File.ReadAllBytesAsync(fixture.Files.Source), false));
        var pipe = "FluxVault.Integrity." + Guid.NewGuid().ToString("N");
        await using var server = fixture.Files.Start("serve", fixture.HostArguments.Concat(new[] { "--pipe", pipe, "--gate", "BeforeRestorePublication" }).ToArray());
        await server.WaitForReadyAsync();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var client = CreateClient(pipe);
        var restoring = client.SendAsync(FluxVaultIpcRequest.RestoreVersion(captured.Manifest.VersionId, Path.Combine(fixture.Files.Root, "restored.bin")), cancellation.Token);
        await server.WaitForGateAsync();
        using var statusDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var status = await client.SendAsync(FluxVaultIpcRequest.GetStatus(statusDetailLevel: FluxVaultStatusDetailLevel.Fast), statusDeadline.Token);
        Assert.True(status.Success);
        Assert.NotNull(status.Status);
        Assert.False(File.Exists(Path.Combine(fixture.Files.Root, "restored.bin")));
        await server.KillAsync();
        await Assert.ThrowsAnyAsync<Exception>(() => restoring);
    }

    [Fact]
    public async Task A_folder_restore_through_private_pipe_refuses_an_existing_destination()
    {
        await using var fixture = await DisposablePostgreSqlFixture.CreateAsync();
        var repo = PostgreSqlRepositoryIntegrityTests.Create(fixture);
        await repo.CommitAsync(PostgreSqlRepositoryIntegrityTests.Request(fixture, await File.ReadAllBytesAsync(fixture.Files.Source), false)
            with { WatchedFolderPath = Path.GetDirectoryName(fixture.Files.Source) });
        var folder = (await fixture.Store.ListManifestsAsync()).First(manifest => manifest.EntryKind == RepositoryEntryKind.Folder);
        var output = Path.Combine(fixture.Files.Root, "existing");
        Directory.CreateDirectory(output);
        var sentinel = Path.Combine(output, "sentinel.txt");
        await File.WriteAllTextAsync(sentinel, "keep");
        var pipe = "FluxVault.Integrity." + Guid.NewGuid().ToString("N");
        await using var server = fixture.Files.Start("serve", fixture.HostArguments.Concat(new[] { "--pipe", pipe }).ToArray());
        await server.WaitForReadyAsync();
        var response = await CreateClient(pipe).SendAsync(FluxVaultIpcRequest.RestoreVersion(folder.VersionId, output));
        Assert.False(response.Success);
        Assert.Contains("new destination", response.ErrorMessage);
        Assert.Equal("keep", await File.ReadAllTextAsync(sentinel));
        Assert.Single(Directory.EnumerateFiles(output));
    }

    private static NamedPipeFluxVaultClient CreateClient(string pipe)
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new(WindowsFluxVaultPipeClientFactory.ForPrivateFixture(pipe, identity.User!.Value));
    }

    private static async Task<byte[]> Hash(string path)
    { await using var stream = File.OpenRead(path); return await SHA256.HashDataAsync(stream); }
}
