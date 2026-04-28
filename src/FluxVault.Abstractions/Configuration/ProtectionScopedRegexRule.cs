using System.Text.RegularExpressions;

namespace FluxVault.Abstractions.Configuration;

public sealed record ProtectionScopedRegexRule(
    string Id,
    string Pattern,
    ProtectionExclusionTarget Target,
    bool IsEnabled = true,
    string? Label = null,
    string? Description = null)
{
    public static ProtectionScopedRegexRule ExactPath(
        string id,
        string path,
        ProtectionExclusionTarget target,
        string? label = null,
        string? description = null)
    {
        return new ProtectionScopedRegexRule(
            id,
            $"^{Regex.Escape(Path.GetFullPath(path))}$",
            target,
            IsEnabled: true,
            label,
            description);
    }

    public static ProtectionScopedRegexRule PathPrefix(
        string id,
        string path,
        ProtectionExclusionTarget target,
        string? label = null,
        string? description = null)
    {
        var normalised = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var separator = Regex.Escape(Path.DirectorySeparatorChar.ToString());
        return new ProtectionScopedRegexRule(
            id,
            $"^{Regex.Escape(normalised)}({separator}|$)",
            target,
            IsEnabled: true,
            label,
            description);
    }
}
