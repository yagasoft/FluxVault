using System.IO.Enumeration;

namespace FluxVault.Windows.Capture;

public interface IVssSnapshotCoordinator
{
    Task<VssSnapshotResult> CreateSnapshotAsync(
        VssSnapshotRequest request,
        CancellationToken cancellationToken = default);
}

public sealed record VssSnapshotRequest(string SourcePath, string VolumeRoot);

public sealed record VssWriterEvidence(string WriterName, IReadOnlyList<string> CoveredPaths)
{
    public bool Covers(string sourcePath)
    {
        var fullPath = Path.GetFullPath(sourcePath);
        return CoveredPaths.Any(path => CoversPath(path, fullPath));
    }

    private static bool CoversPath(string coveredPath, string sourcePath)
    {
        if (string.IsNullOrWhiteSpace(coveredPath))
        {
            return false;
        }

        if (coveredPath.Contains('*') || coveredPath.Contains('?'))
        {
            var directory = Path.GetDirectoryName(coveredPath);
            var pattern = Path.GetFileName(coveredPath);
            if (string.IsNullOrWhiteSpace(directory) || string.IsNullOrWhiteSpace(pattern))
            {
                return false;
            }

            var recursive = string.Equals(Path.GetFileName(directory), "**", StringComparison.Ordinal);
            if (recursive)
            {
                directory = Path.GetDirectoryName(directory);
                if (string.IsNullOrWhiteSpace(directory))
                {
                    return false;
                }
            }

            var sourceDirectory = Path.GetDirectoryName(sourcePath);
            return !string.IsNullOrWhiteSpace(sourceDirectory)
                && (recursive
                    ? IsUnderDirectory(sourcePath, Path.GetFullPath(directory))
                    : IsSameDirectory(sourceDirectory, Path.GetFullPath(directory)))
                && FileSystemName.MatchesSimpleExpression(pattern, Path.GetFileName(sourcePath), ignoreCase: true);
        }

        var fullCoveredPath = Path.GetFullPath(coveredPath);
        if (File.Exists(fullCoveredPath))
        {
            return string.Equals(fullCoveredPath, sourcePath, StringComparison.OrdinalIgnoreCase);
        }

        return IsUnderDirectory(sourcePath, fullCoveredPath);
    }

    private static bool IsUnderDirectory(string sourcePath, string directory)
    {
        var normalisedDirectory = directory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return string.Equals(sourcePath, normalisedDirectory, StringComparison.OrdinalIgnoreCase)
            || sourcePath.StartsWith(normalisedDirectory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            || sourcePath.StartsWith(normalisedDirectory + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsSameDirectory(string left, string right)
    {
        var normalisedLeft = left.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var normalisedRight = right.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return string.Equals(normalisedLeft, normalisedRight, StringComparison.OrdinalIgnoreCase);
    }
}

public sealed record VssSnapshotResult(
    bool Success,
    Guid? SnapshotId,
    string? SnapshotDeviceObject,
    IReadOnlyList<VssWriterEvidence> Writers,
    string Message)
{
    public Func<ValueTask>? Cleanup { get; init; }

    public static VssSnapshotResult Created(
        Guid SnapshotId,
        string SnapshotDeviceObject,
        IReadOnlyList<VssWriterEvidence> Writers,
        string Message,
        Func<ValueTask>? Cleanup = null)
    {
        return new VssSnapshotResult(true, SnapshotId, SnapshotDeviceObject, Writers, Message)
        {
            Cleanup = Cleanup
        };
    }

    public static VssSnapshotResult Failed(string Message, Func<ValueTask>? Cleanup = null)
    {
        return new VssSnapshotResult(false, null, null, [], Message)
        {
            Cleanup = Cleanup
        };
    }
}
