using FluxVault.Abstractions.Configuration;

namespace FluxVault.Abstractions.Security;

public enum ContentEncryptionPlanState
{
    Disabled = 0,
    MissingActiveKeyReference = 1,
    Ready = 2
}

public sealed record ContentEncryptionPlan(
    ContentEncryptionPlanState State,
    ClientSideEncryptionAlgorithm Algorithm,
    EncryptionMetadataMode MetadataMode,
    EncryptionKeyReferenceConfiguration? ActiveKeyReference,
    string Status);

public sealed record FleetPolicyDocument(
    string PolicyId,
    int Version,
    DateTimeOffset IssuedAtUtc,
    string MinimumClientVersion,
    IReadOnlyList<FleetPolicySetting> RequiredSettings);

public sealed record FleetPolicySetting(
    string Name,
    string Value);

public enum FleetPolicyEvaluationState
{
    Disabled = 0,
    MissingPolicy = 1,
    Compliant = 2,
    Warning = 3,
    NonCompliant = 4
}

public sealed record FleetPolicyEvaluation(
    FleetPolicyEvaluationState State,
    string? AppliedPolicyId,
    int AssignmentCount,
    int LocalStatusCount,
    string Status);
