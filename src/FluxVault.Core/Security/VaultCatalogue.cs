using FluxVault.Abstractions.Configuration;
using FluxVault.Abstractions.Ipc;
using FluxVault.Abstractions.Security;

namespace FluxVault.Core.Security;

public sealed record VaultBinding(VaultId Id, string RepositoryPath, string StateRoot,
    MetadataStoreConfiguration MetadataStore)
{
    public string MetadataNamespace => Id.MetadataNamespace;

    public void Validate()
    {
        if (!Id.IsValid || !Path.IsPathFullyQualified(RepositoryPath) || !Path.IsPathFullyQualified(StateRoot) ||
            string.Equals(Path.GetFullPath(RepositoryPath), Path.GetFullPath(StateRoot), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("A vault requires distinct absolute storage and state roots.");
        ArgumentNullException.ThrowIfNull(MetadataStore);
    }
}

public sealed record VaultCatalogueEntry(VaultBinding Binding, long Revision, string DisplayName,
    VaultAccessPolicy Access, FluxVaultConfiguration Configuration);

public enum VaultCatalogueFailure { Denied, StaleRevision, IdentityMismatch, InvalidConfiguration, OperationConflict }

public sealed class VaultCatalogueException(VaultCatalogueFailure failure, string? message = null) : Exception(message ?? (failure switch
{
    VaultCatalogueFailure.Denied => "The vault is unavailable or you do not have permission for this command.",
    VaultCatalogueFailure.StaleRevision => "The vault changed. Refresh before saving; your pending edits are retained.",
    VaultCatalogueFailure.OperationConflict => "This operation identity has already been used for a different request.",
    VaultCatalogueFailure.InvalidConfiguration => "This change would alter protected vault infrastructure.",
    _ => "The trusted vault catalogue could not be verified."
}))
{
    public VaultCatalogueFailure Failure { get; } = failure;
}

public enum VaultOperationState { Admitted, Completed }

public sealed record VaultOperationReceipt(Guid OperationId, VaultId VaultId, string ActorSid,
    FluxVaultIpcCommand Command, string Fingerprint, VaultOperationState State, long Revision,
    FluxVaultIpcResponse? Response, VaultPermission RequiredPermissions = VaultPermission.None);

public sealed record VaultAdmission(VaultCatalogueEntry Vault, VaultOperationReceipt? Receipt, bool IsReplay);

public interface IVaultCatalogue
{
    Task<VaultAdmission> AdmitAsync(FluxVaultCallerContext caller, FluxVaultIpcRequest request, CancellationToken cancellationToken = default);
    Task<VaultAdmission> SaveConfigurationAsync(FluxVaultCallerContext caller, FluxVaultIpcRequest request, CancellationToken cancellationToken = default);
    Task<VaultAdmission> SetAccessAsync(FluxVaultCallerContext caller, FluxVaultIpcRequest request, CancellationToken cancellationToken = default);
    Task<VaultOperationReceipt?> GetReceiptAsync(FluxVaultCallerContext caller, VaultId vaultId, Guid operationId, CancellationToken cancellationToken = default);
    Task CompleteAsync(VaultOperationReceipt receipt, FluxVaultIpcResponse response, CancellationToken cancellationToken = default);
}
