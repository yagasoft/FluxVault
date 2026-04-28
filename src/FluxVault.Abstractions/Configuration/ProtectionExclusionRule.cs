namespace FluxVault.Abstractions.Configuration;

public sealed record ProtectionExclusionRule(
    string Id,
    string Pattern,
    ProtectionExclusionTarget Target,
    bool IsEnabled,
    string? Label = null,
    string? Description = null);
