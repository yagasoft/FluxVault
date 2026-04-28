using FluxVault.Abstractions.Policies;

namespace FluxVault.Abstractions.Configuration;

public sealed record ProtectionSelectionRule(
    string Id,
    string Path,
    ProtectionSelectionMode Mode,
    CompressionPreference Compression,
    ResourceProfile ResourceProfile,
    bool IsEnabled,
    IReadOnlyList<ProtectionScopedRegexRule>? IncludeRegexRules = null,
    IReadOnlyList<ProtectionScopedRegexRule>? ExcludeRegexRules = null);
