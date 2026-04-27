using FluxVault.Abstractions.Policies;

namespace FluxVault.Core.Policies;

public static class ProtectionPolicyDefaults
{
    public static ProtectionPolicy CreateDefault()
    {
        return new ProtectionPolicy(
            ResourceProfile: ResourceProfile.Balanced,
            Compression: CompressionPreference.Zstd,
            MinimumCompressionBytes: 256 * 1024,
            MaximumFileBytes: 100L * 1024 * 1024 * 1024);
    }
}
