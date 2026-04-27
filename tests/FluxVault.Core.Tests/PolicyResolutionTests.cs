using FluxVault.Abstractions.Policies;
using FluxVault.Core.Policies;

namespace FluxVault.Core.Tests;

public sealed class PolicyResolutionTests
{
    [Fact]
    public void Resolve_uses_file_override_before_folder_and_global_defaults()
    {
        var global = ProtectionPolicyDefaults.CreateDefault();
        var folder = new WatchedFolderPolicy(
            Id: "design",
            Path: @"D:\Work\Design",
            IsRecursive: true,
            IncludeGlobs: ["**/*"],
            ExcludeGlobs: ["**/bin/**"],
            ResourceProfile: ResourceProfile.Quiet,
            Compression: CompressionPreference.Off,
            MinimumCompressionBytes: 128 * 1024,
            MaximumFileBytes: 500L * 1024 * 1024 * 1024,
            Overrides:
            [
                new FilePolicyOverride("**/*.dwg", ResourceProfile.Fast, CompressionPreference.Zstd, 64 * 1024)
            ]);

        var resolved = PolicyResolver.Resolve(global, folder, @"D:\Work\Design\Model\floor.dwg");

        Assert.Equal("design", resolved.WatchedFolderId);
        Assert.Equal(ResourceProfile.Fast, resolved.ResourceProfile);
        Assert.Equal(CompressionPreference.Zstd, resolved.Compression);
        Assert.Equal(64 * 1024, resolved.MinimumCompressionBytes);
    }

    [Fact]
    public void Resolve_keeps_folder_policy_when_no_override_matches()
    {
        var global = ProtectionPolicyDefaults.CreateDefault() with
        {
            ResourceProfile = ResourceProfile.Balanced,
            Compression = CompressionPreference.Zstd
        };
        var folder = new WatchedFolderPolicy(
            Id: "docs",
            Path: @"D:\Work\Docs",
            IsRecursive: true,
            IncludeGlobs: ["**/*"],
            ExcludeGlobs: [],
            ResourceProfile: ResourceProfile.Quiet,
            Compression: CompressionPreference.Off,
            MinimumCompressionBytes: 256 * 1024,
            MaximumFileBytes: 20L * 1024 * 1024 * 1024,
            Overrides: []);

        var resolved = PolicyResolver.Resolve(global, folder, @"D:\Work\Docs\brief.docx");

        Assert.Equal(ResourceProfile.Quiet, resolved.ResourceProfile);
        Assert.Equal(CompressionPreference.Off, resolved.Compression);
        Assert.Equal(256 * 1024, resolved.MinimumCompressionBytes);
    }
}
