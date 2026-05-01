using FluxVault.Abstractions.Configuration;
using FluxVault.Abstractions.Security;

namespace FluxVault.Core.Security;

public static class FleetPolicyEvaluator
{
    public static FleetPolicyEvaluation Evaluate(
        EnterpriseFleetConfiguration configuration,
        FleetPolicyDocument? policyDocument)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var normalised = configuration.Normalise();
        if (!normalised.IsEnabled)
        {
            return new FleetPolicyEvaluation(
                FleetPolicyEvaluationState.Disabled,
                AppliedPolicyId: null,
                AssignmentCount: 0,
                LocalStatusCount: normalised.LocalStatuses.Count,
                "Fleet policy is disabled; local-only operation.");
        }

        if (policyDocument is null)
        {
            return new FleetPolicyEvaluation(
                FleetPolicyEvaluationState.MissingPolicy,
                AppliedPolicyId: null,
                normalised.Assignments.Count,
                normalised.LocalStatuses.Count,
                "Fleet policy is enabled but no local policy document is loaded.");
        }

        var state = normalised.LocalStatuses.Any(status => status.State == FleetPolicyComplianceState.NonCompliant)
            ? FleetPolicyEvaluationState.NonCompliant
            : normalised.LocalStatuses.Any(status => status.State == FleetPolicyComplianceState.Warning)
                ? FleetPolicyEvaluationState.Warning
                : FleetPolicyEvaluationState.Compliant;
        return new FleetPolicyEvaluation(
            state,
            policyDocument.PolicyId,
            normalised.Assignments.Count,
            normalised.LocalStatuses.Count,
            $"Local fleet policy {policyDocument.PolicyId} evaluated from local status records.");
    }
}
