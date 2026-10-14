using System.Runtime.Versioning;
using FluxVault.Abstractions.Ipc;
using FluxVault.Core.Ipc;
using FluxVault.Core.Security;

namespace FluxVault.Windows.Security;

/// <summary>Single installation composition. The caller remains native and request-scoped.</summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsSingleVaultService : IAuthenticatedFluxVaultRequestHandler, IAsyncDisposable
{
    private readonly WindowsVaultInstallation installation;
    private readonly PostgreSqlVaultCatalogue catalogue;
    private readonly WindowsAuthorisedVaultCommandExecutor executor;
    private readonly AuthenticatedFluxVaultRequestHandler handler;
    private readonly WindowsAutomaticProtectionRuntime automatic;
    internal WindowsAutomaticProtectionRuntime AutomaticRuntimeForFixture => automatic;

    private WindowsSingleVaultService(WindowsVaultInstallation installation, PostgreSqlVaultCatalogue catalogue,
        WindowsAuthorisedVaultCommandExecutor executor, WindowsAutomaticProtectionRuntime automatic, SemaphoreSlim mutationGate)
    {
        this.installation = installation; this.catalogue = catalogue; this.executor = executor;
        this.automatic = automatic;
        handler = new(catalogue, executor, sharedMutationGate: mutationGate, prepareMutation: automatic.PrepareMutationAsync);
    }

    public static async Task<WindowsSingleVaultService> OpenAsync(string bootstrapPath, CancellationToken cancellationToken = default)
    {
        var installation = WindowsVaultInstallation.Open(bootstrapPath);
        PostgreSqlVaultCatalogue? catalogue = null;
        WindowsAuthorisedVaultCommandExecutor? executor = null;
        WindowsAutomaticProtectionRuntime? automatic = null;
        try
        {
            catalogue = new(installation.Configuration.Endpoint);
            var vault = await catalogue.VerifyInstallationAsync(installation.Configuration, cancellationToken);
            using (new WindowsVaultStorageGuard().OpenAutomatic(vault,out _,out _,cancellationToken)) { }
            executor = new(installation.Configuration.Endpoint, installation.Configuration.Binding);
            var mutationGate = new SemaphoreSlim(1, 1);
            automatic = new(catalogue, vault.Binding, executor.MetadataSource, mutationGate);
            await automatic.InitialiseAsync(cancellationToken);
            executor.RuntimeFence = catalogue.VerifyRuntimeFenceAsync;
            return new(installation, catalogue, executor, automatic, mutationGate);
        }
        catch
        {
            if (automatic is not null) await automatic.DisposeAsync();
            if (executor is not null) await executor.DisposeAsync();
            if (catalogue is not null) await catalogue.DisposeAsync();
            installation.Dispose();
            throw;
        }
    }

    public Task RunAutomaticProtectionAsync(CancellationToken cancellationToken) => automatic.RunAsync(cancellationToken);

    public async Task<FluxVaultIpcResponse> HandleAsync(FluxVaultCallerContext caller, FluxVaultIpcRequest request, CancellationToken cancellationToken = default)
    {
        var response = await handler.HandleAsync(caller, request, cancellationToken);
        if (response.Status is not { } status) return response;
        AutomaticCatalogueState saved;
        try { saved = await catalogue.ReadAutomaticStateAsync(installation.Configuration.Binding, cancellationToken); }
        catch (AutomaticProtectionUnavailableException exception)
        {
            return response with { Status = status with { AutomaticProtection = automatic.Status with
                { State = AutomaticProtectionState.Blocked, Reason = exception.Message,CanChangePreference=false }, LastMessage = exception.Message + " " + ManualStatusMessage(status) } };
        }
        var observed = ProjectAutomaticStatus(saved, automatic.Status, caller.UserSid, response.VaultRevision);
        return response with { Status = status with { AutomaticProtection = observed, LastMessage = observed.Reason + " " + ManualStatusMessage(status) } };
    }

    internal static AutomaticProtectionRuntimeStatus ProjectAutomaticStatus(AutomaticCatalogueState saved,
        AutomaticProtectionRuntimeStatus runtime, string callerSid, long? responseRevision)
    {
        var diagnostic = runtime.State is AutomaticProtectionState.Failed or AutomaticProtectionState.OutcomeUncertain;
        // A concurrent command can commit between the admitted response and this read.
        // Never attach its controls to an earlier receipt/configuration revision.
        if (saved.Vault.Revision != responseRevision)
            return runtime with { State = diagnostic ? runtime.State : AutomaticProtectionState.Reconciling,
                Reason = (diagnostic ? runtime.Reason + " " : "") + "Saved controls changed during this status read; refresh to confirm automatic protection.",
                CanChangePreference = false };
        var observed = runtime with { Policy = saved.Policy, RequiresCreatorApproval = saved.RequiresCreatorApproval,
            PendingCount = saved.PendingCount, UncertainCount = saved.UncertainCount,
            LastSuccessfulVersionId = saved.LastSuccessfulVersionId, LastSuccessfulUtc = saved.LastSuccessfulUtc,
            MirrorPendingCount=saved.MirrorPendingCount,MirrorReason=saved.MirrorReason,
            CanChangePreference=runtime.CanChangePreference && callerSid==saved.Vault.Access.OwnerSid };
        if (!diagnostic)
        {
            if (!saved.Vault.Configuration.IsEnabled)
                observed = observed with { State = AutomaticProtectionState.Paused, Reason = "Protection is paused; pending work and history are kept." };
            else if (!saved.Policy.Enabled)
                observed = observed with { State = AutomaticProtectionState.Disabled, Reason = "Automatic protection is off." };
            else if (saved.RequiresCreatorApproval)
                observed = observed with { State = AutomaticProtectionState.Blocked, Reason = "Creator approval is required for the saved source selection." };
            else if (observed.Policy != runtime.Policy || runtime.RequiresCreatorApproval ||
                runtime.State is AutomaticProtectionState.Paused or AutomaticProtectionState.Disabled)
                observed = observed with { State = AutomaticProtectionState.Reconciling, Reason = "Saved preference changed; the automatic runtime is updating." };
        }
        if(saved.MirrorPendingCount>0)observed=observed with {Reason=observed.Reason+" Mirror replication is pending for "+saved.MirrorPendingCount+" file(s). "+saved.MirrorReason};
        return observed;
    }

    private static string ManualStatusMessage(FluxVaultServiceStatus status) => status.LastMessage
        .Replace(WindowsAuthorisedVaultCommandExecutor.AutomaticProtectionNotice,"",StringComparison.Ordinal)
        .Replace(WindowsAuthorisedVaultCommandExecutor.ProtectionNotice(false),"",StringComparison.Ordinal);

    // The pipe server must join accepted requests before its owner disposes this scope.
    public async ValueTask DisposeAsync()
    {
        try { await automatic.DisposeAsync(); }
        finally { try { await executor.DisposeAsync(); }
        finally { try { await catalogue.DisposeAsync(); } finally { installation.Dispose(); } }
        }
    }
}
