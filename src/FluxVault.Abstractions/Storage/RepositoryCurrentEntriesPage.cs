using FluxVault.Abstractions.Security;

namespace FluxVault.Abstractions.Storage;

// Numeric canonical path identities order transport pages, never grant access.
public sealed record RepositoryCurrentEntriesCursor(VaultId RepositoryId, int PageSize, long Generation, long PathId);
public sealed record RepositoryCurrentEntriesQuery(VaultId RepositoryId, int PageSize = 100, RepositoryCurrentEntriesCursor? Cursor = null);
public sealed record RepositoryCurrentEntry(long PathId, RepositoryVersionSummary Version);
public sealed record RepositoryCurrentEntriesPage(RepositoryCurrentEntriesQuery Query, long Generation,
    IReadOnlyList<RepositoryCurrentEntry> Entries, RepositoryCurrentEntriesCursor? NextCursor);
