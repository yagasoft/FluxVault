using FluxVault.Abstractions.Configuration;
using FluxVault.Abstractions.Security;

namespace FluxVault.Core.Security;

public static class ContentEncryptionPlanBuilder
{
    public static ContentEncryptionPlan Build(SecurityPostureConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var encryption = configuration.Normalise().ClientSideEncryption;
        if (!encryption.IsEnabled)
        {
            return new ContentEncryptionPlan(
                ContentEncryptionPlanState.Disabled,
                encryption.Algorithm,
                encryption.MetadataMode,
                ActiveKeyReference: null,
                "Client-side encryption is disabled; repository artefacts remain plain.");
        }

        var active = encryption.KeyReferences.FirstOrDefault(reference =>
            string.Equals(reference.Id, encryption.ActiveKeyReferenceId, StringComparison.OrdinalIgnoreCase));
        if (active is null)
        {
            return new ContentEncryptionPlan(
                ContentEncryptionPlanState.MissingActiveKeyReference,
                encryption.Algorithm,
                encryption.MetadataMode,
                ActiveKeyReference: null,
                "Client-side encryption is enabled but no active key reference is available.");
        }

        return new ContentEncryptionPlan(
            ContentEncryptionPlanState.Ready,
            encryption.Algorithm,
            encryption.MetadataMode,
            active,
            "Client-side encryption planned; active key reference configured.");
    }
}
