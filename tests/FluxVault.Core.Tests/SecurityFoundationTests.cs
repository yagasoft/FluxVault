using FluxVault.Abstractions.Configuration;
using FluxVault.Abstractions.Security;
using FluxVault.Core.Security;

namespace FluxVault.Core.Tests;

public sealed class SecurityFoundationTests
{
    [Fact]
    public void Encryption_plan_reports_disabled_when_client_side_encryption_is_off()
    {
        var configuration = SecurityPostureConfiguration.CreateDefault();

        var plan = ContentEncryptionPlanBuilder.Build(configuration);

        Assert.Equal(ContentEncryptionPlanState.Disabled, plan.State);
        Assert.Equal(ClientSideEncryptionAlgorithm.Aes256Gcm, plan.Algorithm);
        Assert.Null(plan.ActiveKeyReference);
        Assert.DoesNotContain("key material", plan.Status, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Encryption_plan_uses_active_key_reference_without_exposing_key_material()
    {
        var key = new EncryptionKeyReferenceConfiguration(
            Id: "repository-key",
            Provider: EncryptionKeyProvider.ExternalSecret,
            ReferenceName: "kv://fluxvault/repository-key",
            Purpose: EncryptionKeyPurpose.RepositoryContent);
        var configuration = new SecurityPostureConfiguration(
            ClientSideEncryption: new ClientSideEncryptionConfiguration(
                IsEnabled: true,
                Algorithm: ClientSideEncryptionAlgorithm.Aes256Gcm,
                MetadataMode: EncryptionMetadataMode.ProtectedMetadata,
                ActiveKeyReferenceId: "repository-key",
                KeyReferences: [key]));

        var plan = ContentEncryptionPlanBuilder.Build(configuration);

        Assert.Equal(ContentEncryptionPlanState.Ready, plan.State);
        Assert.Equal(key, plan.ActiveKeyReference);
        Assert.Equal(EncryptionMetadataMode.ProtectedMetadata, plan.MetadataMode);
        Assert.DoesNotContain("kv://fluxvault/repository-key", plan.Status, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Fleet_policy_evaluator_reports_local_policy_compliance()
    {
        var checkedAt = new DateTimeOffset(2026, 5, 1, 11, 0, 0, TimeSpan.Zero);
        var configuration = new EnterpriseFleetConfiguration(
            IsEnabled: true,
            Mode: FleetPolicyMode.LocalManaged,
            PolicySource: "file://fleet-policy.json",
            Assignments:
            [
                new FleetPolicyAssignmentConfiguration(
                    Id: "assignment-1",
                    PolicyId: "policy-2026-05",
                    TargetDeviceId: "device-local",
                    AssignedAtUtc: checkedAt)
            ],
            LocalStatuses:
            [
                new FleetDeviceStatusConfiguration(
                    DeviceId: "device-local",
                    PolicyId: "policy-2026-05",
                    State: FleetPolicyComplianceState.Compliant,
                    CheckedAtUtc: checkedAt,
                    Detail: "Policy accepted.")
            ]);
        var document = new FleetPolicyDocument(
            PolicyId: "policy-2026-05",
            Version: 3,
            IssuedAtUtc: checkedAt,
            MinimumClientVersion: "1.0.1",
            RequiredSettings:
            [
                new FleetPolicySetting("clientSideEncryption", "planned")
            ]);

        var evaluation = FleetPolicyEvaluator.Evaluate(configuration, document);

        Assert.Equal(FleetPolicyEvaluationState.Compliant, evaluation.State);
        Assert.Equal("policy-2026-05", evaluation.AppliedPolicyId);
        Assert.Equal(1, evaluation.AssignmentCount);
        Assert.Equal(1, evaluation.LocalStatusCount);
        Assert.Contains("local", evaluation.Status, StringComparison.OrdinalIgnoreCase);
    }
}
