using System.IO.Pipes;
using FluxVault.Abstractions.Ipc;

namespace FluxVault.Core.Ipc;

public sealed class NamedPipeFluxVaultServer(IFluxVaultRequestHandler handler, string pipeName = NamedPipeFluxVaultServer.DefaultPipeName)
{
    public const string DefaultPipeName = "FluxVault.Service";

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            await using var pipe = new NamedPipeServerStream(
                pipeName,
                PipeDirection.InOut,
                maxNumberOfServerInstances: 1,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous);

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
