using FluxVault.Abstractions.Capture;
using FluxVault.Abstractions.Storage;

namespace FluxVault.Windows.Capture;

public sealed class WriterAwareVssCaptureProvider : IFileCaptureProvider
{
    private readonly IVssSnapshotCoordinator coordinator;

    public WriterAwareVssCaptureProvider()
        : this(new WindowsVssSnapshotCoordinator())
    {
    }

    public WriterAwareVssCaptureProvider(IVssSnapshotCoordinator coordinator)
    {
        this.coordinator = coordinator;
    }

    public async Task<FileCaptureResult> CaptureAsync(
        FileCaptureRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var fullPath = Path.GetFullPath(request.SourcePath);
        var root = Path.GetPathRoot(fullPath);
        if (string.IsNullOrWhiteSpace(root))
        {
            return FileCaptureResult.Failed($"Cannot determine volume root for {fullPath}.");
        }

        var snapshot = await coordinator.CreateSnapshotAsync(
                new VssSnapshotRequest(fullPath, root),
                cancellationToken)
            .ConfigureAwait(false);
        if (!snapshot.Success)
        {
            if (snapshot.Cleanup is not null)
            {
                await snapshot.Cleanup().ConfigureAwait(false);
            }

            return FileCaptureResult.Failed(snapshot.Message);
        }

        if (string.IsNullOrWhiteSpace(snapshot.SnapshotDeviceObject))
        {
            await CleanupSnapshotAsync(snapshot).ConfigureAwait(false);
            return FileCaptureResult.Failed("VSS requester created a snapshot without a readable device path.");
        }

        var shadowPath = BuildShadowPath(root, fullPath, snapshot.SnapshotDeviceObject);
        try
        {
            var stream = new FileStream(
                shadowPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                bufferSize: 1024 * 128,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            var matchingWriters = snapshot.Writers
                .Where(writer => writer.Covers(fullPath))
                .Select(writer => writer.WriterName)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Order(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var consistency = matchingWriters.Length == 0
                ? CaptureConsistency.CrashConsistent
                : CaptureConsistency.AppConsistent;
            var message = matchingWriters.Length == 0
                ? "Captured from writer-aware VSS snapshot, but no VSS writer metadata covered this file; treating as crash-consistent."
                : $"Captured from writer-aware VSS snapshot. App-consistent writer coverage: {string.Join(", ", matchingWriters)}.";
            return FileCaptureResult.Captured(stream, consistency, message, () => CleanupSnapshotAsync(snapshot));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            await CleanupSnapshotAsync(snapshot).ConfigureAwait(false);
            return FileCaptureResult.Failed($"VSS snapshot read failed: {ex.Message}");
        }
    }

    public static string BuildShadowPath(string volumeRoot, string sourcePath, string shadowVolume)
    {
        var relativePath = Path.GetRelativePath(volumeRoot, sourcePath);
        return Path.Combine(shadowVolume.TrimEnd('\\', '/'), relativePath);
    }

    private static async ValueTask CleanupSnapshotAsync(VssSnapshotResult snapshot)
    {
        if (snapshot.Cleanup is not null)
        {
            await snapshot.Cleanup().ConfigureAwait(false);
        }
    }
}
