using FluxVault.Abstractions.Configuration;

namespace FluxVault.Core.Security;

public sealed record AutomaticCatalogueState(VaultCatalogueEntry Vault, AutomaticProtectionPolicy Policy,
    bool RequiresCreatorApproval, string ScopeFingerprint, int PendingCount, int UncertainCount,
    string? LastSuccessfulVersionId, DateTimeOffset? LastSuccessfulUtc, int MirrorPendingCount=0,string? MirrorReason=null);

public sealed record AutomaticMirrorWork(string PathKey,string ScopeId,string SourcePath,long AfterOutboxId,long ThroughOutboxId);
public sealed record AutomaticMirrorVersion(long OutboxId,string VersionId);

public sealed record AutomaticScopeWork(string ScopeId, string RootPath, string ScopeFingerprint, long DirtyGeneration);

public sealed record AutomaticPathObservation(string Value, DateTimeOffset ObservedAtUtc,
    bool IsDeletion = false, bool RetainedResultVerified = false);

public sealed record AutomaticPathWork(string PathKey, string ScopeId, string RootPath, string SourcePath,
    string ScopeFingerprint, long DirtyGeneration, long? ClaimedGeneration, string? ReservedVersionId,
    Guid? OwnerGeneration, int Attempts, string? SourceObservation, string? LastSourceObservation,
    string? LastVersionId, string? Reason, DateTimeOffset? FirstEventUtc = null,
    DateTimeOffset? LatestEventUtc = null, DateTimeOffset? LastAttemptUtc = null,
    string? QueuedObservation = null, bool IsDeletion = false, string? DeletedFromVersionId = null);

public sealed class AutomaticProtectionUnavailableException(string message, Exception? inner = null) : IOException(message, inner);
