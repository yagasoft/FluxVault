using System.Text;
using System.Text.RegularExpressions;
using FluxVault.Abstractions.Policies;

namespace FluxVault.Core.Policies;

public static class PolicyResolver
{
    public static ResolvedProtectionPolicy Resolve(
        ProtectionPolicy global,
        WatchedFolderPolicy folder,
        string filePath)
    {
        ArgumentNullException.ThrowIfNull(global);
        ArgumentNullException.ThrowIfNull(folder);
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);

        var selected = folder.Overrides.FirstOrDefault(rule => GlobMatches(rule.Glob, filePath));

        return new ResolvedProtectionPolicy(
            folder.Id,
            selected?.ResourceProfile ?? folder.ResourceProfile,
            selected?.Compression ?? folder.Compression,
            selected?.MinimumCompressionBytes ?? folder.MinimumCompressionBytes,
            folder.MaximumFileBytes > 0 ? folder.MaximumFileBytes : global.MaximumFileBytes);
    }

    private static bool GlobMatches(string glob, string path)
    {
        var normalisedGlob = glob.Replace('\\', '/');
        var normalisedPath = path.Replace('\\', '/');
        var regex = new StringBuilder("^");

        for (var i = 0; i < normalisedGlob.Length; i++)
        {
            var current = normalisedGlob[i];
            if (current == '*')
            {
                if (i + 1 < normalisedGlob.Length && normalisedGlob[i + 1] == '*')
                {
                    regex.Append(".*");
                    i++;
                }
                else
                {
                    regex.Append("[^/]*");
                }
            }
            else if (current == '?')
            {
                regex.Append("[^/]");
            }
            else
            {
                regex.Append(Regex.Escape(current.ToString()));
            }
        }

        regex.Append('$');
        return Regex.IsMatch(normalisedPath, regex.ToString(), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }
}
