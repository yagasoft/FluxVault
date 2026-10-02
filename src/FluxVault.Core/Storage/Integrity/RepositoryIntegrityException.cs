namespace FluxVault.Core.Storage.Integrity;

public enum RepositoryIntegrityFailure
{
    CorruptObject, MissingObject, InvalidManifest, LimitExceeded, DescriptorConflict,
    OwnershipUnknown, OwnershipMismatch, RepositoryBusy
}

public sealed class RepositoryIntegrityException(RepositoryIntegrityFailure code, string message) : IOException(message)
{
    public RepositoryIntegrityFailure Code { get; } = code;
}
