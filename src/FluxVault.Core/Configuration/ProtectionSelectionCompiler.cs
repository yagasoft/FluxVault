using System.Security.Cryptography;
using System.Text;
using FluxVault.Abstractions.Configuration;

namespace FluxVault.Core.Configuration;

public static class ProtectionSelectionCompiler
{
    private static readonly string[] DefaultIncludePatterns = ["*"];
    private static readonly string[] DefaultExcludePatterns = ["~$*"];

    public static IReadOnlyList<WatchedFolderConfiguration> Compile(IReadOnlyList<ProtectionSelectionRule> selectionRules)
    {
        ArgumentNullException.ThrowIfNull(selectionRules);
        var enabledRules = selectionRules
            .Where(rule => rule.IsEnabled && !string.IsNullOrWhiteSpace(rule.Path))
            .Select(Normalise)
            .ToArray();
        var recursiveFolders = enabledRules
            .Where(rule => rule.Mode == ProtectionSelectionMode.RecursiveFolder)
            .ToArray();
        var compiled = new List<WatchedFolderConfiguration>();

        foreach (var rule in enabledRules.Where(rule => rule.Mode is ProtectionSelectionMode.RecursiveFolder or ProtectionSelectionMode.ImmediateFiles))
        {
            if (rule.Mode == ProtectionSelectionMode.ImmediateFiles && IsCoveredByRecursiveFolder(rule.Path, recursiveFolders))
            {
                continue;
            }

            compiled.Add(new WatchedFolderConfiguration(
                rule.Id,
                rule.Path,
                Recursive: rule.Mode == ProtectionSelectionMode.RecursiveFolder,
                IncludePatterns: DefaultIncludePatterns,
                ExcludePatterns: DefaultExcludePatterns,
                rule.Compression,
                rule.ResourceProfile,
                IsEnabled: true));
        }

        foreach (var group in enabledRules
                     .Where(rule => rule.Mode == ProtectionSelectionMode.File)
                     .Where(rule => !IsCoveredByRecursiveFolder(rule.Path, recursiveFolders))
                     .Where(rule => !IsCoveredByImmediateParent(rule.Path, enabledRules))
                     .GroupBy(rule => Path.GetDirectoryName(rule.Path) ?? string.Empty, StringComparer.OrdinalIgnoreCase)
                     .Where(group => !string.IsNullOrWhiteSpace(group.Key)))
        {
            var first = group.First();
            compiled.Add(new WatchedFolderConfiguration(
                StableFileGroupId(group.Key),
                group.Key,
                Recursive: false,
                IncludePatterns: group
                    .Select(rule => Path.GetFileName(rule.Path))
                    .Where(name => !string.IsNullOrWhiteSpace(name))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Order(StringComparer.OrdinalIgnoreCase)
                    .ToArray(),
                ExcludePatterns: DefaultExcludePatterns,
                first.Compression,
                first.ResourceProfile,
                IsEnabled: true));
        }

        return compiled
            .OrderBy(folder => folder.Path, StringComparer.OrdinalIgnoreCase)
            .ThenBy(folder => folder.Id, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static ProtectionSelectionRule Normalise(ProtectionSelectionRule rule)
    {
        return rule with { Path = Path.GetFullPath(rule.Path) };
    }

    private static bool IsCoveredByRecursiveFolder(string path, IReadOnlyList<ProtectionSelectionRule> recursiveFolders)
    {
        return recursiveFolders.Any(folder => IsSamePath(path, folder.Path) || IsUnderPath(path, folder.Path));
    }

    private static bool IsCoveredByImmediateParent(string filePath, IReadOnlyList<ProtectionSelectionRule> rules)
    {
        var parent = Path.GetDirectoryName(filePath);
        return parent is not null
               && rules.Any(rule => rule.Mode == ProtectionSelectionMode.ImmediateFiles
                                    && IsSamePath(parent, rule.Path));
    }

    private static bool IsSamePath(string left, string right)
    {
        return string.Equals(
            TrimPath(left),
            TrimPath(right),
            StringComparison.OrdinalIgnoreCase);
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

    private static string StableFileGroupId(string parentPath)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(TrimPath(parentPath).ToUpperInvariant()));
        return "files-" + Convert.ToHexString(bytes, 0, 6).ToLowerInvariant();
    }
}
