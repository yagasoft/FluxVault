using System.IO.Pipes;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using FluxVault.Abstractions.Ipc;
using Microsoft.Extensions.Logging;

namespace FluxVault.Core.Ipc;

public sealed class NamedPipeFluxVaultServer(
    IFluxVaultRequestHandler handler,
    string pipeName = NamedPipeFluxVaultServer.DefaultPipeName,
    ILogger<NamedPipeFluxVaultServer>? logger = null)
{
    public const string DefaultPipeName = "FluxVault.Service";
    private const int MaxConcurrentClients = 32;
    private const int PendingListenerCount = 8;

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var listeners = Enumerable
            .Range(0, PendingListenerCount)
            .Select(_ => AcceptLoopAsync(cancellationToken))
            .ToArray();
        await Task.WhenAll(listeners).ConfigureAwait(false);
    }

    private async Task AcceptLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            NamedPipeServerStream? pipe = null;

            try
            {
                pipe = CreateServerStreamForCurrentPlatform(pipeName);
                await pipe.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
                var connectedPipe = pipe;
                pipe = null;
                _ = Task.Run(() => HandleConnectionAsync(connectedPipe, cancellationToken), CancellationToken.None);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                if (pipe is not null)
                {
                    await pipe.DisposeAsync().ConfigureAwait(false);
                }

                return;
            }
            catch (IOException)
            {
                if (pipe is not null)
                {
                    await pipe.DisposeAsync().ConfigureAwait(false);
                }

                await Task.Delay(TimeSpan.FromMilliseconds(50), cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private static NamedPipeServerStream CreateServerStreamForCurrentPlatform(string pipeName)
    {
        if (OperatingSystem.IsWindows())
        {
            return CreateServerStream(pipeName);
        }

        return new NamedPipeServerStream(
            pipeName,
            PipeDirection.InOut,
            maxNumberOfServerInstances: MaxConcurrentClients,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous);
    }

    [SupportedOSPlatform("windows")]
    internal static NamedPipeServerStream CreateServerStream(string pipeName)
    {
        return NamedPipeServerStreamAcl.Create(
            pipeName,
            PipeDirection.InOut,
            maxNumberOfServerInstances: MaxConcurrentClients,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous,
            inBufferSize: 0,
            outBufferSize: 0,
            CreateDefaultPipeSecurity(),
            HandleInheritability.None);
    }

    [SupportedOSPlatform("windows")]
    internal static PipeSecurity CreateDefaultPipeSecurity()
    {
        var security = new PipeSecurity();
        var currentUser = WindowsIdentity.GetCurrent().User;
        if (currentUser is not null)
        {
            AddAllowRule(security, currentUser, PipeAccessRights.FullControl);
        }

        AddAllowRule(security, WellKnownSidType.LocalSystemSid, PipeAccessRights.FullControl);
        AddAllowRule(security, WellKnownSidType.BuiltinAdministratorsSid, PipeAccessRights.FullControl);
        AddAllowRule(security, WellKnownSidType.AuthenticatedUserSid, PipeAccessRights.ReadWrite);
        AddAllowRule(security, WellKnownSidType.WinBuiltinAnyPackageSid, PipeAccessRights.ReadWrite);
        return security;
    }

    [SupportedOSPlatform("windows")]
    private static void AddAllowRule(PipeSecurity security, WellKnownSidType sidType, PipeAccessRights rights)
    {
        var sid = new SecurityIdentifier(sidType, null);
        AddAllowRule(security, sid, rights);
    }

    [SupportedOSPlatform("windows")]
    private static void AddAllowRule(PipeSecurity security, SecurityIdentifier sid, PipeAccessRights rights)
    {
        security.AddAccessRule(new PipeAccessRule(sid, rights, AccessControlType.Allow));
    }

    private async Task HandleConnectionAsync(NamedPipeServerStream pipe, CancellationToken cancellationToken)
    {
        try
        {
            using var reader = new StreamReader(pipe, leaveOpen: true);
            await using var writer = new StreamWriter(pipe, leaveOpen: true) { AutoFlush = true };
            var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            var response = line is null
                ? FluxVaultIpcResponse.Failure("Empty IPC request.")
                : await DeserializeAndHandleSafeAsync(line, cancellationToken).ConfigureAwait(false);
            await writer.WriteLineAsync(FluxVaultIpcSerializer.SerializeResponse(response)).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (IOException)
        {
        }
        finally
        {
            await pipe.DisposeAsync().ConfigureAwait(false);
        }
    }

    private async Task<FluxVaultIpcResponse> DeserializeAndHandleSafeAsync(string line, CancellationToken cancellationToken)
    {
        try
        {
            return await HandleSafeAsync(FluxVaultIpcSerializer.DeserializeRequest(line), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException)
        {
            return FluxVaultIpcResponse.Failure(ex.Message);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger?.LogWarning(ex, "Failed to process FluxVault IPC request payload.");
            return FluxVaultIpcResponse.Failure(ex.Message);
        }
    }

    private async Task<FluxVaultIpcResponse> HandleSafeAsync(FluxVaultIpcRequest request, CancellationToken cancellationToken)
    {
        try
        {
            return await handler.HandleAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException)
        {
            return FluxVaultIpcResponse.Failure(ex.Message);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger?.LogError(ex, "Unexpected FluxVault IPC handler exception for {Command}.", request.Command);
            return FluxVaultIpcResponse.Failure(ex.Message);
        }
    }

}
