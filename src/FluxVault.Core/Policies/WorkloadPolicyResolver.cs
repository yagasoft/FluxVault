using FluxVault.Abstractions.Configuration;
using FluxVault.Abstractions.Policies;

namespace FluxVault.Core.Policies;

public sealed record ResolvedWorkloadPolicy(
    WorkloadPolicyPresetId PresetId,
    ResourceProfile ResourceProfile,
    CompressionPreference Compression,
    int MinimumCompressionBytes,
    bool IsExcluded,
    string Reason);

public static class WorkloadPolicyResolver
{
    public static ResolvedWorkloadPolicy Resolve(
        FluxVaultConfiguration configuration,
        WatchedFolderConfiguration folder,
        string filePath,
        long contentLength,
        bool isHotFile)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(folder);
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);

        var fullPath = Path.GetFullPath(filePath);
        var rule = FindNearestRule(configuration.SelectionRules ?? [], fullPath);
        if (rule is null)
        {
            return ResolveLegacyFolder(configuration, folder, fullPath, contentLength, isHotFile);
        }

        var presetId = rule.WorkloadPreset ?? WorkloadPolicyPresetId.GeneralPurpose;
        return ResolvePreset(configuration, WorkloadPolicyPresetCatalog.Get(presetId), rule.Path, fullPath, contentLength, isHotFile);
    }

    private static ResolvedWorkloadPolicy ResolveLegacyFolder(
        FluxVaultConfiguration configuration,
        WatchedFolderConfiguration folder,
        string fullPath,
        long contentLength,
        bool isHotFile)
    {
        var compression = CodecPolicySelector.Select(configuration.CodecPolicy, fullPath, contentLength, isHotFile);
        return new ResolvedWorkloadPolicy(
            WorkloadPolicyPresetId.GeneralPurpose,
            folder.ResourceProfile,
            compression,
            Math.Max(0, (int)Math.Min(int.MaxValue, configuration.CodecPolicy.MinimumBytes)),
            IsExcluded: false,
            Reason: "Legacy watched-folder policy.");
    }

    private static ResolvedWorkloadPolicy ResolvePreset(
        FluxVaultConfiguration configuration,
        WorkloadPolicyPreset preset,
        string policyRootPath,
        string fullPath,
        long contentLength,
        bool isHotFile)
    {
        var extension = Path.GetExtension(fullPath);
        var excludedFolder = MatchingFolderSegment(policyRootPath, fullPath, preset.ExcludedFolderNames);
        if (excludedFolder is not null)
        {
            return Excluded(preset, $"Excluded by {preset.DisplayName} folder rule: {excludedFolder}.");
        }

        if (Contains(preset.ExcludedExtensions, extension))
        {
            return Excluded(preset, $"Excluded by {preset.DisplayName} extension rule: {extension}.");
        }

        var compression = preset.Compression;
        var minimum = preset.MinimumCompressionBytes;
        var reason = $"{preset.DisplayName} workload preset.";
        var extensionOverride = preset.ExtensionOverrides.FirstOrDefault(rule => SameExtension(rule.Extension, extension));
        if (extensionOverride is not null)
        {
            compression = extensionOverride.Compression ?? compression;
            minimum = extensionOverride.MinimumCompressionBytes ?? minimum;
            reason = $"{preset.DisplayName} extension override for {extension}.";
        }

        if (Contains(preset.NoCompressionExtensions, extension))
        {
            compression = CompressionPreference.Off;
            reason = $"{preset.DisplayName} stores {extension} without extra compression.";
        }

        if (configuration.CodecPolicy.SkipExtensions.Any(value => SameExtension(value, extension)))
        {
            compression = CompressionPreference.Off;
            reason = $"Global skip extension {extension} stores the file without extra compression.";
        }

        if (contentLength < minimum)
        {
            compression = CompressionPreference.Off;
            reason = $"File is below the {minimum / 1024} KB workload compression threshold.";
        }
        else if (isHotFile && compression != CompressionPreference.Off)
        {
            compression = configuration.CodecPolicy.HotFileOverride;
            reason = $"{preset.DisplayName} hot-file override.";
        }

        return new ResolvedWorkloadPolicy(
            preset.Id,
            preset.ResourceProfile,
            compression,
            minimum,
            IsExcluded: false,
            reason);
    }

    private static ResolvedWorkloadPolicy Excluded(WorkloadPolicyPreset preset, string reason)
    {
        return new ResolvedWorkloadPolicy(
            preset.Id,
            preset.ResourceProfile,
            preset.Compression,
            preset.MinimumCompressionBytes,
            IsExcluded: true,
            reason);
    }

    private static ProtectionSelectionRule? FindNearestRule(IReadOnlyList<ProtectionSelectionRule> rules, string filePath)
    {
        return rules
            .Where(rule => rule.IsEnabled && CoversFile(rule, filePath))
            .OrderByDescending(rule => TrimPath(rule.Path).Length)
            .FirstOrDefault();
    }

    private static bool CoversFile(ProtectionSelectionRule rule, string filePath)
    {
        var rulePath = Path.GetFullPath(rule.Path);
        return rule.Mode switch
        {
            ProtectionSelectionMode.File => IsSamePath(filePath, rulePath),
            ProtectionSelectionMode.ImmediateFiles => IsDirectChildFile(filePath, rulePath),
            ProtectionSelectionMode.RecursiveFolder => IsSamePath(filePath, rulePath) || IsUnderPath(filePath, rulePath),
            _ => false
        };
    }

    private static string? MatchingFolderSegment(string policyRootPath, string fullPath, IReadOnlyList<string> folderNames)
    {
        if (folderNames.Count == 0)
        {
            return null;
        }

        var relativePath = Path.GetRelativePath(Path.GetFullPath(policyRootPath), fullPath);
        var pathToInspect = relativePath.StartsWith("..", StringComparison.Ordinal)
            ? fullPath
            : relativePath;
        return pathToInspect
            .Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault(segment => folderNames.Any(name => string.Equals(segment, name, StringComparison.OrdinalIgnoreCase)));
    }

    private static bool Contains(IReadOnlyList<string> values, string extension)
    {
        return values.Any(value => SameExtension(value, extension));
    }

    private static bool SameExtension(string left, string right)
    {
        return string.Equals(NormaliseExtension(left), NormaliseExtension(right), StringComparison.OrdinalIgnoreCase);
    }

    private static string NormaliseExtension(string extension)
    {
        if (string.IsNullOrWhiteSpace(extension))
        {
            return string.Empty;
        }

        return extension.StartsWith('.') ? extension : "." + extension;
    }

    private static bool IsDirectChildFile(string filePath, string folderPath)
    {
        var parent = Path.GetDirectoryName(filePath);
        return parent is not null && IsSamePath(parent, folderPath);
    }

    private static bool IsSamePath(string left, string right)
    {
        return string.Equals(TrimPath(left), TrimPath(right), StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsUnderPath(string path, string root)
    {
        var trimmedRoot = TrimPath(root) + Path.DirectorySeparatorChar;
        return Path.GetFullPath(path).StartsWith(trimmedRoot, StringComparison.OrdinalIgnoreCase);
    }

    private static string TrimPath(string path)
    {
        return Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }
}
