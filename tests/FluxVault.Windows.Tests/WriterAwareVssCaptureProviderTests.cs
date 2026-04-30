using System.Text;
using FluxVault.Abstractions.Capture;
using FluxVault.Abstractions.Storage;
using FluxVault.Windows.Capture;

namespace FluxVault.Windows.Tests;

public sealed class WriterAwareVssCaptureProviderTests
{
    [Fact]
    public async Task Writer_covered_snapshot_returns_app_consistent_capture()
    {
        using var workspace = TemporaryTestFolder.Create();
        var source = Path.Combine(workspace.RootPath, "watched", "db.mdf");
        await File.WriteAllTextAsync(source, "live content");
        var coordinator = new FakeVssSnapshotCoordinator(
            source,
            "shadow content",
            [new VssWriterEvidence("SqlServerWriter", [Path.GetDirectoryName(source)!])]);
        var provider = new WriterAwareVssCaptureProvider(coordinator);

        await using var capture = await provider.CaptureAsync(new FileCaptureRequest(source));

        Assert.True(capture.Success);
        Assert.Equal(CaptureConsistency.AppConsistent, capture.Consistency);
        Assert.Contains("SqlServerWriter", capture.Message);
        using var reader = new StreamReader(capture.Content!, Encoding.UTF8);
        Assert.Equal("shadow content", await reader.ReadToEndAsync());
    }

    [Fact]
    public async Task Snapshot_without_matching_writer_coverage_returns_crash_consistent_capture()
    {
        using var workspace = TemporaryTestFolder.Create();
        var source = Path.Combine(workspace.RootPath, "watched", "draft.txt");
        await File.WriteAllTextAsync(source, "live content");
        var coordinator = new FakeVssSnapshotCoordinator(
            source,
            "shadow content",
            [new VssWriterEvidence("SystemWriter", [Path.Combine(workspace.RootPath, "system")])]);
        var provider = new WriterAwareVssCaptureProvider(coordinator);

        await using var capture = await provider.CaptureAsync(new FileCaptureRequest(source));

        Assert.True(capture.Success);
        Assert.Equal(CaptureConsistency.CrashConsistent, capture.Consistency);
        Assert.Contains("no VSS writer metadata covered", capture.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Writer_failure_is_reported_as_capture_failure_and_snapshot_cleanup_runs()
    {
        using var workspace = TemporaryTestFolder.Create();
        var source = Path.Combine(workspace.RootPath, "watched", "db.mdf");
        await File.WriteAllTextAsync(source, "live content");
        var coordinator = new FakeVssSnapshotCoordinator(
            VssSnapshotResult.Failed(
                "VSS writer SqlServerWriter failed during PrepareForBackup.",
                () =>
                {
                    return ValueTask.CompletedTask;
                }));
        var provider = new WriterAwareVssCaptureProvider(coordinator);

        await using var capture = await provider.CaptureAsync(new FileCaptureRequest(source));

        Assert.False(capture.Success);
        Assert.Contains("SqlServerWriter", capture.Message);
        Assert.Equal(1, coordinator.CleanupCount);
    }

    [Fact]
    public void Non_recursive_writer_wildcard_does_not_cover_nested_files()
    {
        using var workspace = TemporaryTestFolder.Create();
        var writerRoot = Path.Combine(workspace.RootPath, "watched");
        var writer = new VssWriterEvidence(
            "SqlServerWriter",
            [Path.Combine(writerRoot, "*.mdf")]);

        Assert.True(writer.Covers(Path.Combine(writerRoot, "db.mdf")));
        Assert.False(writer.Covers(Path.Combine(writerRoot, "nested", "db.mdf")));
    }

    [Fact]
    public void Recursive_writer_wildcard_covers_nested_files()
    {
        using var workspace = TemporaryTestFolder.Create();
        var writerRoot = Path.Combine(workspace.RootPath, "watched");
        var writer = new VssWriterEvidence(
            "SqlServerWriter",
            [Path.Combine(writerRoot, "**", "*.mdf")]);

        Assert.True(writer.Covers(Path.Combine(writerRoot, "db.mdf")));
        Assert.True(writer.Covers(Path.Combine(writerRoot, "nested", "db.mdf")));
        Assert.False(writer.Covers(Path.Combine(writerRoot, "nested", "notes.txt")));
    }

    private sealed class FakeVssSnapshotCoordinator : IVssSnapshotCoordinator
    {
        private readonly string? sourcePath;
        private readonly string? shadowContent;
        private readonly IReadOnlyList<VssWriterEvidence>? writers;
        private readonly VssSnapshotResult? result;

        public FakeVssSnapshotCoordinator(string sourcePath, string shadowContent, IReadOnlyList<VssWriterEvidence> writers)
        {
            this.sourcePath = Path.GetFullPath(sourcePath);
            this.shadowContent = shadowContent;
            this.writers = writers;
        }

        public FakeVssSnapshotCoordinator(VssSnapshotResult result)
        {
            this.result = result;
        }

        public int CleanupCount { get; private set; }

        public async Task<VssSnapshotResult> CreateSnapshotAsync(
            VssSnapshotRequest request,
            CancellationToken cancellationToken = default)
        {
            if (result is not null)
            {
                return result with { Cleanup = CountCleanupAsync };
            }

            Assert.Equal(sourcePath, Path.GetFullPath(request.SourcePath));
            var shadowRoot = Path.Combine(Path.GetDirectoryName(request.SourcePath)!, "..", "shadow-root");
            var shadowPath = WriterAwareVssCaptureProvider.BuildShadowPath(
                request.VolumeRoot,
                request.SourcePath,
                shadowRoot);
            Directory.CreateDirectory(Path.GetDirectoryName(shadowPath)!);
            await File.WriteAllTextAsync(shadowPath, shadowContent, cancellationToken);
            return VssSnapshotResult.Created(
                SnapshotId: Guid.NewGuid(),
                SnapshotDeviceObject: shadowRoot,
                Writers: writers ?? [],
                Message: "Writer-aware VSS snapshot created.",
                Cleanup: CountCleanupAsync);
        }

        private ValueTask CountCleanupAsync()
        {
            CleanupCount++;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class TemporaryTestFolder : IDisposable
    {
        private TemporaryTestFolder(string rootPath)
        {
            RootPath = rootPath;
            Directory.CreateDirectory(Path.Combine(rootPath, "watched"));
        }

        public string RootPath { get; }

        public static TemporaryTestFolder Create()
        {
            return new TemporaryTestFolder(Path.Combine(Path.GetTempPath(), "FluxVaultTests", Guid.NewGuid().ToString("N")));
        }

        public void Dispose()
        {
            if (Directory.Exists(RootPath))
            {
                Directory.Delete(RootPath, recursive: true);
            }
        }
    }
}
