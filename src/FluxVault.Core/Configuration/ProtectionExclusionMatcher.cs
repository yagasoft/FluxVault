using System.Text.RegularExpressions;
using FluxVault.Abstractions.Configuration;

namespace FluxVault.Core.Configuration;

public static class ProtectionExclusionMatcher
{
    private static readonly TimeSpan MatchTimeout = TimeSpan.FromMilliseconds(100);

    public static bool IsFileExcluded(string filePath, IReadOnlyList<ProtectionExclusionRule> rules)
    {
        var fullPath = Path.GetFullPath(filePath);
        foreach (var rule in EnabledRules(rules))
        {
            if (rule.Target is ProtectionExclusionTarget.File or ProtectionExclusionTarget.Both
                && IsMatch(rule, fullPath))
            {
                return true;
            }

            if (rule.Target is ProtectionExclusionTarget.Folder or ProtectionExclusionTarget.Both
                && EnumerateParentFolders(fullPath).Any(folder => IsMatch(rule, folder)))
            {
                return true;
            }
        }

        return false;
    }

    public static bool IsFolderExcluded(string folderPath, IReadOnlyList<ProtectionExclusionRule> rules)
    {
        var fullPath = Path.GetFullPath(folderPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return EnabledRules(rules)
            .Where(rule => rule.Target is ProtectionExclusionTarget.Folder or ProtectionExclusionTarget.Both)
            .Any(rule => IsMatch(rule, fullPath));
    }

    private static IEnumerable<ProtectionExclusionRule> EnabledRules(IReadOnlyList<ProtectionExclusionRule> rules)
    {
        return rules.Where(rule => rule.IsEnabled && !string.IsNullOrWhiteSpace(rule.Pattern));
    }

    private static bool IsMatch(ProtectionExclusionRule rule, string path)
    {
        try
        {
            return Regex.IsMatch(
                path,
                rule.Pattern,
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
                MatchTimeout);
        }
        catch (RegexMatchTimeoutException)
        {
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static IEnumerable<string> EnumerateParentFolders(string filePath)
    {
        var directory = Path.GetDirectoryName(filePath);
        while (!string.IsNullOrWhiteSpace(directory))
        {
            yield return directory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            directory = Path.GetDirectoryName(directory);
        }
    }
}
