using FluxVault.Abstractions.Configuration;
using FluxVault.Core.Configuration;

namespace FluxVault.Core.Tests;

public sealed class ProtectionExclusionRuleTests
{
    [Fact]
    public void Validator_accepts_valid_enabled_rule()
    {
        var result = ProtectionExclusionRuleValidator.Validate(
            [
                new ProtectionExclusionRule(
                    Id: "cache",
                    Pattern: @"\\cache(\\|$)",
                    Target: ProtectionExclusionTarget.Folder,
                    IsEnabled: true)
            ]);

        Assert.True(result.IsValid);
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void Validator_rejects_invalid_regex()
    {
        var result = ProtectionExclusionRuleValidator.Validate(
            [
                new ProtectionExclusionRule(
                    Id: "broken",
                    Pattern: "[",
                    Target: ProtectionExclusionTarget.Both,
                    IsEnabled: true)
            ]);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, error => error.Contains("broken", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Matcher_excludes_file_and_parent_folder_paths_case_insensitively()
    {
        var rules = new[]
        {
            new ProtectionExclusionRule(
                Id: "tmp",
                Pattern: @"\.TMP$",
                Target: ProtectionExclusionTarget.File,
                IsEnabled: true),
            new ProtectionExclusionRule(
                Id: "node",
                Pattern: @"\\node_modules(\\|$)",
                Target: ProtectionExclusionTarget.Folder,
                IsEnabled: true)
        };

        Assert.True(ProtectionExclusionMatcher.IsFileExcluded(@"D:\Work\draft.tmp", rules));
        Assert.True(ProtectionExclusionMatcher.IsFileExcluded(@"D:\Work\node_modules\package.json", rules));
        Assert.True(ProtectionExclusionMatcher.IsFolderExcluded(@"D:\Work\NODE_MODULES", rules));
        Assert.False(ProtectionExclusionMatcher.IsFileExcluded(@"D:\Work\src\package.json", rules));
    }
}
