using FluxVault.Abstractions.Storage;
using FluxVault.Core.Storage.Metadata;

namespace FluxVault.Core.Tests;

public sealed class PostgreSqlMetadataStoreTests
{
    [Fact]
    public void Schema_contains_core_whole_pc_metadata_tables()
    {
        var sql = PostgreSqlMetadataSchema.CreateSchemaSql;

        Assert.Contains("CREATE TABLE IF NOT EXISTS fluxvault.paths", sql);
        Assert.Contains("CREATE TABLE IF NOT EXISTS fluxvault.versions", sql);
        Assert.Contains("manifest_json jsonb NOT NULL", sql);
        Assert.Contains("CREATE TABLE IF NOT EXISTS fluxvault.version_chunks", sql);
        Assert.Contains("CREATE TABLE IF NOT EXISTS fluxvault.current_entries", sql);
        Assert.Contains("CREATE TABLE IF NOT EXISTS fluxvault.capture_queue", sql);
        Assert.Contains("CREATE TABLE IF NOT EXISTS fluxvault.metadata_outbox", sql);
        Assert.Contains("CREATE TABLE IF NOT EXISTS fluxvault.peer_operations", sql);
        Assert.Contains("CREATE INDEX IF NOT EXISTS ix_versions_source_path_captured", sql);
        Assert.Contains("CREATE UNIQUE INDEX IF NOT EXISTS ux_current_entries_path_kind", sql);
    }

    [Fact]
    public void Manifest_projection_preserves_version_chunks_and_folder_entries()
    {
        var capturedAt = new DateTimeOffset(2026, 5, 15, 8, 30, 0, TimeSpan.Zero);
        var manifest = new FileVersionManifest(
            VersionId: "version-1",
            WatchedFolderId: "docs",
            SourcePath: @"D:\Docs\Folder",
            CapturedAtUtc: capturedAt,
            Consistency: CaptureConsistency.BestEffort,
            LogicalLength: 42,
            Chunks:
            [
                new ManifestChunk("digest-a", 0, 10, 8, ChunkEncoding.Zstd),
                new ManifestChunk("digest-b", 10, 32, 30, ChunkEncoding.Raw)
            ],
            ParentVersionIds: ["parent-1"],
            ContentSignature: "sig-1",
            EntryKind: RepositoryEntryKind.Folder,
            FolderEntries:
            [
                new FolderVersionEntry("note.txt", @"D:\Docs\Folder\note.txt", RepositoryEntryKind.File, "child-1", false, 42, capturedAt)
            ]);

        var projection = MetadataManifestProjection.FromManifest(manifest);

        Assert.Equal("version-1", projection.Version.VersionId);
        Assert.Equal(@"D:\Docs\Folder", projection.Path.SourcePath);
        Assert.Equal(["parent-1"], projection.LineageEdges.Select(edge => edge.ParentVersionId));
        Assert.Equal(["digest-a", "digest-b"], projection.Chunks.Select(chunk => chunk.Digest));
        Assert.Equal([0, 1], projection.VersionChunks.Select(chunk => chunk.ChunkOrdinal));
        var folderEntry = Assert.Single(projection.FolderEntries);
        Assert.Equal("note.txt", folderEntry.Name);
        Assert.Equal("child-1", folderEntry.ChildVersionId);
    }

    [Fact]
    public async Task Manifest_importer_reads_existing_manifest_files_into_projection_batches()
    {
        using var workspace = TemporaryWorkspace.Create();
        var manifestsPath = Path.Combine(workspace.RepositoryPath, "manifests");
        Directory.CreateDirectory(manifestsPath);
        await File.WriteAllTextAsync(
            Path.Combine(manifestsPath, "version-1.json"),
            """
            {
              "versionId": "version-1",
              "watchedFolderId": "docs",
              "sourcePath": "D:\\Docs\\note.txt",
              "capturedAtUtc": "2026-05-15T08:30:00+00:00",
              "consistency": "BestEffort",
              "logicalLength": 10,
              "chunks": [
                {
                  "digest": "digest-a",
                  "offset": 0,
                  "length": 10,
                  "storedLength": 8,
                  "encoding": "Zstd"
                }
              ],
              "operationType": "Capture",
              "entryKind": "File"
            }
            """);

        var projections = await ManifestMetadataImporter.ImportAsync(workspace.RepositoryPath);

        var projection = Assert.Single(projections);
        Assert.Equal("version-1", projection.Version.VersionId);
        Assert.Equal("digest-a", Assert.Single(projection.Chunks).Digest);
    }
}
