namespace FluxVault.Abstractions.Policies;

public sealed record WatchedFolderPolicy(
    string Id,
    string Path,
    bool IsRecursive,
    IReadOnlyList<string> IncludeGlobs,
    IReadOnlyList<string> ExcludeGlobs,
    ResourceProfile ResourceProfile,
    CompressionPreference Compression,
    int MinimumCompressionBytes,
    long MaximumFileBytes,
    IReadOnlyList<FilePolicyOverride> Overrides);

public sealed record FilePolicyOverride(
    string Glob,
    ResourceProfile? ResourceProfile,
    CompressionPreference? Compression,
    int? MinimumCompressionBytes);

public sealed record ResolvedProtectionPolicy(
    string WatchedFolderId,
    ResourceProfile ResourceProfile,
    CompressionPreference Compression,
    int MinimumCompressionBytes,
    long MaximumFileBytes);
