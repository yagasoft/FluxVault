using FluxVault.Abstractions.Ipc;
using FluxVault.Abstractions.Security;
using FluxVault.Core.Ipc;
using Microsoft.Extensions.Logging;

namespace FluxVault.Core.Security;

/// <summary>The executor must retain this admitted binding/revision and use caller-authorised file handles.</summary>
public interface IAuthorisedVaultCommandExecutor
{
    Task<FluxVaultIpcResponse> ExecuteAsync(FluxVaultCallerContext caller, VaultAdmission admission,
        FluxVaultIpcRequest request, CancellationToken cancellationToken = default);
}

public sealed class AuthenticatedFluxVaultRequestHandler(IVaultCatalogue catalogue, IAuthorisedVaultCommandExecutor executor,
    ILogger<AuthenticatedFluxVaultRequestHandler>? logger = null) : IAuthenticatedFluxVaultRequestHandler
{
    public async Task<FluxVaultIpcResponse> HandleAsync(FluxVaultCallerContext caller, FluxVaultIpcRequest request,
        CancellationToken cancellationToken = default)
    {
        if (!caller.ImpersonationPermitted || string.IsNullOrWhiteSpace(caller.UserSid) || !VaultCommandPolicy.TryGet(request, out _))
            return Denied();
        var mutation = PostgreSqlVaultCatalogue.IsMutation(request.Command);
        var executionStarted = false;
        var catalogueMutationStarted = false;
        try
        {
            if (request.Command == FluxVaultIpcCommand.ListVaults)
            {
                var page = await catalogue.ListAccessibleAsync(caller, request.VaultsAfter, request.VaultPageSize, cancellationToken);
                return FluxVaultIpcResponse.Ok() with { Vaults = page.Vaults, NextVaultAfter = page.NextAfter };
            }
            if (request.VaultId is not { IsValid: true } id) return Denied();
            if (request.Command == FluxVaultIpcCommand.GetOperationStatus)
            {
                if (request.OperationId is null || request.OperationId == Guid.Empty) return Denied();
                var receipt = await catalogue.GetReceiptAsync(caller, id, request.OperationId.Value, cancellationToken);
                return ReceiptResponse(receipt, id, request.OperationId.Value);
            }
            // Lifecycle provisioning must establish protected physical resources before it can become public.
            // No legacy profile-manager fallback is permitted in this intermediate security implementation.
            if (request.Command is FluxVaultIpcCommand.CreateProfile or FluxVaultIpcCommand.DuplicateProfile)
                return Failure(FluxVaultIpcErrorCode.Unavailable, "Vault provisioning is not available in this build.", request);
            catalogueMutationStarted = request.Command is FluxVaultIpcCommand.SaveConfiguration or FluxVaultIpcCommand.SetVaultAccess;
            var admission = request.Command switch
            {
                FluxVaultIpcCommand.SaveConfiguration => await catalogue.SaveConfigurationAsync(caller, request, cancellationToken),
                FluxVaultIpcCommand.SetVaultAccess => await catalogue.SetAccessAsync(caller, request, cancellationToken),
                _ => await catalogue.AdmitAsync(caller, request, cancellationToken)
            };
            if (admission.IsReplay || admission.Receipt?.State == VaultOperationState.Completed)
                return ReceiptResponse(admission.Receipt, id, request.OperationId ?? Guid.Empty);
            if (request.Command == FluxVaultIpcCommand.SetActiveProfile)
                return FluxVaultIpcResponse.Ok() with { VaultId = id, VaultRevision = admission.Vault.Revision };
            executionStarted = true;
            var response = await executor.ExecuteAsync(caller, admission, request, cancellationToken);
            response = response with { VaultId = id, VaultRevision = admission.Vault.Revision, OperationId = request.OperationId };
            if (admission.Receipt is not null) await catalogue.CompleteAsync(admission.Receipt, response, cancellationToken);
            return response;
        }
        catch (VaultCatalogueException exception) when (!executionStarted)
        {
            var code = exception.Failure switch
            {
                VaultCatalogueFailure.Denied => FluxVaultIpcErrorCode.Denied,
                VaultCatalogueFailure.StaleRevision => FluxVaultIpcErrorCode.StaleRevision,
                VaultCatalogueFailure.OperationConflict => FluxVaultIpcErrorCode.OperationConflict,
                VaultCatalogueFailure.InvalidConfiguration => FluxVaultIpcErrorCode.InvalidRequest,
                _ => FluxVaultIpcErrorCode.Unavailable
            };
            return Failure(code, exception.Message, request);
        }
        catch (Exception exception) when (!executionStarted && !catalogueMutationStarted && exception is ArgumentException or System.Text.Json.JsonException)
        {
            logger?.LogWarning(exception, "Vault request or stored payload was rejected");
            return Failure(FluxVaultIpcErrorCode.InvalidRequest, "The request or stored vault data could not be validated.", request);
        }
        catch (Exception exception)
        {
            logger?.LogWarning(exception, "Vault command acknowledgement could not be confirmed; execution started: {ExecutionStarted}", executionStarted);
            return Failure(mutation ? FluxVaultIpcErrorCode.OutcomeUnknown : FluxVaultIpcErrorCode.Unavailable,
                mutation ? "The operation outcome could not be confirmed. Keep pending edits and check its status before retrying." :
                    executionStarted ? "The vault command could not be completed. Try again when the vault is available." :
                        "The vault catalogue is unavailable. No repository command was started; try again when it is available.", request);
        }
    }

    private static FluxVaultIpcResponse Denied() => FluxVaultIpcResponse.Failure("The vault is unavailable or you do not have permission for this command.") with { ErrorCode = FluxVaultIpcErrorCode.Denied };
    private static FluxVaultIpcResponse Failure(FluxVaultIpcErrorCode code, string message, FluxVaultIpcRequest request) =>
        FluxVaultIpcResponse.Failure(message) with { ErrorCode = code, VaultId = request.VaultId, OperationId = request.OperationId };
    private static FluxVaultIpcResponse ReceiptResponse(VaultOperationReceipt? receipt, VaultId id, Guid operation) =>
        receipt?.Response ?? FluxVaultIpcResponse.Failure("This operation was admitted, but its outcome is not yet confirmed. It has not been executed again.") with
        { ErrorCode = FluxVaultIpcErrorCode.OutcomeUnknown, VaultId = id, OperationId = operation, VaultRevision = receipt?.Revision };
}
