using System.Text;
using FluxVault.Abstractions.Capture;
using FluxVault.Abstractions.Configuration;
using FluxVault.Abstractions.Ipc;
using FluxVault.Abstractions.Storage;
using FluxVault.Core.Configuration;
using FluxVault.Core.Service;
using FluxVault.Core.Storage;
using FluxVault.Core.Storage.Metadata;

namespace FluxVault.Core.Tests;

public sealed class AuthorisedSelectionRecoveryTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Preview_uses_caller_output_inspection_without_preparing_or_publishing(bool folder)
    {
        using var fixture = await Fixture.CreateAsync(folder);
        var output = new Output { Exists = true };
        var response = await fixture.Operations.HandleRestoreSelectionAsync(FluxVaultIpcRequest.PreviewRestoreSelection(
            fixture.Source, folder, RestoreSelectionDestinationMode.Elsewhere, fixture.Destination), output);
        Assert.True(response.Success); Assert.Equal(1, response.RestoreSelection!.FileCount);
        Assert.Equal(1, response.RestoreSelection.ConflictCount); Assert.Equal(0, response.RestoreSelection.RestoredCount);
        Assert.Null(response.RestoreResult); Assert.Equal(0, output.CreateCount); Assert.Equal(1, output.InspectCount);
        Assert.False(Directory.Exists(fixture.Destination));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task File_or_folder_recovery_uses_owned_target_and_retains_actual_verification(bool folder, bool original)
    {
        using var fixture = await Fixture.CreateAsync(folder);
        var destination = original ? fixture.Source : fixture.Destination;
        var output = new Output();
        var response = await fixture.Operations.HandleRestoreSelectionAsync(FluxVaultIpcRequest.RunRestoreSelection(fixture.Source, folder,
            original ? RestoreSelectionDestinationMode.Original : RestoreSelectionDestinationMode.Elsewhere, destination, true), output);
        Assert.True(response.Success); Assert.Equal(destination, response.OutputPath);
        Assert.Equal(1, response.RestoreSelection!.RestoredCount); Assert.Equal(10, response.RestoreResult!.VerifiedLogicalBytes);
        Assert.Equal(1, response.RestoreResult.RestoredFileCount); Assert.Equal(["Publication warning"], response.RestoreSelection.Warnings);
        Assert.Equal("first file", Encoding.UTF8.GetString(output.Target!.Files[folder ? "a.txt" : ""]));
        Assert.True(output.Target.Disposed); Assert.False(File.Exists(destination)); Assert.False(Directory.Exists(destination));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Existing_unconfirmed_file_or_any_existing_folder_is_refused_before_output_creation(bool folder)
    {
        using var fixture = await Fixture.CreateAsync(folder);
        var output = new Output { Exists = true };
        var response = await fixture.Operations.HandleRestoreSelectionAsync(FluxVaultIpcRequest.RunRestoreSelection(fixture.Source, folder,
            RestoreSelectionDestinationMode.Elsewhere, fixture.Destination, overwriteConfirmed: folder), output);
        Assert.False(response.Success); Assert.Equal(FluxVaultIpcErrorCode.InvalidRequest, response.ErrorCode);
        Assert.Equal(0, output.CreateCount); Assert.Null(response.RestoreResult);
    }

    [Fact]
    public async Task Unavailable_output_inspection_cannot_be_treated_as_missing()
    {
        using var fixture = await Fixture.CreateAsync(false);
        var output = new Output { InspectionUnavailable = true };
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => fixture.Operations.HandleRestoreSelectionAsync(FluxVaultIpcRequest.RunRestoreSelection(
            fixture.Source, false, RestoreSelectionDestinationMode.Elsewhere, fixture.Destination, true), output));
        Assert.Equal(0, output.CreateCount);
    }

    [Fact]
    public async Task Fallback_groups_history_before_ignoring_tombstones_and_publishes_latest_live_files_once()
    {
        using var fixture = await Fixture.CreateAsync(false);
        var root = Path.GetDirectoryName(fixture.Source)!;
        var deleted = Path.Combine(root, "removed.txt");
        await fixture.Repository.CommitAsync(RepositoryIntegrityTests.Request("deleted bytes") with { SourcePath = deleted });
        var tombstone = await fixture.Repository.RecordDeletionAsync(new("test", root, deleted, false, DateTimeOffset.UtcNow.AddSeconds(2)));
        // Deliberately remove generated folder snapshots to exercise the legacy per-file fallback.
        foreach (var folder in (await fixture.Repository.ListVersionsAsync()).Where(version => version.EntryKind == RepositoryEntryKind.Folder))
            File.Delete(Path.Combine(fixture.Workspace.RepositoryPath, "manifests", folder.VersionId + ".json"));
        Assert.NotNull(tombstone);
        var output = new Output();
        var response = await fixture.Operations.HandleRestoreSelectionAsync(FluxVaultIpcRequest.RunRestoreSelection(root, true,
            RestoreSelectionDestinationMode.Elsewhere, fixture.Destination, true), output);
        Assert.True(response.Success); Assert.Equal(1, response.RestoreSelection!.FileCount); Assert.Equal(1, response.RestoreResult!.RestoredFileCount);
        Assert.Equal(["a.txt"], output.Target!.Files.Keys); Assert.Equal(1, output.Target.PublishCount);
    }

    [Fact]
    public async Task Explicit_tombstoned_file_still_recovers_its_validated_last_good_target()
    {
        using var fixture = await Fixture.CreateAsync(false);
        await fixture.Repository.RecordDeletionAsync(new("test", Path.GetDirectoryName(fixture.Source)!, fixture.Source, false, DateTimeOffset.UtcNow.AddSeconds(2)));
        var output = new Output();
        var response = await fixture.Operations.HandleRestoreSelectionAsync(FluxVaultIpcRequest.RunRestoreSelection(fixture.Source, false,
            RestoreSelectionDestinationMode.Elsewhere, fixture.Destination, true), output);
        Assert.True(response.Success); Assert.Equal("first file", Encoding.UTF8.GetString(output.Target!.Files[""]));
    }

    [Fact]
    public async Task Missing_history_cannot_publish_an_empty_success()
    {
        using var fixture = await Fixture.CreateAsync(false);
        var output = new Output();
        var response = await fixture.Operations.HandleRestoreSelectionAsync(FluxVaultIpcRequest.RunRestoreSelection(Path.Combine(fixture.Workspace.RootPath, "never-protected"), true,
            RestoreSelectionDestinationMode.Elsewhere, fixture.Destination, true), output);
        Assert.False(response.Success); Assert.Contains("history", response.ErrorMessage); Assert.Equal(0, output.CreateCount);
    }

    private sealed class Fixture(TemporaryWorkspace workspace, FileSystemChunkRepository repository, FluxVaultOperations operations, string source) : IDisposable
    {
        internal TemporaryWorkspace Workspace => workspace; internal FileSystemChunkRepository Repository => repository;
        internal FluxVaultOperations Operations => operations; internal string Source => source;
        internal string Destination => Path.Combine(workspace.RootPath, "recovery");
        internal static async Task<Fixture> CreateAsync(bool folder)
        {
            var workspace = TemporaryWorkspace.Create();
            var root = Path.Combine(workspace.RootPath, "working"); var path = Path.Combine(root, "a.txt");
            var repository = RepositoryIntegrityTests.Create(workspace.RepositoryPath);
            await repository.CommitAsync(RepositoryIntegrityTests.Request("first file") with { SourcePath = path, WatchedFolderPath = folder ? root : null });
            var store = new FileFluxVaultConfigurationStore(Path.Combine(workspace.RootPath, "config.json"), workspace.RootPath);
            await store.SaveAsync(FluxVaultConfiguration.CreateDefault(workspace.RootPath));
            var operations = new FluxVaultOperations(store, new NoCapture(), metadataStoreFactory: _ => new InMemoryRepositoryMetadataStore(), repositoryFactory: _ => repository);
            return new(workspace, repository, operations, folder ? root : path);
        }
        public void Dispose() => workspace.Dispose();
    }
    private sealed class NoCapture : IFileCaptureProvider
    { public Task<FileCaptureResult> CaptureAsync(FileCaptureRequest request, CancellationToken cancellationToken = default) => throw new InvalidOperationException(); }
    private sealed class Output : IRestoreSelectionOutputAccess
    {
        internal bool Exists, InspectionUnavailable; internal int InspectCount, CreateCount; internal SelectionForestRecoveryTests.Target? Target;
        public Task<bool> ExistsAsync(string path, RepositoryEntryKind expectedKind, CancellationToken cancellationToken)
        { InspectCount++; if (InspectionUnavailable) throw new UnauthorizedAccessException("Caller cannot inspect output"); return Task.FromResult(Exists); }
        public Task<IRepositoryRestoreTarget> CreateAsync(string path, RepositoryEntryKind kind, bool overwriteConfirmed, CancellationToken cancellationToken)
        { CreateCount++; Target = new(path) { Warning = "Publication warning", ExpectedKind = kind }; return Task.FromResult<IRepositoryRestoreTarget>(Target); }
    }
}
