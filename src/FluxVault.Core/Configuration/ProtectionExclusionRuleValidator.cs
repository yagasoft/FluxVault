using System.Text.RegularExpressions;
using FluxVault.Abstractions.Configuration;

namespace FluxVault.Core.Configuration;

public static class ProtectionExclusionRuleValidator
{
    private static readonly TimeSpan MatchTimeout = TimeSpan.FromMilliseconds(100);

    public static ProtectionExclusionRuleValidationResult Validate(IReadOnlyList<ProtectionExclusionRule> rules)
    {
        var errors = new List<string>();
        foreach (var rule in rules.Where(rule => rule.IsEnabled))
        {
            if (string.IsNullOrWhiteSpace(rule.Id))
            {
                errors.Add("Protection exclusion rule id is required.");
            }

            if (string.IsNullOrWhiteSpace(rule.Pattern))
            {
                errors.Add($"Protection exclusion rule '{rule.Id}' pattern is required.");
                continue;
            }

            try
            {
                _ = new Regex(
                    rule.Pattern,
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
                    MatchTimeout);
            }
            catch (ArgumentException ex)
            {
                errors.Add($"Protection exclusion rule '{rule.Id}' has an invalid regex: {ex.Message}");
            }
        }

        return new ProtectionExclusionRuleValidationResult(errors.Count == 0, errors);
    }
}

public sealed record ProtectionExclusionRuleValidationResult(
    bool IsValid,
    IReadOnlyList<string> Errors);
