using FluxVault.Abstractions.Configuration;
using FluxVault.Abstractions.Security;
using FluxVault.Abstractions.Storage;

namespace FluxVault.Core.Storage.Metadata;

public static class RepositoryCurrentEntriesPaging
{
    public static void Validate(RepositoryCurrentEntriesQuery query, VaultId? binding = null)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (!query.RepositoryId.IsValid || binding is not null && query.RepositoryId != binding)
            throw new ArgumentException("Current-entry query does not match the repository binding.");
        if (query.PageSize is < 1 or > RepositoryBrowsePolicy.MaximumItemsPerPage) throw new ArgumentOutOfRangeException(nameof(query.PageSize));
        if (query.Cursor is { } cursor && (cursor.RepositoryId != query.RepositoryId || cursor.PageSize != query.PageSize ||
            cursor.Generation < 0 || cursor.PathId <= 0)) throw new ArgumentException("Current-entry cursor does not match the query.");
    }
    public static RepositoryCurrentEntriesPage Page(RepositoryCurrentEntriesQuery query, long generation,
        IReadOnlyList<RepositoryCurrentEntry> entries, bool more) => new(query, generation, entries,
            more && entries.Count > 0 ? new(query.RepositoryId, query.PageSize, generation, entries[^1].PathId) : null);
}
