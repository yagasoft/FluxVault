using System.Runtime.Versioning;
using System.Security.Principal;
using FluxVault.Abstractions.Configuration;
using FluxVault.Abstractions.Ipc;
using FluxVault.Core.Chunking;
using FluxVault.Core.Content;
using FluxVault.Core.Ipc;
using FluxVault.Core.Security;
using FluxVault.Core.Storage;
using FluxVault.Core.Storage.Metadata;

namespace FluxVault.Windows.Security;

/// <summary>Once-only authenticated installation. This is never registered on the normal service pipe.</summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsSingleVaultProvisioner : IAuthenticatedFluxVaultRequestHandler, IAsyncDisposable
{
    private readonly FileStream ticketStream;
    private readonly IDisposable ticketPins;
    private readonly PostgreSqlVaultCatalogue catalogue;
    private readonly SemaphoreSlim mutation = new(1, 1);
    private readonly FluxVaultConfiguration configuration;
    private FluxVaultIpcResponse? result;
    private int terminal;
    public FluxVaultProvisioningTicket Ticket { get; }
    public bool Terminal => Volatile.Read(ref terminal) != 0;
    public FluxVaultIpcResponse? Result => result;
    internal Exception? LastFailure { get; private set; }
    internal Func<CancellationToken, Task>? BeforeBootstrapPublication { get; set; }
    internal Func<CancellationToken, Task>? BeforeStorageCreation { get; set; }

    private WindowsSingleVaultProvisioner(FileStream stream, IDisposable pins, FluxVaultProvisioningTicket ticket)
    {
        ticketStream = stream; ticketPins = pins; Ticket = ticket;
        configuration = ticket.Validate(); catalogue = new(ticket.Installation.Endpoint);
    }

    public static WindowsSingleVaultProvisioner Open(string ticketPath, string bootstrapPath)
    {
        using var identity = WindowsIdentity.GetCurrent();
        if (identity.User?.Value != "S-1-5-18" || identity.ImpersonationLevel != TokenImpersonationLevel.None)
            throw new UnauthorizedAccessException("Provisioning requires the LocalSystem process identity without impersonation.");
        var (stream, pins) = WindowsVaultStorageGuard.OpenProtectedFile(ticketPath, "S-1-5-18", WindowsVaultStorageGuard.ReadTrustedPrincipals());
        try
        {
            if (stream.Length is 0 or > FluxVaultProvisioningTicket.MaximumBytes) throw new InvalidDataException("Provisioning ticket size is invalid.");
            var bytes = new byte[checked((int)stream.Length)]; stream.ReadExactly(bytes);
            if (stream.ReadByte() != -1) throw new InvalidDataException("Provisioning ticket changed while reading.");
            var ticket = FluxVaultProvisioningTicket.Parse(bytes);
            if (!string.Equals(WindowsCallerFileAccess.ValidatePath(ticket.BootstrapPath), WindowsCallerFileAccess.ValidatePath(bootstrapPath), StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Provisioning ticket names another bootstrap target.");
            return new(stream, pins, ticket);
        }
        catch { stream.Dispose(); pins.Dispose(); throw; }
    }

    public async Task<FluxVaultIpcResponse> HandleAsync(FluxVaultCallerContext caller, FluxVaultIpcRequest request, CancellationToken token = default)
    {
        if (!Ticket.Accepts(caller, request))
            return FluxVaultIpcResponse.Failure("Installation setup is unavailable to this caller or request.") with { ErrorCode = FluxVaultIpcErrorCode.Denied };
        await mutation.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (result is not null) return result;
            if (Terminal) return FluxVaultIpcResponse.Failure("This setup attempt has ended. Reconcile its protected bootstrap before any further action.");
            // An authenticated attempt is once-only even if preflight fails. Wrong
            // actors/requests above cannot latch the attempt or mutate any target.
            Volatile.Write(ref terminal, 1);
            try
            {
                using var storage = WindowsVaultProvisioningStorage.Prepare(Ticket, configuration);
                await catalogue.VerifyFreshTargetAsync(Ticket.Installation.Binding, token).ConfigureAwait(false);
                if (BeforeStorageCreation is not null) await BeforeStorageCreation(token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                storage.Create(token);
                await catalogue.ProvisionAsync(token).ConfigureAwait(false);
                await using (var metadata = new PostgreSqlRepositoryMetadataStore(Ticket.Installation.Binding))
                {
                    await metadata.ProvisionVaultAsync(token).ConfigureAwait(false);
                    var repository = new FileSystemChunkRepository(Ticket.Installation.Binding, new FastCdcChunker(new()),
                        new Blake3ContentHasher(), new ZstdChunkCodec(), configuration.MirrorSet, metadata);
                    await repository.ProvisionVaultStorageAsync(token).ConfigureAwait(false);
                }
                var vault = await catalogue.InitializeAsync(caller, Ticket.Installation.Binding, "FluxVault", configuration, token).ConfigureAwait(false);
                await catalogue.VerifyInstallationAsync(Ticket.Installation, token).ConfigureAwait(false);
                using (new WindowsVaultStorageGuard().Open(vault, token)) { }
                if (BeforeBootstrapPublication is not null) await BeforeBootstrapPublication(token).ConfigureAwait(false);
                await storage.PublishAsync(Ticket, token).ConfigureAwait(false);
                return result = FluxVaultIpcResponse.Ok() with { VaultId = vault.Binding.Id, VaultRevision = vault.Revision, OperationId = Ticket.Installation.Endpoint.InstanceId };
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                LastFailure = exception;
                return result = FluxVaultIpcResponse.Failure("Installation setup did not confirm completion. Any owned state is retained. Reconcile the protected bootstrap and normal authorised status before operator recovery or another attempt.");
            }
        }
        finally { mutation.Release(); }
    }

    // Call only after the server has stopped admission and joined all requests.
    public async ValueTask DisposeAsync()
    {
        try { await catalogue.DisposeAsync().ConfigureAwait(false); }
        finally { mutation.Dispose(); ticketStream.Dispose(); ticketPins.Dispose(); }
    }
}
