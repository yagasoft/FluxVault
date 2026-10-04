using FluxVault.Abstractions.Security;

namespace FluxVault.Abstractions.Storage;

public enum HistoryPageDirection { Older, Newer }

// A cursor describes a query; it never grants access to a repository.
public sealed record RepositoryHistoryCursor(VaultId RepositoryId, string? SourcePath, bool IncludeDescendants,
    RepositoryEntryKind? EntryKind, long Generation, long CapturedAtTicks, string VersionId,
    HistoryPageDirection Direction, int PageSize = 100);

public sealed record RepositoryHistoryQuery(VaultId RepositoryId, string? SourcePath = null,
    bool IncludeDescendants = false, RepositoryEntryKind? EntryKind = null, int PageSize = 100,
    RepositoryHistoryCursor? Cursor = null);

public sealed record RepositoryHistoryPage(RepositoryHistoryQuery Query, long Generation,
    IReadOnlyList<RepositoryVersionSummary> Versions, RepositoryHistoryCursor? OlderCursor,
    RepositoryHistoryCursor? NewerCursor);

public sealed record RepositorySnapshotQuery(VaultId RepositoryId, string VersionId, int Offset = 0, int PageSize = 100);

public sealed record RepositorySnapshotPage(RepositorySnapshotQuery Query, RepositoryVersionSummary Version,
    IReadOnlyList<FolderVersionEntry> Entries, int? NextOffset);

public sealed class RepositoryHistoryChangedException() : IOException("History changed. Refresh the history before browsing further.");
