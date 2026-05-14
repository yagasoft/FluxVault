using FluxVault.Abstractions.Configuration;
using FluxVault.Abstractions.Policies;
using FluxVault.Core.Configuration;

namespace FluxVault.Core.Tests;

public sealed class ProtectionSelectionRegexMatcherTests
{
    [Fact]
    public void Recursive_folder_regex_applies_to_descendant_files()
    {
        var rule = Rule("root", @"D:\Work", ProtectionSelectionMode.RecursiveFolder) with
        {
            IncludeRegexRules =
            [
                new ProtectionScopedRegexRule("docx", @"\.docx$", ProtectionExclusionTarget.File)
            ]
        };

        Assert.True(ProtectionSelectionRegexMatcher.IsFileIncluded(FullPath(@"D:\Work\Child\brief.docx"), [rule]));
        Assert.False(ProtectionSelectionRegexMatcher.IsFileIncluded(FullPath(@"D:\Work\Child\brief.tmp"), [rule]));
    }

    [Fact]
    public void Immediate_folder_regex_applies_only_to_direct_files()
    {
        var rule = Rule("root", @"D:\Work", ProtectionSelectionMode.ImmediateFiles) with
        {
            IncludeRegexRules =
            [
                new ProtectionScopedRegexRule("docx", @"\.docx$", ProtectionExclusionTarget.File)
            ]
        };

        Assert.True(ProtectionSelectionRegexMatcher.IsFileIncluded(FullPath(@"D:\Work\brief.docx"), [rule]));
        Assert.False(ProtectionSelectionRegexMatcher.IsFileIncluded(FullPath(@"D:\Work\Child\brief.docx"), [rule]));
    }

    [Fact]
    public void Child_regex_rules_are_additive_with_inherited_parent_rules()
    {
        var parent = Rule("parent", @"D:\Work", ProtectionSelectionMode.RecursiveFolder) with
        {
            IncludeRegexRules =
            [
                new ProtectionScopedRegexRule("docx", @"\.docx$", ProtectionExclusionTarget.File)
            ]
        };
        var child = Rule("child", @"D:\Work\Child", ProtectionSelectionMode.RecursiveFolder) with
        {
            ExcludeRegexRules =
            [
                new ProtectionScopedRegexRule("draft", @"\\draft-", ProtectionExclusionTarget.File)
            ]
        };

        Assert.True(ProtectionSelectionRegexMatcher.IsFileIncluded(FullPath(@"D:\Work\Child\final.docx"), [parent, child]));
        Assert.False(ProtectionSelectionRegexMatcher.IsFileIncluded(FullPath(@"D:\Work\Child\draft-1.docx"), [parent, child]));
        Assert.False(ProtectionSelectionRegexMatcher.IsFileIncluded(FullPath(@"D:\Work\Child\final.tmp"), [parent, child]));
    }

    [Fact]
    public void Scoped_exact_exclusion_blocks_inherited_recursive_file()
    {
        var parent = Rule("parent", @"D:\Work", ProtectionSelectionMode.RecursiveFolder) with
        {
            ExcludeRegexRules =
            [
                ProtectionScopedRegexRule.ExactPath("remove", FullPath(@"D:\Work\Child\brief.docx"), ProtectionExclusionTarget.File)
            ]
        };

        Assert.False(ProtectionSelectionRegexMatcher.IsFileIncluded(FullPath(@"D:\Work\Child\brief.docx"), [parent]));
        Assert.True(ProtectionSelectionRegexMatcher.IsFileIncluded(FullPath(@"D:\Work\Child\other.docx"), [parent]));
    }

    [Fact]
    public void Regex_scope_rules_apply_to_protected_descendants_without_selecting_parent_folder()
    {
        var scope = Rule("scope", @"D:\Work", ProtectionSelectionMode.RegexScope) with
        {
            IncludeRegexRules =
            [
                new ProtectionScopedRegexRule("docx", @"\.docx$", ProtectionExclusionTarget.File)
            ]
        };
        var selectedChild = Rule("child", @"D:\Work\Child", ProtectionSelectionMode.RecursiveFolder);

        Assert.True(ProtectionSelectionRegexMatcher.IsFileIncluded(FullPath(@"D:\Work\Child\brief.docx"), [scope, selectedChild]));
        Assert.False(ProtectionSelectionRegexMatcher.IsFileIncluded(FullPath(@"D:\Work\Child\brief.tmp"), [scope, selectedChild]));
    }

    private static ProtectionSelectionRule Rule(string id, string path, ProtectionSelectionMode mode)
    {
        return new ProtectionSelectionRule(
            id,
            FullPath(path),
            mode,
            CompressionPreference.Zstd,
            ResourceProfile.Balanced,
            IsEnabled: true);
    }

    private static string FullPath(string path)
    {
        return Path.GetFullPath(path);
    }
}
