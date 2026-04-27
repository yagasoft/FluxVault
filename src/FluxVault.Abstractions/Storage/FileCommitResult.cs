namespace FluxVault.Abstractions.Storage;

public sealed record FileCommitResult(FileVersionManifest Manifest, int NewChunkCount);
