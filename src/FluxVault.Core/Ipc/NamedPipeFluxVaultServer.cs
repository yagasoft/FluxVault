using System.IO.Pipes;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using FluxVault.Abstractions.Ipc;

namespace FluxVault.Core.Ipc;

public sealed class NamedPipeFluxVaultServer(IFluxVaultRequestHandler handler, string pipeName = NamedPipeFluxVaultServer.DefaultPipeName)
{
    public const string DefaultPipeName = "FluxVault.Service";

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            await using var pipe = CreateServerStreamForCurrentPlatform(pipeName);

            try
            {
                await pipe.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
                using var reader = new StreamReader(pipe, leaveOpen: true);
                await using var writer = new StreamWriter(pipe, leaveOpen: true) { AutoFlush = true };
                var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
                var response = line is null
                    ? FluxVaultIpcResponse.Failure("Empty IPC request.")
                    : await HandleSafeAsync(FluxVaultIpcSerializer.DeserializeRequest(line), cancellationToken).ConfigureAwait(false);
                await writer.WriteLineAsync(FluxVaultIpcSerializer.SerializeResponse(response)).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
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
            maxNumberOfServerInstances: 1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous);
    }

    [SupportedOSPlatform("windows")]
    internal static NamedPipeServerStream CreateServerStream(string pipeName)
    {
        return NamedPipeServerStreamAcl.Create(
            pipeName,
            PipeDirection.InOut,
            maxNumberOfServerInstances: 1,
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
        security.AddAccessRule(new PipeAccessRule(sid, rights, AccessControlType.Allow));
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
    }

}
