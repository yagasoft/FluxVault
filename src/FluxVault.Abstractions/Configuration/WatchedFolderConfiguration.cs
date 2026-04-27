using FluxVault.Abstractions.Policies;

namespace FluxVault.Abstractions.Configuration;

public sealed record WatchedFolderConfiguration(
    string Id,
    string Path,
    bool Recursive,
    IReadOnlyList<string> IncludePatterns,
    IReadOnlyList<string> ExcludePatterns,
    CompressionPreference Compression,
    ResourceProfile ResourceProfile,
    bool IsEnabled);
