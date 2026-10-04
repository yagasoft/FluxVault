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

    private WindowsSingleVaultService(WindowsVaultInstallation installation, PostgreSqlVaultCatalogue catalogue,
        WindowsAuthorisedVaultCommandExecutor executor)
    {
        this.installation = installation; this.catalogue = catalogue; this.executor = executor;
        handler = new(catalogue, executor);
    }

    public static async Task<WindowsSingleVaultService> OpenAsync(string bootstrapPath, CancellationToken cancellationToken = default)
    {
        var installation = WindowsVaultInstallation.Open(bootstrapPath);
        PostgreSqlVaultCatalogue? catalogue = null;
        WindowsAuthorisedVaultCommandExecutor? executor = null;
        try
        {
            catalogue = new(installation.Configuration.Endpoint);
            var vault = await catalogue.VerifyInstallationAsync(installation.Configuration, cancellationToken);
            using (new WindowsVaultStorageGuard().Open(vault, cancellationToken)) { }
            executor = new(installation.Configuration.Endpoint, installation.Configuration.Binding);
            return new(installation, catalogue, executor);
        }
        catch
        {
            if (executor is not null) await executor.DisposeAsync();
            if (catalogue is not null) await catalogue.DisposeAsync();
            installation.Dispose();
            throw;
        }
    }

    public async Task<FluxVaultIpcResponse> HandleAsync(FluxVaultCallerContext caller, FluxVaultIpcRequest request, CancellationToken cancellationToken = default)
    {
        var response = await handler.HandleAsync(caller, request, cancellationToken);
        return response.Status is not { } status ? response : response with
        { Status = status with { LastMessage = WindowsAuthorisedVaultCommandExecutor.ProtectionNotice(status.Configuration.IsEnabled) + status.LastMessage } };
    }

    // The pipe server must join accepted requests before its owner disposes this scope.
    public async ValueTask DisposeAsync()
    {
        try { await executor.DisposeAsync(); }
        finally { try { await catalogue.DisposeAsync(); } finally { installation.Dispose(); } }
    }
}
