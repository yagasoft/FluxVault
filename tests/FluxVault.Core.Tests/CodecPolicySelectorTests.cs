using FluxVault.Abstractions.Policies;
using FluxVault.Core.Policies;

namespace FluxVault.Core.Tests;

public sealed class CodecPolicySelectorTests
{
    [Fact]
    public void Adaptive_policy_uses_lz4_for_hot_files()
    {
        var policy = CodecPolicy.CreateDefault();

        var codec = CodecPolicySelector.Select(policy, @"D:\Work\model.dat", 10 * 1024 * 1024, isHotFile: true);

        Assert.Equal(CompressionPreference.Lz4, codec);
    }

    [Fact]
    public void Adaptive_policy_skips_known_compressed_file_types()
    {
        var policy = CodecPolicy.CreateDefault();

        var codec = CodecPolicySelector.Select(policy, @"D:\Work\video.mp4", 10 * 1024 * 1024, isHotFile: false);

        Assert.Equal(CompressionPreference.Off, codec);
    }

    [Fact]
    public void Cold_archive_policy_uses_lzma_for_large_cold_files()
    {
        var policy = CodecPolicy.CreateDefault() with { Profile = CodecProfile.ColdArchive };

        var codec = CodecPolicySelector.Select(policy, @"D:\Work\export.csv", 10 * 1024 * 1024, isHotFile: false);

        Assert.Equal(CompressionPreference.Lzma, codec);
    }
}
