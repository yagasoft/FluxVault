namespace FluxVault.Abstractions.Storage;

public sealed record RepositoryDeletionResult(
    FileVersionManifest Manifest,
    IReadOnlyList<string> MirrorWarnings);
