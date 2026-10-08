using FluxVault.Abstractions.Security;

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
    IReadOnlyList<RepositoryPurgeScope>? PreserveScopes = null,
    RepositoryHistoryDeletionReview? HistoryDeletionReview = null);

public sealed record RepositoryHistoryDeletionReview(VaultId VaultId, long ConfigurationRevision, string? Fingerprint = null);

public sealed record RepositoryHistoryDeletionPreview(
    RepositoryPurgeScope Scope,
    string Fingerprint,
    bool CanDelete,
    bool IsComplete,
    int CandidateVersionCount,
    long CandidateLogicalBytes,
    IReadOnlyList<RepositoryVersionSummary> Candidates,
    IReadOnlyList<RepositoryVersionSummary> BlockingDependencies,
    IReadOnlyList<string> Warnings);

public sealed record RepositoryPurgeResult(
    int PurgedVersionCount,
    int DeletedChunkCount,
    long ReclaimedBytes,
    IReadOnlyList<string> MirrorWarnings,
    bool Success = true,
    string? ErrorMessage = null);
