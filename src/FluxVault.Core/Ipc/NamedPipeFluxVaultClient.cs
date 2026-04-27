using System.IO.Pipes;
using FluxVault.Abstractions.Ipc;

namespace FluxVault.Core.Ipc;

public sealed class NamedPipeFluxVaultClient(string pipeName = NamedPipeFluxVaultServer.DefaultPipeName)
{
    public async Task<FluxVaultIpcResponse> SendAsync(FluxVaultIpcRequest request, CancellationToken cancellationToken = default)
    {
        using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await pipe.ConnectAsync(2000, cancellationToken).ConfigureAwait(false);
        await using var writer = new StreamWriter(pipe, leaveOpen: true) { AutoFlush = true };
        using var reader = new StreamReader(pipe, leaveOpen: true);

        await writer.WriteLineAsync(FluxVaultIpcSerializer.SerializeRequest(request)).ConfigureAwait(false);
        var response = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false)
            ?? throw new IOException("FluxVault service closed the IPC connection without a response.");
        return FluxVaultIpcSerializer.DeserializeResponse(response);
    }
}
