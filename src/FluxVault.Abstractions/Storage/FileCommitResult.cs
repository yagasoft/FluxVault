namespace FluxVault.Abstractions.Storage;

public sealed record FileCommitResult(
    FileVersionManifest Manifest,
    int NewChunkCount,
    IReadOnlyList<string> MirrorWarnings)
{
    public FileCommitResult(FileVersionManifest manifest, int newChunkCount)
        : this(manifest, newChunkCount, [])
    {
    }
}
