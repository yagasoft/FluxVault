using FluxVault.Abstractions.Policies;

namespace FluxVault.Core.Policies;

public static class CodecPolicySelector
{
    public static CompressionPreference Select(
        CodecPolicy policy,
        string sourcePath,
        long contentLength,
        bool isHotFile)
    {
        ArgumentNullException.ThrowIfNull(policy);
        if (contentLength < policy.MinimumBytes)
        {
            return CompressionPreference.Off;
        }

        var extension = Path.GetExtension(sourcePath);
        if (policy.SkipExtensions.Any(value => string.Equals(value, extension, StringComparison.OrdinalIgnoreCase)))
        {
            return CompressionPreference.Off;
        }

        if (isHotFile)
        {
            return policy.HotFileOverride;
        }

        return policy.Profile switch
        {
            CodecProfile.Speed => CompressionPreference.Lz4,
            CodecProfile.Ratio => CompressionPreference.Brotli,
            CodecProfile.ColdArchive => CompressionPreference.Lzma,
            _ => policy.Codec
        };
    }
}
