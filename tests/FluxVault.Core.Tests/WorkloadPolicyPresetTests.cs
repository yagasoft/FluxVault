using FluxVault.Abstractions.Configuration;
using FluxVault.Abstractions.Policies;
using FluxVault.Core.Policies;

namespace FluxVault.Core.Tests;

public sealed class WorkloadPolicyPresetTests
{
    [Fact]
    public void Built_in_catalog_contains_all_conservative_presets()
    {
        var presets = WorkloadPolicyPresetCatalog.Presets;

        Assert.Equal(Enum.GetValues<WorkloadPolicyPresetId>().Length, presets.Count);
        Assert.Contains(presets, preset => preset.Id == WorkloadPolicyPresetId.GeneralPurpose && preset.ResourceProfile == ResourceProfile.Balanced);
        Assert.Contains(presets, preset => preset.Id == WorkloadPolicyPresetId.OfficeDocuments && preset.NoCompressionExtensions.Contains(".docx"));
        Assert.Contains(presets, preset => preset.Id == WorkloadPolicyPresetId.CadBim && preset.NoCompressionExtensions.Contains(".rvt"));
        Assert.Contains(presets, preset => preset.Id == WorkloadPolicyPresetId.AdobeVideo && preset.ExcludedFolderNames.Contains("Media Cache Files"));
        Assert.Contains(presets, preset => preset.Id == WorkloadPolicyPresetId.DeveloperWorkspace && preset.ExcludedFolderNames.Contains("node_modules"));
        Assert.Contains(presets, preset => preset.Id == WorkloadPolicyPresetId.GenericLargeFiles && preset.NoCompressionExtensions.Contains(".vhdx"));
    }

    [Fact]
    public void Resolver_uses_nearest_selected_workload_preset_for_file_policy()
    {
        var configuration = ConfigurationWithSelections(
            [
                Rule("root", @"D:\Work", ProtectionSelectionMode.RecursiveFolder, WorkloadPolicyPresetId.OfficeDocuments),
                Rule("src", @"D:\Work\src", ProtectionSelectionMode.RecursiveFolder, WorkloadPolicyPresetId.DeveloperWorkspace)
            ]);
        var folder = Folder(@"D:\Work");

        var resolved = WorkloadPolicyResolver.Resolve(
            configuration,
            folder,
            @"D:\Work\src\app.cs",
            contentLength: 512 * 1024,
            isHotFile: false);

        Assert.Equal(WorkloadPolicyPresetId.DeveloperWorkspace, resolved.PresetId);
        Assert.Equal(ResourceProfile.Fast, resolved.ResourceProfile);
        Assert.Equal(CompressionPreference.Zstd, resolved.Compression);
        Assert.Equal(64 * 1024, resolved.MinimumCompressionBytes);
        Assert.False(resolved.IsExcluded);
    }

    [Fact]
    public void Resolver_excludes_common_generated_developer_folders()
    {
        var configuration = ConfigurationWithSelections(
            [Rule("repo", @"D:\Work\repo", ProtectionSelectionMode.RecursiveFolder, WorkloadPolicyPresetId.DeveloperWorkspace)]);
        var folder = Folder(@"D:\Work\repo");

        var resolved = WorkloadPolicyResolver.Resolve(
            configuration,
            folder,
            @"D:\Work\repo\node_modules\package\index.js",
            contentLength: 128 * 1024,
            isHotFile: false);

        Assert.True(resolved.IsExcluded);
        Assert.Contains("node_modules", resolved.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Resolver_applies_global_skip_extensions_after_preset_choice()
    {
        var configuration = ConfigurationWithSelections(
            [Rule("docs", @"D:\Docs", ProtectionSelectionMode.RecursiveFolder, WorkloadPolicyPresetId.OfficeDocuments)]) with
        {
            CodecPolicy = CodecPolicy.CreateDefault() with { SkipExtensions = [".csv"] }
        };
        var folder = Folder(@"D:\Docs");

        var resolved = WorkloadPolicyResolver.Resolve(
            configuration,
            folder,
            @"D:\Docs\report.csv",
            contentLength: 2 * 1024 * 1024,
            isHotFile: false);

        Assert.Equal(CompressionPreference.Off, resolved.Compression);
        Assert.Contains(".csv", resolved.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Resolver_defaults_old_selection_rules_to_general_purpose()
    {
        var configuration = ConfigurationWithSelections(
            [Rule("docs", @"D:\Docs", ProtectionSelectionMode.RecursiveFolder, preset: null)]);
        var folder = Folder(@"D:\Docs");

        var resolved = WorkloadPolicyResolver.Resolve(
            configuration,
            folder,
            @"D:\Docs\brief.txt",
            contentLength: 512 * 1024,
            isHotFile: false);

        Assert.Equal(WorkloadPolicyPresetId.GeneralPurpose, resolved.PresetId);
        Assert.Equal(ResourceProfile.Balanced, resolved.ResourceProfile);
        Assert.Equal(CompressionPreference.Zstd, resolved.Compression);
    }

    private static FluxVaultConfiguration ConfigurationWithSelections(IReadOnlyList<ProtectionSelectionRule> rules)
    {
        return FluxVaultConfiguration.CreateDefault(@"D:\Vault") with
        {
            SelectionRules = rules,
            WorkloadPolicy = new WorkloadPolicyConfiguration(WorkloadPolicyPresetId.CadBim)
        };
    }

    private static ProtectionSelectionRule Rule(
        string id,
        string path,
        ProtectionSelectionMode mode,
        WorkloadPolicyPresetId? preset)
    {
        return new ProtectionSelectionRule(
            id,
            Path.GetFullPath(path),
            mode,
            CompressionPreference.Zstd,
            ResourceProfile.Balanced,
            IsEnabled: true,
            WorkloadPreset: preset);
    }

    private static WatchedFolderConfiguration Folder(string path)
    {
        return new WatchedFolderConfiguration(
            "folder",
            Path.GetFullPath(path),
            Recursive: true,
            IncludePatterns: ["*"],
            ExcludePatterns: ["~$*"],
            Compression: CompressionPreference.Zstd,
            ResourceProfile: ResourceProfile.Balanced,
            IsEnabled: true);
    }
}
