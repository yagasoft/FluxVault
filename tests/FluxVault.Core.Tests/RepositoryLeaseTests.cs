using System.Text.Json;
using FluxVault.Core.Storage.Integrity;

namespace FluxVault.Core.Tests;

public sealed class RepositoryLeaseTests
{
    [Fact]
    public async Task Cancellation_at_the_process_gate_does_not_release_another_owner()
    {
        using var workspace = TemporaryWorkspace.Create();
        var first = await RepositoryLeaseSet.AcquireAsync(workspace.RepositoryPath, [], MirrorLeaseMode.None, default);
        using var cancellation = new CancellationTokenSource();
        var waiting = RepositoryLeaseSet.AcquireAsync(workspace.RepositoryPath, [], MirrorLeaseMode.None, cancellation.Token).AsTask();
        Assert.False(waiting.IsCompleted);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
        Assert.Throws<IOException>(() => new FileStream(Path.Combine(workspace.RepositoryPath, StorageOwnership.LockName),
            FileMode.Open, FileAccess.ReadWrite, FileShare.None));
        await first.DisposeAsync();
        await using var later = await RepositoryLeaseSet.AcquireAsync(workspace.RepositoryPath, [], MirrorLeaseMode.None, default);
        Assert.True(File.Exists(Path.Combine(workspace.RepositoryPath, StorageOwnership.LockName)));
    }

    [Fact]
    public async Task Partial_acquisition_failure_releases_every_handle()
    {
        using var workspace = TemporaryWorkspace.Create();
        var mirror = Path.Combine(workspace.RootPath, "z-mirror");
        Directory.CreateDirectory(mirror);
        using (var blocker = new FileStream(Path.Combine(mirror, StorageOwnership.LockName), FileMode.Create, FileAccess.ReadWrite, FileShare.None))
        {
            var exception = await Assert.ThrowsAsync<RepositoryIntegrityException>(() =>
                RepositoryLeaseSet.AcquireAsync(workspace.RepositoryPath, [mirror], MirrorLeaseMode.Required, default).AsTask());
            Assert.Equal(RepositoryIntegrityFailure.RepositoryBusy, exception.Code);
            await using var later = await RepositoryLeaseSet.AcquireAsync(workspace.RepositoryPath, [], MirrorLeaseMode.None, default);
        }
        await using var complete = await RepositoryLeaseSet.AcquireAsync(workspace.RepositoryPath, [mirror], MirrorLeaseMode.Required, default);
        Assert.Single(complete.MirrorRoots);
    }

    [Theory]
    [InlineData("malformed")]
    [InlineData("foreign")]
    [InlineData("role")]
    public async Task Unsafe_markers_are_rejected_and_preserved(string scenario)
    {
        using var workspace = TemporaryWorkspace.Create();
        var mirror = Path.Combine(workspace.RootPath, "mirror");
        string id;
        await using (var lease = await RepositoryLeaseSet.AcquireAsync(workspace.RepositoryPath, [], MirrorLeaseMode.None, default)) id = lease.StorageId;
        Directory.CreateDirectory(mirror);
        var marker = scenario == "malformed" ? "not json" : JsonSerializer.Serialize(new
        {
            formatVersion = 1, storageId = scenario == "foreign" ? Guid.NewGuid().ToString("N") : id,
            role = scenario == "role" ? "primary" : "mirror"
        });
        var markerPath = Path.Combine(mirror, StorageOwnership.MarkerName);
        await File.WriteAllTextAsync(markerPath, marker);
        await Assert.ThrowsAsync<RepositoryIntegrityException>(() =>
            RepositoryLeaseSet.AcquireAsync(workspace.RepositoryPath, [mirror], MirrorLeaseMode.Required, default).AsTask());
        Assert.Equal(marker, await File.ReadAllTextAsync(markerPath));
        await using var later = await RepositoryLeaseSet.AcquireAsync(workspace.RepositoryPath, [], MirrorLeaseMode.None, default);
    }

    [Fact]
    public async Task An_empty_recognised_storage_directory_can_be_initialised()
    {
        using var workspace = TemporaryWorkspace.Create();
        Directory.CreateDirectory(Path.Combine(workspace.RepositoryPath, "chunks"));
        await using var lease = await RepositoryLeaseSet.AcquireAsync(workspace.RepositoryPath, [], MirrorLeaseMode.None, default);
        Assert.NotEmpty(lease.StorageId);
    }
}
