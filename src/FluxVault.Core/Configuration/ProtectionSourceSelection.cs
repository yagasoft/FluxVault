using System.IO.Enumeration;
using FluxVault.Abstractions.Configuration;

namespace FluxVault.Core.Configuration;

/// <summary>The same saved source/filter decision used by manual and automatic capture.</summary>
public static class ProtectionSourceSelection
{
    public static bool TryFindFolder(FluxVaultConfiguration configuration, string path, out WatchedFolderConfiguration folder)
    {
        var fullPath = Path.GetFullPath(path);
        foreach (var candidate in configuration.WatchedFolders.Where(item => item.IsEnabled))
        {
            var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(candidate.Path));
            var prefix = Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar;
            var under = candidate.Recursive ? fullPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) :
                string.Equals(Path.GetDirectoryName(fullPath), root, StringComparison.OrdinalIgnoreCase);
            var name = Path.GetFileName(fullPath);
            if (under && (candidate.IncludePatterns.Count == 0 || candidate.IncludePatterns.Any(pattern => FileSystemName.MatchesSimpleExpression(pattern, name, true))) &&
                !candidate.ExcludePatterns.Any(pattern => FileSystemName.MatchesSimpleExpression(pattern, name, true)) &&
                !ProtectionExclusionMatcher.IsFileExcluded(fullPath, configuration.ExclusionRules) &&
                ProtectionSelectionRegexMatcher.IsFileIncluded(fullPath, configuration.SelectionRules))
            { folder=candidate; return true; }
        }
        folder=null!;
        return false;
    }
}
