using FluxVault.Abstractions.Policies;

namespace FluxVault.Abstractions.Configuration;

public sealed record WorkloadPolicyConfiguration(WorkloadPolicyPresetId DefaultPreset)
{
    public static WorkloadPolicyConfiguration CreateDefault()
    {
        return new WorkloadPolicyConfiguration(WorkloadPolicyPresetId.GeneralPurpose);
    }
}
