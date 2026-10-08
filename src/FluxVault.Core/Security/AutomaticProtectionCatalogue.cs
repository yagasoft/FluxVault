using FluxVault.Abstractions.Configuration;

namespace FluxVault.Core.Security;

public sealed record AutomaticCatalogueState(VaultCatalogueEntry Vault, AutomaticProtectionPolicy Policy,
    bool RequiresCreatorApproval, string ScopeFingerprint, int PendingCount, int UncertainCount,
    string? LastSuccessfulVersionId, DateTimeOffset? LastSuccessfulUtc);

public sealed record AutomaticScopeWork(string ScopeId, string RootPath, string ScopeFingerprint, long DirtyGeneration);

public sealed record AutomaticPathWork(string PathKey, string ScopeId, string RootPath, string SourcePath,
    string ScopeFingerprint, long DirtyGeneration, long? ClaimedGeneration, string? ReservedVersionId,
    Guid? OwnerGeneration, int Attempts, string? SourceObservation, string? LastSourceObservation,
    string? LastVersionId, string? Reason);

public sealed class AutomaticProtectionUnavailableException(string message, Exception? inner = null) : IOException(message, inner);
