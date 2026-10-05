using System.Diagnostics;
using System.IO.Pipes;
using System.Text.Json;
using FluxVault.Abstractions.Ipc;
using FluxVault.Core.Diagnostics;
using FluxVault.Core.Security;
using Microsoft.Extensions.Logging;

namespace FluxVault.Core.Ipc;

public sealed class NamedPipeFluxVaultServer(
    IAuthenticatedFluxVaultRequestHandler handler,
    IFluxVaultPipeServerFactory serverStreamFactory,
    IFluxVaultCallerContextProvider callerContextProvider,
    FluxVaultIpcLimits? limits = null,
    ILogger<NamedPipeFluxVaultServer>? logger = null,
    TelemetryCollector? telemetryCollector = null,
    Func<bool>? stopAfterConnection = null)
{
    public const string DefaultPipeName = "FluxVault.Service";
    private readonly FluxVaultIpcLimits limits = Validate(limits ?? new(), stopAfterConnection);
    private readonly HashSet<Task> accepted = [];
    private readonly object acceptedGate = new();
    private int started;

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        if (Interlocked.Exchange(ref started, 1) != 0) throw new InvalidOperationException("A pipe server has one lifetime.");
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using var capacity = new SemaphoreSlim(limits.MaximumConcurrentRequests);
        // Acquire once before accepting. A collision is fatal; there is no attach/retry fallback.
        await using var anchor = serverStreamFactory.CreateFirstListener();
        var listeners = new[] { ServeAnchorAsync(anchor, capacity, stop.Token) }
            .Concat(Enumerable.Range(1, limits.PendingListeners - 1).Select(_ => AcceptLoopAsync(capacity, stop.Token))).ToArray();
        try
        {
            await Task.WhenAny(listeners).ConfigureAwait(false);
            await stop.CancelAsync().ConfigureAwait(false);
            await Task.WhenAll(listeners).ConfigureAwait(false);
        }
        finally
        {
            await stop.CancelAsync().ConfigureAwait(false);
            try { await Task.WhenAll(listeners).ConfigureAwait(false); }
            finally
            {
                Task[] pending;
                lock (acceptedGate) pending = accepted.ToArray();
                await Task.WhenAll(pending).ConfigureAwait(false);
            }
        }
    }

    private async Task ServeAnchorAsync(NamedPipeServerStream anchor, SemaphoreSlim capacity, CancellationToken cancellationToken)
    {
        try
        {
            while (true)
            {
                await capacity.WaitAsync(cancellationToken).ConfigureAwait(false);
                var connected = false;
                try
                {
                    await anchor.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
                    connected = true;
                    await HandleConnectionAsync(anchor, cancellationToken).ConfigureAwait(false);
                }
                finally
                {
                    // Handler completion alone is insufficient; HandleConnection includes response delivery.
                    // EOF can make IsConnected false while the native instance still needs resetting.
                    if (connected) anchor.Disconnect();
                    capacity.Release();
                }
                // Once-only setup may retire only after response delivery/receipt and
                // native caller cleanup. Serial admission prevents another connection
                // from observing a terminal handler while its response is still in flight.
                if (stopAfterConnection?.Invoke() == true) return;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
    }

    private async Task AcceptLoopAsync(SemaphoreSlim capacity, CancellationToken cancellationToken)
    {
        try
        {
            while (true)
            {
                await capacity.WaitAsync(cancellationToken).ConfigureAwait(false);
                NamedPipeServerStream? pipe = null;
                var handedOff = false;
                try
                {
                    pipe = serverStreamFactory.CreateAdditionalListener();
                    await pipe.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
                    var connected = pipe;
                    pipe = null;
                    var work = Task.Run(async () =>
                    {
                        try { await HandleConnectionAsync(connected, cancellationToken).ConfigureAwait(false); }
                        finally { await connected.DisposeAsync().ConfigureAwait(false); capacity.Release(); }
                    }, CancellationToken.None);
                    handedOff = true;
                    lock (acceptedGate) accepted.Add(work);
                    _ = work.ContinueWith(completed =>
                    {
                        if (completed.IsFaulted) logger?.LogError(completed.Exception, "FluxVault IPC connection terminated unexpectedly.");
                        lock (acceptedGate) accepted.Remove(completed);
                    },
                        CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                }
                finally
                {
                    if (pipe is not null) await pipe.DisposeAsync().ConfigureAwait(false);
                    if (!handedOff) capacity.Release();
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
    }

    private async Task HandleConnectionAsync(NamedPipeServerStream pipe, CancellationToken cancellationToken)
    {
        FluxVaultCallerContext? caller = null;
        try
        {
            FluxVaultIpcResponse response;
            try
            {
                var request = await FluxVaultIpcFrame.ReadAsync<FluxVaultIpcRequest>(pipe, limits.MaximumRequestBytes,
                    limits.MaximumJsonDepth, limits.FrameReadTimeout, cancellationToken).ConfigureAwait(false);
                caller = callerContextProvider.Capture(pipe);
                var stopwatch = Stopwatch.StartNew();
                telemetryCollector?.RecordIpcRequestStarted(request.Command);
                response = await handler.HandleAsync(caller, request, cancellationToken).ConfigureAwait(false);
                telemetryCollector?.RecordIpcRequestCompleted(request.Command, stopwatch.Elapsed, response.Success);
            }
            catch (UnauthorizedAccessException exception)
            {
                logger?.LogWarning(exception, "FluxVault IPC caller authentication or authorisation failed.");
                response = FluxVaultIpcResponse.Failure("Access to the requested vault is denied.");
            }
            catch (Exception exception) when (exception is InvalidDataException or JsonException or ArgumentException or IOException)
            {
                logger?.LogWarning(exception, "FluxVault IPC request was refused.");
                response = FluxVaultIpcResponse.Failure("The service could not accept this request.");
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                logger?.LogError(exception, "FluxVault IPC operation failed.");
                response = FluxVaultIpcResponse.Failure("The service could not complete this request.");
            }
            await FluxVaultIpcFrame.WriteAsync(pipe, response, limits.MaximumResponseBytes, limits.MaximumJsonDepth,
                limits.FrameWriteTimeout, cancellationToken).ConfigureAwait(false);
            using var receiptDeadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            receiptDeadline.CancelAfter(limits.FrameWriteTimeout);
            var receipt = new byte[1];
            var received = await pipe.ReadAsync(receipt, receiptDeadline.Token).ConfigureAwait(false);
            if (received != 1 || receipt[0] != 6)
                logger?.LogTrace("FluxVault IPC response receipt was not confirmed.");
        }
        catch (OperationCanceledException) { }
        catch (IOException) { }
        catch (InvalidDataException exception) { logger?.LogWarning(exception, "FluxVault IPC response exceeds the frame limit."); }
        finally { caller?.Dispose(); }
    }

    private static FluxVaultIpcLimits Validate(FluxVaultIpcLimits value, Func<bool>? stopAfterConnection)
    {
        value.Validate();
        if (stopAfterConnection is not null && (value.PendingListeners != 1 || value.MaximumConcurrentRequests != 1))
            throw new ArgumentException("A terminal pipe lifetime requires one listener and one concurrent request.", nameof(stopAfterConnection));
        return value;
    }
}
