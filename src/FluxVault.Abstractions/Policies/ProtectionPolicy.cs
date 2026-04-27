namespace FluxVault.Abstractions.Policies;

public sealed record ProtectionPolicy(
    ResourceProfile ResourceProfile,
    CompressionPreference Compression,
    int MinimumCompressionBytes,
    long MaximumFileBytes);
