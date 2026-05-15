namespace FluxVault.Abstractions.Storage;

public enum RepositoryPurgeScopeKind
{
    File = 0,
    ImmediateFiles = 1,
    RecursiveFolder = 2
}

public sealed record RepositoryPurgeScope(
    string SourcePath,
    RepositoryPurgeScopeKind Kind);

public sealed record RepositoryPurgeRequest(
    IReadOnlyList<RepositoryPurgeScope> Scopes,
    IReadOnlyList<RepositoryPurgeScope>? PreserveScopes = null);

public sealed record RepositoryPurgeResult(
    int PurgedVersionCount,
    int DeletedChunkCount,
    long ReclaimedBytes,
    IReadOnlyList<string> MirrorWarnings,
    bool Success = true,
    string? ErrorMessage = null);
