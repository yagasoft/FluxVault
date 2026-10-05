using System.Text;
using System.Text.Json;
using FluxVault.Abstractions.Ipc;

namespace FluxVault.Core.Ipc;

public sealed class NamedPipeFluxVaultClient : IFluxVaultServiceClient
{
    private readonly IFluxVaultPipeClientFactory pipeFactory;
    private readonly FluxVaultIpcLimits limits;

    public NamedPipeFluxVaultClient(IFluxVaultPipeClientFactory pipeFactory, FluxVaultIpcLimits? limits = null)
    {
        this.pipeFactory = pipeFactory ?? throw new ArgumentNullException(nameof(pipeFactory));
        this.limits = limits ?? new();
        this.limits.Validate();
    }

    public async Task<FluxVaultIpcResponse> SendAsync(FluxVaultIpcRequest request, CancellationToken cancellationToken = default)
    {
        await using var pipe = await pipeFactory.ConnectAsync(cancellationToken).ConfigureAwait(false);
        await FluxVaultIpcFrame.WriteAsync(pipe, request, limits.MaximumRequestBytes, limits.MaximumJsonDepth,
            limits.FrameWriteTimeout, cancellationToken).ConfigureAwait(false);
        FluxVaultIpcResponse response;
        try
        {
            response = await FluxVaultIpcFrame.ReadAsync<FluxVaultIpcResponse>(pipe, limits.MaximumResponseBytes,
                limits.MaximumJsonDepth, limits.FrameReadTimeout, cancellationToken, waitForResponse: true).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is InvalidDataException or JsonException or DecoderFallbackException)
        {
            // The request was sent, but an invalid response cannot establish its outcome.
            // Use the same transport uncertainty contract as a disconnected service.
            throw new IOException("The service response was incomplete or invalid. Check the operation outcome before retrying.", exception);
        }
        // DisconnectNamedPipe discards unread bytes. A bounded receipt lets the served anchor be reused safely.
        using var receiptDeadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        receiptDeadline.CancelAfter(limits.FrameWriteTimeout);
        try { await pipe.WriteAsync(new byte[] { 6 }, receiptDeadline.Token).ConfigureAwait(false); }
        catch (Exception exception) when (exception is IOException or OperationCanceledException or ObjectDisposedException)
        {
            // A complete response is authoritative even if this housekeeping receipt cannot be delivered.
        }
        return response;
    }
}
