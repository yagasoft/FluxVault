namespace FluxVault.Abstractions.Storage;

/// <summary>Inspects output only through caller authority; unavailable paths must throw rather than appear missing.</summary>
public interface IRestoreSelectionOutputAccess
{
    Task<bool> ExistsAsync(string path, RepositoryEntryKind expectedKind, CancellationToken cancellationToken);
    Task<IRepositoryRestoreTarget> CreateAsync(string path, RepositoryEntryKind kind, bool overwriteConfirmed, CancellationToken cancellationToken);
}

public sealed record RepositoryRestoreFileSelection(string VersionId, string RelativePath);
