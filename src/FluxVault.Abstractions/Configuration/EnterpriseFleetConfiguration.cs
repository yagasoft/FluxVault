namespace FluxVault.Abstractions.Configuration;

public enum FleetPolicyMode
{
    LocalOnly = 0,
    LocalManaged = 1,
    EnterpriseManaged = 2
}

public enum FleetPolicyComplianceState
{
    Unknown = 0,
    Compliant = 1,
    Warning = 2,
    NonCompliant = 3
}

public sealed record EnterpriseFleetConfiguration(
    bool IsEnabled = false,
    FleetPolicyMode Mode = FleetPolicyMode.LocalOnly,
    string? PolicySource = null,
    IReadOnlyList<FleetPolicyAssignmentConfiguration> Assignments = null!,
    IReadOnlyList<FleetDeviceStatusConfiguration> LocalStatuses = null!)
{
    public static EnterpriseFleetConfiguration CreateDefault()
    {
        return new EnterpriseFleetConfiguration(IsEnabled: false, Assignments: [], LocalStatuses: []);
    }

    public EnterpriseFleetConfiguration Normalise()
    {
        return this with
        {
            PolicySource = string.IsNullOrWhiteSpace(PolicySource) ? null : PolicySource.Trim(),
            Assignments = (Assignments ?? [])
                .Where(assignment => !string.IsNullOrWhiteSpace(assignment.Id))
                .Select(assignment => assignment.Normalise())
                .ToArray(),
            LocalStatuses = (LocalStatuses ?? [])
                .Where(status => !string.IsNullOrWhiteSpace(status.DeviceId))
                .Select(status => status.Normalise())
                .ToArray()
        };
    }
}

public sealed record FleetPolicyAssignmentConfiguration(
    string Id,
    string PolicyId,
    string TargetDeviceId,
    DateTimeOffset AssignedAtUtc)
{
    public FleetPolicyAssignmentConfiguration Normalise()
    {
        return this with
        {
            Id = Id.Trim(),
            PolicyId = PolicyId.Trim(),
            TargetDeviceId = TargetDeviceId.Trim()
        };
    }
}

public sealed record FleetDeviceStatusConfiguration(
    string DeviceId,
    string PolicyId,
    FleetPolicyComplianceState State,
    DateTimeOffset CheckedAtUtc,
    string Detail)
{
    public FleetDeviceStatusConfiguration Normalise()
    {
        return this with
        {
            DeviceId = DeviceId.Trim(),
            PolicyId = PolicyId.Trim(),
            Detail = string.IsNullOrWhiteSpace(Detail) ? State.ToString() : Detail.Trim()
        };
    }
}
