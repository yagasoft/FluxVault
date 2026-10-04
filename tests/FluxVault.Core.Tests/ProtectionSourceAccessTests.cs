using System.Text;
using FluxVault.Abstractions.Capture;
using FluxVault.Abstractions.Configuration;
using FluxVault.Abstractions.Ipc;
using FluxVault.Abstractions.Policies;
using FluxVault.Abstractions.Storage;
using FluxVault.Core.Capture;
using FluxVault.Core.Configuration;
using FluxVault.Core.Service;
using FluxVault.Core.Storage;
using FluxVault.Core.Storage.Metadata;

namespace FluxVault.Core.Tests;

public sealed class ProtectionSourceAccessTests
{
    [Fact]
    public async Task Direct_adapter_accepts_an_owned_file_under_its_actual_drive_root()
    {
        using var workspace = TemporaryWorkspace.Create();
        var file = Path.Combine(workspace.RootPath, "owned.txt"); await File.WriteAllTextAsync(file, "owned bytes");
        var result = new FileSystemProtectionSourceAccess().Inspect(Path.GetPathRoot(file)!, file, RepositoryEntryKind.File);
        Assert.Equal(ProtectionSourceAvailability.Present, result.Availability); Assert.Equal(11, result.Length);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Selected_root_vanishing_after_its_admission_probe_is_never_tombstoned(bool targeted)
    {
        using var workspace = TemporaryWorkspace.Create();
        var root = Path.Combine(workspace.RootPath, "working"); Directory.CreateDirectory(root);
        var path = Path.Combine(root, "document.txt"); var repo = RepositoryIntegrityTests.Create(workspace.RepositoryPath);
        await Seed(repo, root, path); var before = (await repo.ListVersionsAsync()).Select(item => item.VersionId).Order().ToArray();
        var capture = new Capture();
        var access = new VanishingRoot(root, targeted ? 1 : 2);
        var operations = await Operations(workspace, root, repo, access, capture);
        var result = targeted ? await operations.RunBackupForFilesAsync([root]) : await operations.RunBackupNowAsync();
        Assert.False(result.Success); Assert.True(result.FailedFileCount > 0); Assert.Equal(0, result.RecordedDeletionCount);
        Assert.True(access.MissingObserved); Assert.Equal(0, capture.Count);
        Assert.Equal(before, (await repo.ListVersionsAsync()).Select(item => item.VersionId).Order().ToArray());
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Root_disappearance_between_admission_and_child_probe_retains_history(bool targeted)
    {
        using var workspace = TemporaryWorkspace.Create();
        var root = Path.Combine(workspace.RootPath, "working"); Directory.CreateDirectory(root);
        var path = Path.Combine(root, "document.txt"); var repo = RepositoryIntegrityTests.Create(workspace.RepositoryPath);
        await Seed(repo, root, path); var before = (await repo.ListVersionsAsync()).Select(item => item.VersionId).Order().ToArray();
        var moved = false;
        var access = new FileSystemProtectionSourceAccess(requested =>
        { Assert.Equal(path, requested); if (!moved) { Directory.Move(root, root + "-moved"); moved = true; } });
        var capture = new Capture(); var operations = await Operations(workspace, root, repo, access, capture);
        var result = targeted ? await operations.RunBackupForFilesAsync([path]) : await operations.RunBackupNowAsync();
        Assert.True(moved); Assert.False(result.Success); Assert.True(result.FailedFileCount > 0);
        Assert.Equal(0, result.RecordedDeletionCount); Assert.Equal(0, capture.Count);
        Assert.Equal(before, (await repo.ListVersionsAsync()).Select(item => item.VersionId).Order().ToArray());
        Assert.All(await repo.ListLatestEntriesAsync(), item => Assert.False(item.IsDeleted));
    }

    [Fact]
    public async Task Real_present_root_with_missing_child_still_records_confirmed_deletion()
    {
        using var workspace = TemporaryWorkspace.Create();
        var root = Path.Combine(workspace.RootPath, "working"); Directory.CreateDirectory(root);
        var path = Path.Combine(root, "document.txt"); var repo = RepositoryIntegrityTests.Create(workspace.RepositoryPath);
        await Seed(repo, root, path); var capture = new Capture();
        var operations = await Operations(workspace, root, repo, new FileSystemProtectionSourceAccess(), capture);
        var result = await operations.RunBackupNowAsync();
        Assert.True(result.Success); Assert.Equal(1, result.RecordedDeletionCount); Assert.Equal(0, capture.Count);
        Assert.True((await repo.ListLatestEntriesAsync()).Single(item => item.SourcePath == path).IsDeleted);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Renamed_real_protection_root_fails_without_tombstoning_its_history(bool targeted)
    {
        using var workspace = TemporaryWorkspace.Create();
        var root = Path.Combine(workspace.RootPath, "working"); Directory.CreateDirectory(root);
        var path = Path.Combine(root, "document.txt"); var repo = RepositoryIntegrityTests.Create(workspace.RepositoryPath);
        await Seed(repo, root, path); var before = (await repo.ListVersionsAsync()).Select(item => item.VersionId).Order().ToArray();
        var capture = new Capture(); var operations = await Operations(workspace, root, repo, new FileSystemProtectionSourceAccess(), capture);
        Directory.Move(root, root + "-moved");
        var result = targeted ? await operations.RunBackupForFilesAsync([path]) : await operations.RunBackupNowAsync();
        Assert.False(result.Success); Assert.True(result.FailedFileCount > 0); Assert.Equal(0, result.RecordedDeletionCount);
        Assert.Equal(0, capture.Count); Assert.Equal(before, (await repo.ListVersionsAsync()).Select(item => item.VersionId).Order().ToArray());
        Assert.All(await repo.ListLatestEntriesAsync(), item => Assert.False(item.IsDeleted));
    }
    [Fact]
    public async Task Interrupted_enumeration_reports_failure_and_preserves_unobserved_history()
    {
        using var workspace = TemporaryWorkspace.Create();
        var root = Path.Combine(workspace.RootPath, "working"); Directory.CreateDirectory(root);
        var path = Path.Combine(root, "document.txt");
        var repo = RepositoryIntegrityTests.Create(workspace.RepositoryPath); await Seed(repo, root, path);
        var before = (await repo.ListVersionsAsync()).Select(item => item.VersionId).Order().ToArray();
        var capture = new Capture();
        var access = new Access(root, path, ProtectionSourceAvailability.Unavailable) { EnumerationFailure = true };
        var operations = await Operations(workspace, root, repo, access, capture);
        var result = await operations.RunBackupNowAsync();
        Assert.False(result.Success); Assert.Contains("enumeration interrupted", result.Message);
        Assert.Equal(0, result.RecordedDeletionCount); Assert.Equal(0, capture.Count);
        Assert.Equal(before, (await repo.ListVersionsAsync()).Select(item => item.VersionId).Order().ToArray());
    }

    [Fact]
    public async Task Failed_capture_after_metadata_inspection_reports_failure_without_removing_selection()
    {
        using var workspace = TemporaryWorkspace.Create();
        var root = Path.Combine(workspace.RootPath, "working"); Directory.CreateDirectory(root);
        var path = Path.Combine(root, "document.txt");
        var repo = RepositoryIntegrityTests.Create(workspace.RepositoryPath);
        var capture = new Capture { Fail = true };
        var access = new Access(root, path, ProtectionSourceAvailability.Present) { EnumerateCandidate = true };
        var operations = await Operations(workspace, root, repo, access, capture);
        var result = await operations.RunBackupNowAsync();
        Assert.False(result.Success); Assert.Contains("capture access denied", result.Message);
        Assert.Equal(1, capture.Count); Assert.Equal(0, result.RecordedDeletionCount);
        Assert.Empty(await repo.ListVersionsAsync());
        Assert.True((await operations.GetStatusAsync(FluxVaultStatusDetailLevel.Fast)).Configuration.WatchedFolders.Single().IsEnabled);
    }

    [Fact]
    public async Task Cancellation_reaches_source_enumeration_and_disposes_the_enumerator()
    {
        using var workspace = TemporaryWorkspace.Create();
        var root = Path.Combine(workspace.RootPath, "working"); Directory.CreateDirectory(root);
        var path = Path.Combine(root, "document.txt");
        var repo = RepositoryIntegrityTests.Create(workspace.RepositoryPath); var capture = new Capture();
        using var cancelled = new CancellationTokenSource();
        var access = new Access(root, path, ProtectionSourceAvailability.Present) { CancelScan = cancelled };
        var operations = await Operations(workspace, root, repo, access, capture);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operations.RunBackupNowAsync(cancelled.Token));
        Assert.True(access.EnumeratorDisposed); Assert.Equal(0, capture.Count); Assert.Empty(await repo.ListVersionsAsync());
    }
    [Fact]
    public async Task Unavailable_root_is_reported_as_unavailable_in_status_and_backup()
    {
        using var workspace = TemporaryWorkspace.Create();
        var root = Path.Combine(workspace.RootPath, "working"); Directory.CreateDirectory(root);
        var path = Path.Combine(root, "document.txt"); await File.WriteAllTextAsync(path, "actual source bytes");
        var repo = RepositoryIntegrityTests.Create(workspace.RepositoryPath);
        var capture = new Capture();
        var access = new Access(root, path, ProtectionSourceAvailability.Unavailable) { RootUnavailable = true, EnumerateCandidate = true };
        var operations = await Operations(workspace, root, repo, access, capture);
        var status = await operations.GetStatusAsync(FluxVaultStatusDetailLevel.Fast);
        var folder = Assert.Single(status.WatchedFolders);
        Assert.False(folder.Exists);
        Assert.Contains("unavailable", folder.Status, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("caller denied", folder.Status);
        var result = await operations.RunBackupNowAsync();
        Assert.False(result.Success); Assert.True(result.FailedFileCount > 0);
        Assert.Equal(0, capture.Count); Assert.Empty(await repo.ListVersionsAsync());
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Unavailable_tracked_source_preserves_history_and_reports_failed_backup(bool targeted)
    {
        using var workspace = TemporaryWorkspace.Create();
        var root = Path.Combine(workspace.RootPath, "working"); Directory.CreateDirectory(root);
        var path = Path.Combine(root, "document.txt");
        var repo = RepositoryIntegrityTests.Create(workspace.RepositoryPath);
        await Seed(repo, root, path);
        var before = (await repo.ListVersionsAsync()).Select(item => item.VersionId).Order().ToArray();
        var access = new Access(root, path, ProtectionSourceAvailability.Unavailable);
        var capture = new Capture();
        var operations = await Operations(workspace, root, repo, access, capture);
        var result = targeted ? await operations.RunBackupForFilesAsync([path]) : await operations.RunBackupNowAsync();
        Assert.False(result.Success);
        Assert.True(result.FailedFileCount > 0);
        Assert.Contains("caller denied", result.Message);
        Assert.Equal(0, result.RecordedDeletionCount);
        Assert.Equal(0, capture.Count);
        Assert.Equal(before, (await repo.ListVersionsAsync()).Select(item => item.VersionId).Order().ToArray());
        Assert.False((await repo.ListLatestEntriesAsync()).Single(item => item.SourcePath == path).IsDeleted);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Confirmed_missing_source_records_deletion_without_capture(bool targeted)
    {
        using var workspace = TemporaryWorkspace.Create();
        var root = Path.Combine(workspace.RootPath, "working"); Directory.CreateDirectory(root);
        var path = Path.Combine(root, "document.txt");
        var repo = RepositoryIntegrityTests.Create(workspace.RepositoryPath);
        await Seed(repo, root, path);
        var capture = new Capture();
        var operations = await Operations(workspace, root, repo, new Access(root, path, ProtectionSourceAvailability.Missing), capture);
        var result = targeted ? await operations.RunBackupForFilesAsync([path]) : await operations.RunBackupNowAsync();
        Assert.True(result.Success);
        Assert.Equal(1, result.RecordedDeletionCount);
        Assert.Equal(0, capture.Count);
        Assert.True((await repo.ListLatestEntriesAsync()).Single(item => item.SourcePath == path).IsDeleted);
    }

    [Fact]
    public async Task Unavailable_candidate_metadata_is_not_captured_or_silently_skipped()
    {
        using var workspace = TemporaryWorkspace.Create();
        var root = Path.Combine(workspace.RootPath, "working"); Directory.CreateDirectory(root);
        var path = Path.Combine(root, "document.txt"); await File.WriteAllTextAsync(path, "actual source bytes");
        var repo = RepositoryIntegrityTests.Create(workspace.RepositoryPath);
        var capture = new Capture();
        var access = new Access(root, path, ProtectionSourceAvailability.Unavailable) { EnumerateCandidate = true };
        var operations = await Operations(workspace, root, repo, access, capture);
        var result = await operations.RunBackupNowAsync();
        Assert.False(result.Success);
        Assert.True(result.FailedFileCount > 0);
        Assert.Contains("caller denied", result.Message);
        Assert.Equal(0, capture.Count);
        Assert.Empty(await repo.ListVersionsAsync());
    }

    private static async Task Seed(FileSystemChunkRepository repo, string root, string path) =>
        await repo.CommitAsync(new("source", path, DateTimeOffset.UtcNow.AddDays(-1), CaptureConsistency.BestEffort,
            CompressionPreference.Off, 1, new MemoryStream(Encoding.UTF8.GetBytes("accepted bytes")), WatchedFolderPath: root));

    private static async Task<FluxVaultOperations> Operations(TemporaryWorkspace workspace, string root,
        FileSystemChunkRepository repo, IProtectionSourceAccess access, Capture capture)
    {
        var configuration = FluxVaultConfiguration.CreateDefault(workspace.RootPath) with
        {
            RepositoryPath = workspace.RepositoryPath,
            WatchedFolders = [new("source", root, true, ["*.txt"], [], CompressionPreference.Off, ResourceProfile.Balanced, true)]
        };
        var store = new FileFluxVaultConfigurationStore(Path.Combine(workspace.RootPath, "config.json"), workspace.RootPath);
        await store.SaveAsync(configuration);
        return new(store, capture, metadataStoreFactory: _ => new InMemoryRepositoryMetadataStore(),
            repositoryFactory: _ => repo, sourceAccess: access);
    }

    private sealed class Access(string root, string path, ProtectionSourceAvailability availability) : IProtectionSourceAccess
    {
        internal bool EnumerateCandidate, RootUnavailable, EnumerationFailure, EnumeratorDisposed;
        internal CancellationTokenSource? CancelScan;
        public ProtectionSourceInspection Inspect(string protectionRoot, string requested, RepositoryEntryKind kind, CancellationToken token = default)
        {
            Assert.Equal(root, protectionRoot);
            if (requested == root && RootUnavailable) return new(ProtectionSourceAvailability.Unavailable, FailureReason: "caller denied source directory");
            return requested == path ? new(availability, kind, 13, DateTimeOffset.UtcNow, FailureReason: availability == ProtectionSourceAvailability.Unavailable ? "caller denied source metadata" : null) :
                new(ProtectionSourceAvailability.Present, kind);
        }
        public IEnumerable<ProtectionSourceCandidate> EnumerateDirectory(string protectionRoot, string directory, CancellationToken token = default)
        {
            Assert.Equal(root, protectionRoot); Assert.Equal(root, directory);
            try
            {
                if (EnumerationFailure) throw new IOException("enumeration interrupted");
                CancelScan?.Cancel(); token.ThrowIfCancellationRequested();
                if (EnumerateCandidate) yield return new(path, RepositoryEntryKind.File);
            }
            finally { EnumeratorDisposed = true; }
        }
    }
    private sealed class Capture : IFileCaptureProvider
    {
        internal int Count;
        internal bool Fail;
        public Task<FileCaptureResult> CaptureAsync(FileCaptureRequest request, CancellationToken token = default)
        { Count++; return Fail ? Task.FromResult(FileCaptureResult.Failed("capture access denied")) : new NormalFileCaptureProvider().CaptureAsync(request, token); }
    }
    private sealed class VanishingRoot(string root, int initiallyPresent) : IProtectionSourceAccess
    {
        private int probes;
        internal bool MissingObserved;
        public ProtectionSourceInspection Inspect(string protectionRoot, string path, RepositoryEntryKind kind, CancellationToken token = default)
        {
            if (path != root) return new(ProtectionSourceAvailability.Unavailable, FailureReason: "root vanished");
            if (++probes <= initiallyPresent) return new(ProtectionSourceAvailability.Present, RepositoryEntryKind.Folder);
            MissingObserved = true; return new(ProtectionSourceAvailability.Missing);
        }
        public IEnumerable<ProtectionSourceCandidate> EnumerateDirectory(string protectionRoot, string directory, CancellationToken token = default) => [];
    }
}
