using System.Text.RegularExpressions;
using FluxVault.Abstractions.Configuration;

namespace FluxVault.Core.Configuration;

public static class ProtectionSelectionRegexMatcher
{
    private static readonly TimeSpan MatchTimeout = TimeSpan.FromMilliseconds(250);

    public static bool IsFileIncluded(string filePath, IReadOnlyList<ProtectionSelectionRule>? selectionRules)
    {
        if (selectionRules is null || selectionRules.Count == 0)
        {
            return true;
        }

        var fullPath = Path.GetFullPath(filePath);
        var applicable = selectionRules
            .Where(rule => rule.IsEnabled && CoversFile(rule, fullPath))
            .OrderBy(rule => TrimPath(rule.Path).Length)
            .ToArray();
        if (applicable.Length == 0)
        {
            return false;
        }

        var includeRules = applicable
            .SelectMany(rule => EnabledRules(rule.IncludeRegexRules, ProtectionExclusionTarget.File))
            .ToArray();
        var excludeRules = applicable
            .SelectMany(rule => EnabledRules(rule.ExcludeRegexRules, ProtectionExclusionTarget.File))
            .ToArray();

        return includeRules.All(rule => Matches(rule.Pattern, fullPath))
               && !excludeRules.Any(rule => Matches(rule.Pattern, fullPath));
    }

    public static bool IsFolderExcluded(string folderPath, IReadOnlyList<ProtectionSelectionRule>? selectionRules)
    {
        if (selectionRules is null || selectionRules.Count == 0)
        {
            return false;
        }

        var fullPath = Path.GetFullPath(folderPath);
        return selectionRules
            .Where(rule => rule.IsEnabled && CoversFolder(rule, fullPath))
            .SelectMany(rule => EnabledRules(rule.ExcludeRegexRules, ProtectionExclusionTarget.Folder))
            .Any(rule => Matches(rule.Pattern, fullPath));
    }

    private static IEnumerable<ProtectionScopedRegexRule> EnabledRules(
        IReadOnlyList<ProtectionScopedRegexRule>? rules,
        ProtectionExclusionTarget target)
    {
        return (rules ?? [])
            .Where(rule => rule.IsEnabled
                           && (rule.Target is ProtectionExclusionTarget.Both || rule.Target == target));
    }

    private static bool CoversFile(ProtectionSelectionRule rule, string filePath)
    {
        var rulePath = Path.GetFullPath(rule.Path);
        return rule.Mode switch
        {
            ProtectionSelectionMode.File => IsSamePath(filePath, rulePath),
            ProtectionSelectionMode.ImmediateFiles => IsDirectChildFile(filePath, rulePath),
            ProtectionSelectionMode.RecursiveFolder => IsSamePath(filePath, rulePath) || IsUnderPath(filePath, rulePath),
            ProtectionSelectionMode.RegexScope => IsSamePath(filePath, rulePath) || IsUnderPath(filePath, rulePath),
            _ => false
        };
    }

    private static bool CoversFolder(ProtectionSelectionRule rule, string folderPath)
    {
        var rulePath = Path.GetFullPath(rule.Path);
        return rule.Mode switch
        {
            ProtectionSelectionMode.ImmediateFiles => IsSamePath(folderPath, rulePath),
            ProtectionSelectionMode.RecursiveFolder => IsSamePath(folderPath, rulePath) || IsUnderPath(folderPath, rulePath),
            ProtectionSelectionMode.RegexScope => IsSamePath(folderPath, rulePath) || IsUnderPath(folderPath, rulePath),
            _ => false
        };
    }

    private static bool IsDirectChildFile(string filePath, string folderPath)
    {
        var parent = Path.GetDirectoryName(filePath);
        return parent is not null && IsSamePath(parent, folderPath);
    }

    private static bool Matches(string pattern, string path)
    {
        try
        {
            return Regex.IsMatch(path, pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, MatchTimeout);
        }
        catch (RegexMatchTimeoutException)
        {
            return false;
        }
        catch (ArgumentException)
        {
            return false;
        }
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
