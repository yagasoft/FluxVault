using System.Text.Json;
using FluxVault.Abstractions.Configuration;
using FluxVault.Abstractions.Ipc;
using FluxVault.Abstractions.Security;
using FluxVault.Abstractions.Storage;

namespace FluxVault.Core.Storage.Metadata;

public static class RepositoryHistoryPaging
{
    public const int MaximumResponseBytes = 2 * 1024 * 1024;
    private static readonly JsonSerializerOptions WireOptions = new(JsonSerializerDefaults.Web);

    public static string CanonicalPath(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)).ToUpperInvariant();
    private static void ValidateCount(int count)
    {
        if (count is < 1 or > RepositoryBrowsePolicy.MaximumItemsPerPage) throw new ArgumentOutOfRangeException(nameof(count));
    }
    public static void ValidateVersionId(string id)
    {
        if (!Guid.TryParseExact(id, "N", out _)) throw new ArgumentException("A 32-character hexadecimal version identity is required.");
    }
    public static RepositoryHistoryQuery Validate(RepositoryHistoryQuery query, VaultId? binding = null)
    {
        ArgumentNullException.ThrowIfNull(query);
        ValidateCount(query.PageSize);
        if (!query.RepositoryId.IsValid || binding is not null && query.RepositoryId != binding) throw new ArgumentException("History query does not match the repository binding.");
        if (query.EntryKind is { } kind && !Enum.IsDefined(kind)) throw new ArgumentException("Unknown entry kind.");
        if (query.SourcePath is not null && !Path.IsPathFullyQualified(query.SourcePath)) throw new ArgumentException("A fully qualified source path is required.");
        query = query with { SourcePath = query.SourcePath is null ? null : CanonicalPath(query.SourcePath) };
        if (query.Cursor is { } cursor)
        {
            ValidateVersionId(cursor.VersionId);
            if (cursor.RepositoryId != query.RepositoryId || cursor.SourcePath != query.SourcePath ||
                cursor.IncludeDescendants != query.IncludeDescendants || cursor.EntryKind != query.EntryKind ||
                cursor.PageSize != query.PageSize || cursor.Generation < 0 || !Enum.IsDefined(cursor.Direction) ||
                cursor.CapturedAtTicks < DateTime.MinValue.Ticks || cursor.CapturedAtTicks > DateTime.MaxValue.Ticks)
                throw new ArgumentException("The history cursor does not match this query.");
        }
        return query;
    }
    public static RepositorySnapshotQuery Validate(RepositorySnapshotQuery query, VaultId? binding = null)
    {
        ArgumentNullException.ThrowIfNull(query);
        ValidateCount(query.PageSize); ValidateVersionId(query.VersionId);
        if (!query.RepositoryId.IsValid || binding is not null && query.RepositoryId != binding || query.Offset < 0)
            throw new ArgumentException("Snapshot query does not match the repository or supported offset.");
        return query;
    }
    public static RepositoryVersionSummary Header(FileVersionManifest manifest) =>
        RepositoryMetadataStoreHelpers.ToSummary(manifest) with { FolderEntries = null, ParentVersionIds = null };

    public static RepositoryHistoryPage Page(RepositoryHistoryQuery query, long generation,
        IReadOnlyList<RepositoryVersionSummary> rows, bool hasOlder, bool hasNewer)
    {
        RepositoryHistoryCursor Cursor(RepositoryVersionSummary row, HistoryPageDirection direction) => new(
            query.RepositoryId, query.SourcePath, query.IncludeDescendants, query.EntryKind, generation,
            row.CapturedAtUtc.UtcTicks, row.VersionId, direction, query.PageSize);
        return new(query, generation, rows, hasOlder && rows.Count > 0 ? Cursor(rows[^1], HistoryPageDirection.Older) : null,
            hasNewer && rows.Count > 0 ? Cursor(rows[0], HistoryPageDirection.Newer) : null);
    }
    public static RepositorySnapshotPage Snapshot(RepositorySnapshotQuery query, FileVersionManifest manifest)
    {
        Validate(query, manifest.VaultId);
        if (manifest.VersionId != query.VersionId) throw new InvalidDataException("Snapshot identity disagrees with the requested version.");
        var entries = (manifest.FolderEntries ?? []).OrderByDescending(e => e.EntryKind)
            .ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase).ThenBy(e => e.Name, StringComparer.Ordinal).ToArray();
        if (query.Offset > entries.Length) throw new ArgumentException("Snapshot offset exceeds its immutable contents.");
        var page = entries.Skip(query.Offset).Take(query.PageSize).ToArray();
        return new(query, Header(manifest), page, query.Offset + page.Length < entries.Length ? query.Offset + page.Length : null);
    }
    public static FluxVaultIpcResponse BoundResponse(FluxVaultIpcResponse response, int maximumBytes = MaximumResponseBytes)
    {
        // Measure exactly the wire serializer, including the already populated authenticated envelope.
        while ((response.HistoryPage is not null || response.SnapshotPage is not null || response.CurrentEntriesPage is not null) &&
            !Fits(response, maximumBytes))
        {
            if (response.HistoryPage is { } history && history.Versions.Count > 1)
            {
                var newer = history.Query.Cursor?.Direction == HistoryPageDirection.Newer;
                var count = Math.Max(1, history.Versions.Count / 2);
                var rows = newer ? history.Versions.TakeLast(count).ToArray() : history.Versions.Take(count).ToArray();
                response = response with { HistoryPage = Page(history.Query, history.Generation, rows,
                    history.OlderCursor is not null || !newer, history.NewerCursor is not null || newer) };
            }
            else if (response.SnapshotPage is { } snapshot && snapshot.Entries.Count > 1)
            {
                var rows = snapshot.Entries.Take(Math.Max(1, snapshot.Entries.Count / 2)).ToArray();
                response = response with { SnapshotPage = snapshot with { Entries = rows, NextOffset = snapshot.Query.Offset + rows.Length } };
            }
            else if (response.CurrentEntriesPage is { } current && current.Entries.Count > 1)
            {
                var rows = current.Entries.Take(Math.Max(1, current.Entries.Count / 2)).ToArray();
                response = response with { CurrentEntriesPage = RepositoryCurrentEntriesPaging.Page(current.Query, current.Generation, rows, true) };
            }
            else return FluxVaultIpcResponse.Failure("This repository item exceeds the supported page size. No partial item was returned.") with
            { VaultId = response.VaultId, VaultRevision = response.VaultRevision, ErrorCode = FluxVaultIpcErrorCode.Unavailable };
        }
        return response;
    }
    private static bool Fits(FluxVaultIpcResponse response, int maximumBytes)
    {
        using var counter = new CountingStream(maximumBytes);
        try { JsonSerializer.Serialize(counter, response, WireOptions); return true; }
        catch (PageSizeExceededException) { return false; }
    }
    private sealed class PageSizeExceededException : IOException;
    private sealed class CountingStream(int maximumBytes) : Stream
    {
        private long count;
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => count;
        public override long Position { get => count; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override void Write(byte[] buffer, int offset, int length) => Write(buffer.AsSpan(offset, length));
        public override void Write(ReadOnlySpan<byte> buffer)
        {
            count += buffer.Length;
            if (count > maximumBytes) throw new PageSizeExceededException();
        }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
    public static int Compare(RepositoryVersionSummary row, RepositoryHistoryCursor cursor) =>
        row.CapturedAtUtc.UtcTicks != cursor.CapturedAtTicks ? row.CapturedAtUtc.UtcTicks.CompareTo(cursor.CapturedAtTicks) :
            StringComparer.Ordinal.Compare(row.VersionId, cursor.VersionId);
    public static bool Matches(RepositoryVersionSummary row, RepositoryHistoryQuery query)
    {
        var path = CanonicalPath(row.SourcePath);
        return (query.EntryKind is null || row.EntryKind == query.EntryKind) && (query.SourcePath is null ||
            path == query.SourcePath || query.IncludeDescendants && path.StartsWith(query.SourcePath.TrimEnd('\\', '/') + Path.DirectorySeparatorChar, StringComparison.Ordinal));
    }
}
