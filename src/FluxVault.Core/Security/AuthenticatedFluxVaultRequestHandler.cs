using FluxVault.Abstractions.Ipc;
using FluxVault.Abstractions.Security;
using FluxVault.Core.Ipc;
using Microsoft.Extensions.Logging;
using FluxVault.Abstractions.Storage;
using FluxVault.Core.Storage.Metadata;

namespace FluxVault.Core.Security;

/// <summary>The executor must retain this admitted binding/revision and use caller-authorised file handles.</summary>
public interface IAuthorisedVaultCommandExecutor
{
    bool CanExecute(FluxVaultIpcRequest request) => true;
    Task<FluxVaultIpcResponse> ExecuteAsync(FluxVaultCallerContext caller, VaultAdmission admission,
        FluxVaultIpcRequest request, CancellationToken cancellationToken = default);
}

public sealed class AuthenticatedFluxVaultRequestHandler(IVaultCatalogue catalogue, IAuthorisedVaultCommandExecutor executor,
    ILogger<AuthenticatedFluxVaultRequestHandler>? logger = null, FluxVaultIpcLimits? limits = null) : IAuthenticatedFluxVaultRequestHandler
{
    // One installation owns one vault. Admission remains inside its mutation gate
    // so a queued command never carries an earlier configuration snapshot.
    private readonly SemaphoreSlim mutationGate = CreateMutationGate(limits ?? new());

    public async Task<FluxVaultIpcResponse> HandleAsync(FluxVaultCallerContext caller, FluxVaultIpcRequest request,
        CancellationToken cancellationToken = default)
    {
        if (!caller.ImpersonationPermitted || string.IsNullOrWhiteSpace(caller.UserSid) || !VaultCommandPolicy.TryGet(request, out _))
            return Denied();
        var mutation = PostgreSqlVaultCatalogue.IsMutation(request.Command);
        var executionStarted = false;
        var catalogueMutationStarted = false;
        var admissionStarted = false;
        SemaphoreSlim? heldGate = null;
        try
        {
            if (request.VaultId is { IsValid: false } || request.VaultId is null && request.Command != FluxVaultIpcCommand.GetStatus) return Denied();
            if (request.Command == FluxVaultIpcCommand.GetOperationStatus)
            {
                if (request.OperationId is null || request.OperationId == Guid.Empty) return Denied();
                var receipt = await catalogue.GetReceiptAsync(caller, request.VaultId!.Value, request.OperationId.Value, cancellationToken);
                return ReceiptResponse(receipt, request.VaultId.Value, request.OperationId.Value);
            }
            if (!executor.CanExecute(request))
                return Failure(FluxVaultIpcErrorCode.InvalidRequest, "This command is not available in this build. No change was started by this request.", request);
            if (mutation)
            {
                await mutationGate.WaitAsync(cancellationToken);
                heldGate = mutationGate;
            }
            cancellationToken.ThrowIfCancellationRequested();
            admissionStarted = true;
            catalogueMutationStarted = request.Command is FluxVaultIpcCommand.SaveConfiguration or FluxVaultIpcCommand.SetVaultAccess or
                FluxVaultIpcCommand.SetProtectionPaused;
            var admission = request.Command switch
            {
                FluxVaultIpcCommand.SaveConfiguration => await catalogue.SaveConfigurationAsync(caller, request, cancellationToken),
                FluxVaultIpcCommand.SetVaultAccess => await catalogue.SetAccessAsync(caller, request, cancellationToken),
                _ => await catalogue.AdmitAsync(caller, request, cancellationToken)
            };
            var id = admission.Vault.Binding.Id;
            var boundRequest = request with { VaultId = id };
            if (admission.IsReplay || admission.Receipt?.State == VaultOperationState.Completed)
                return ReceiptResponse(admission.Receipt, id, request.OperationId ?? Guid.Empty);
            executionStarted = true;
            var response = await executor.ExecuteAsync(caller, admission, boundRequest, cancellationToken);
            response = response with { VaultId = id, VaultRevision = admission.Vault.Revision, OperationId = request.OperationId };
            response = RepositoryHistoryPaging.BoundResponse(response, Math.Min(RepositoryHistoryPaging.MaximumResponseBytes, (limits ?? new()).MaximumResponseBytes));
            if (admission.Receipt is not null)
            {
                if (request.Command == FluxVaultIpcCommand.RunMirrorDrain)
                    return await catalogue.CompleteMirrorDrainAsync(admission.Receipt, boundRequest, response, cancellationToken);
                await catalogue.CompleteAsync(admission.Receipt, response, cancellationToken);
            }
            return response;
        }
        catch (OperationCanceledException) when (!admissionStarted)
        {
            return Failure(FluxVaultIpcErrorCode.Unavailable, "The request was cancelled before admission. No change was started.", request);
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
        catch (RepositoryHistoryChangedException)
        {
            return Failure(FluxVaultIpcErrorCode.HistoryChanged, "History changed. Refresh before browsing further.", request);
        }
        catch (Exception exception)
        {
            logger?.LogWarning(exception, "Vault command acknowledgement could not be confirmed; execution started: {ExecutionStarted}", executionStarted);
            return Failure(mutation ? FluxVaultIpcErrorCode.OutcomeUnknown : FluxVaultIpcErrorCode.Unavailable,
                mutation ? "The operation outcome could not be confirmed. Keep pending edits and check its status before retrying." :
                    executionStarted ? "The vault command could not be completed. Try again when the vault is available." :
                        "The vault catalogue is unavailable. No repository command was started; try again when it is available.", request);
        }
        finally { heldGate?.Release(); }
    }

    private static SemaphoreSlim CreateMutationGate(FluxVaultIpcLimits limits)
    {
        limits.Validate();
        return new(1, 1);
    }

    private static FluxVaultIpcResponse Denied() => FluxVaultIpcResponse.Failure("The vault is unavailable or you do not have permission for this command.") with { ErrorCode = FluxVaultIpcErrorCode.Denied };
    private static FluxVaultIpcResponse Failure(FluxVaultIpcErrorCode code, string message, FluxVaultIpcRequest request) =>
        FluxVaultIpcResponse.Failure(message) with { ErrorCode = code, VaultId = request.VaultId, OperationId = request.OperationId };
    private static FluxVaultIpcResponse ReceiptResponse(VaultOperationReceipt? receipt, VaultId id, Guid operation) =>
        receipt?.Response ?? FluxVaultIpcResponse.Failure("This operation was admitted, but its outcome is not yet confirmed. It has not been executed again.") with
        { ErrorCode = FluxVaultIpcErrorCode.OutcomeUnknown, VaultId = id, OperationId = operation, VaultRevision = receipt?.Revision };
}
