using FluxVault.Abstractions.Configuration;
using FluxVault.Abstractions.Policies;
using FluxVault.Core.Configuration;

namespace FluxVault.Core.Tests;

public sealed class ProtectionSelectionCompilerTests
{
    [Fact]
    public void Compile_emits_recursive_folder_rule_as_recursive_watched_folder()
    {
        var root = FullPath(@"D:\Work\Docs");
        var watched = ProtectionSelectionCompiler.Compile(
            [
                new ProtectionSelectionRule(
                    "docs",
                    root,
                    ProtectionSelectionMode.RecursiveFolder,
                    CompressionPreference.Zstd,
                    ResourceProfile.Balanced,
                    IsEnabled: true)
            ]);

        var folder = Assert.Single(watched);
        Assert.Equal("docs", folder.Id);
        Assert.Equal(root, folder.Path);
        Assert.True(folder.Recursive);
        Assert.Equal(["*"], folder.IncludePatterns);
        Assert.Equal(["~$*"], folder.ExcludePatterns);
        Assert.Equal(CompressionPreference.Zstd, folder.Compression);
        Assert.Equal(ResourceProfile.Balanced, folder.ResourceProfile);
    }

    [Fact]
    public void Compile_emits_immediate_files_rule_as_non_recursive_watched_folder()
    {
        var root = FullPath(@"D:\Work\Docs");
        var watched = ProtectionSelectionCompiler.Compile(
            [
                new ProtectionSelectionRule(
                    "docs",
                    root,
                    ProtectionSelectionMode.ImmediateFiles,
                    CompressionPreference.Brotli,
                    ResourceProfile.Quiet,
                    IsEnabled: true)
            ]);

        var folder = Assert.Single(watched);
        Assert.Equal(root, folder.Path);
        Assert.False(folder.Recursive);
        Assert.Equal(["*"], folder.IncludePatterns);
        Assert.Equal(CompressionPreference.Brotli, folder.Compression);
        Assert.Equal(ResourceProfile.Quiet, folder.ResourceProfile);
    }

    [Fact]
    public void Compile_groups_selected_files_by_parent_folder()
    {
        var root = FullPath(@"D:\Work\Docs");
        var watched = ProtectionSelectionCompiler.Compile(
            [
                new ProtectionSelectionRule(
                    "a",
                    Path.Combine(root, "a.txt"),
                    ProtectionSelectionMode.File,
                    CompressionPreference.Zstd,
                    ResourceProfile.Balanced,
                    IsEnabled: true),
                new ProtectionSelectionRule(
                    "b",
                    Path.Combine(root, "b.txt"),
                    ProtectionSelectionMode.File,
                    CompressionPreference.Zstd,
                    ResourceProfile.Balanced,
                    IsEnabled: true)
            ]);

        var folder = Assert.Single(watched);
        Assert.Equal(root, folder.Path);
        Assert.False(folder.Recursive);
        Assert.Equal(["a.txt", "b.txt"], folder.IncludePatterns.Order(StringComparer.OrdinalIgnoreCase));
        Assert.StartsWith("files-", folder.Id);
    }

    [Fact]
    public void Compile_suppresses_child_rules_when_recursive_parent_covers_them()
    {
        var root = FullPath(@"D:\Work\Docs");
        var watched = ProtectionSelectionCompiler.Compile(
            [
                new ProtectionSelectionRule(
                    "root",
                    root,
                    ProtectionSelectionMode.RecursiveFolder,
                    CompressionPreference.Zstd,
                    ResourceProfile.Balanced,
                    IsEnabled: true),
                new ProtectionSelectionRule(
                    "child",
                    Path.Combine(root, "child", "manual.txt"),
                    ProtectionSelectionMode.File,
                    CompressionPreference.Lz4,
                    ResourceProfile.Fast,
                    IsEnabled: true)
            ]);

        var folder = Assert.Single(watched);
        Assert.Equal("root", folder.Id);
        Assert.True(folder.Recursive);
    }

    [Fact]
    public void Compile_ignores_regex_scope_rules_as_watched_folders()
    {
        var root = FullPath(@"D:\Work");
        var child = Path.Combine(root, "Docs");
        var watched = ProtectionSelectionCompiler.Compile(
            [
                new ProtectionSelectionRule(
                    "work-regex",
                    root,
                    ProtectionSelectionMode.RegexScope,
                    CompressionPreference.Zstd,
                    ResourceProfile.Balanced,
                    IsEnabled: true,
                    IncludeRegexRules:
                    [
                        new ProtectionScopedRegexRule("docx", @"\.docx$", ProtectionExclusionTarget.File)
                    ]),
                new ProtectionSelectionRule(
                    "docs",
                    child,
                    ProtectionSelectionMode.RecursiveFolder,
                    CompressionPreference.Zstd,
                    ResourceProfile.Balanced,
                    IsEnabled: true)
            ]);

        var folder = Assert.Single(watched);
        Assert.Equal("docs", folder.Id);
        Assert.Equal(child, folder.Path);
    }

    private static string FullPath(string path)
    {
        return Path.GetFullPath(path);
    }
}
