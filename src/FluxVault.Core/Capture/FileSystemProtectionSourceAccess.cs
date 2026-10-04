using FluxVault.Abstractions.Capture;
using FluxVault.Abstractions.Storage;

namespace FluxVault.Core.Capture;

/// <summary>Direct local adapter for legacy operations; never an authenticated service fallback.</summary>
public sealed class FileSystemProtectionSourceAccess : IProtectionSourceAccess
{
    private readonly Action<string>? beforeChildProbe;
    public FileSystemProtectionSourceAccess() { }
    internal FileSystemProtectionSourceAccess(Action<string> beforeChildProbe) => this.beforeChildProbe = beforeChildProbe;

    public ProtectionSourceInspection Inspect(string protectionRoot, string path, RepositoryEntryKind kind,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(protectionRoot));
            var source = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
            var rootSelf = string.Equals(root, source, StringComparison.OrdinalIgnoreCase);
            var prefix = Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar;
            if (!rootSelf && !source.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return new(ProtectionSourceAvailability.Unavailable, FailureReason: "The source is outside the protection selection.");
            if (!rootSelf)
            {
                if (!IsReadableRoot(root)) return new(ProtectionSourceAvailability.Unavailable, FailureReason: "The protection root is unavailable.");
                beforeChildProbe?.Invoke(source);
            }
            var attributes = File.GetAttributes(path);
            var actual = (attributes & FileAttributes.Directory) != 0 ? RepositoryEntryKind.Folder : RepositoryEntryKind.File;
            if (actual != kind) return new(ProtectionSourceAvailability.Unavailable, FailureReason: "The source entry type changed.");
            if (kind == RepositoryEntryKind.Folder) return new(ProtectionSourceAvailability.Present, actual);
            var info = new FileInfo(path);
            return new(ProtectionSourceAvailability.Present, actual, info.Length, new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero));
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            var rootSelf = string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(protectionRoot)),
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)), StringComparison.OrdinalIgnoreCase);
            return rootSelf || IsReadableRoot(protectionRoot) ? new(ProtectionSourceAvailability.Missing) :
                new(ProtectionSourceAvailability.Unavailable, FailureReason: "The protection root became unavailable during source inspection.");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        { return new(ProtectionSourceAvailability.Unavailable, FailureReason: exception.Message); }
    }

    private static bool IsReadableRoot(string root)
    {
        try { return (File.GetAttributes(root) & FileAttributes.Directory) != 0; }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { return false; }
    }

    public IEnumerable<ProtectionSourceCandidate> EnumerateDirectory(string protectionRoot, string directory,
        CancellationToken cancellationToken = default)
    {
        foreach (var path in Directory.EnumerateFiles(directory))
        { cancellationToken.ThrowIfCancellationRequested(); yield return new(path, RepositoryEntryKind.File); }
        foreach (var path in Directory.EnumerateDirectories(directory))
        { cancellationToken.ThrowIfCancellationRequested(); yield return new(path, RepositoryEntryKind.Folder); }
    }
}
