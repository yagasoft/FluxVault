namespace FluxVault.Abstractions.Configuration;

public enum ClientSideEncryptionAlgorithm
{
    Aes256Gcm = 0
}

public enum EncryptionMetadataMode
{
    PlainMetadata = 0,
    ProtectedMetadata = 1
}

public enum EncryptionKeyProvider
{
    WindowsDpapi = 0,
    ExternalSecret = 1,
    AzureKeyVault = 2,
    AwsKms = 3,
    GoogleCloudKms = 4
}

public enum EncryptionKeyPurpose
{
    RepositoryContent = 0,
    Metadata = 1
}

public sealed record SecurityPostureConfiguration(
    ClientSideEncryptionConfiguration ClientSideEncryption = null!)
{
    public static SecurityPostureConfiguration CreateDefault()
    {
        return new SecurityPostureConfiguration(ClientSideEncryptionConfiguration.CreateDefault());
    }

    public SecurityPostureConfiguration Normalise()
    {
        return this with
        {
            ClientSideEncryption = (ClientSideEncryption ?? ClientSideEncryptionConfiguration.CreateDefault()).Normalise()
        };
    }
}

public sealed record ClientSideEncryptionConfiguration(
    bool IsEnabled = false,
    ClientSideEncryptionAlgorithm Algorithm = ClientSideEncryptionAlgorithm.Aes256Gcm,
    EncryptionMetadataMode MetadataMode = EncryptionMetadataMode.PlainMetadata,
    string? ActiveKeyReferenceId = null,
    IReadOnlyList<EncryptionKeyReferenceConfiguration> KeyReferences = null!)
{
    public static ClientSideEncryptionConfiguration CreateDefault()
    {
        return new ClientSideEncryptionConfiguration(IsEnabled: false, KeyReferences: []);
    }

    public ClientSideEncryptionConfiguration Normalise()
    {
        return this with
        {
            ActiveKeyReferenceId = string.IsNullOrWhiteSpace(ActiveKeyReferenceId)
                ? null
                : ActiveKeyReferenceId.Trim(),
            KeyReferences = (KeyReferences ?? [])
                .Where(reference => !string.IsNullOrWhiteSpace(reference.Id))
                .Select(reference => reference.Normalise())
                .ToArray()
        };
    }
}

public sealed record EncryptionKeyReferenceConfiguration(
    string Id,
    EncryptionKeyProvider Provider,
    string ReferenceName,
    EncryptionKeyPurpose Purpose = EncryptionKeyPurpose.RepositoryContent)
{
    public EncryptionKeyReferenceConfiguration Normalise()
    {
        return this with
        {
            Id = Id.Trim(),
            ReferenceName = ReferenceName.Trim()
        };
    }
}
