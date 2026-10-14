using System.Net;
using System.Net.Sockets;
using System.IO;

namespace FluxVault.TestHost;

// Fixture-only availability fault. Its destination is the already identity-verified
// private PostgreSQL endpoint; it cannot select the normal database port.
internal sealed class OwnedLoopbackRelay : IAsyncDisposable
{
    private readonly IPAddress destination;
    private readonly int destinationPort;
    private TcpListener? listener;
    private CancellationTokenSource? stopping;
    private Task? accepting;
    private readonly List<Task> connections = [];
    internal int Port { get; private set; }

    internal OwnedLoopbackRelay(string host, int port)
    {
        if (!IPAddress.TryParse(host, out var address) || !IPAddress.IsLoopback(address) || port is < 1024 or 5432)
            throw new ArgumentException("Only a verified private loopback endpoint is allowed.");
        destination = address; destinationPort = port;
        Start();
    }

    internal void Start()
    {
        if (listener is not null) throw new InvalidOperationException("Relay is already listening.");
        listener = new TcpListener(destination, Port);
        listener.Start(); Port = ((IPEndPoint)listener.LocalEndpoint).Port;
        stopping = new CancellationTokenSource();
        accepting = AcceptAsync(listener, stopping.Token);
    }

    private async Task AcceptAsync(TcpListener ownedListener, CancellationToken ct)
    {
        try
        {
            while (true)
            {
                var incoming = await ownedListener.AcceptTcpClientAsync(ct);
                connections.Add(ForwardAsync(incoming, ct));
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (SocketException) when (ct.IsCancellationRequested) { }
    }

    private async Task ForwardAsync(TcpClient incoming, CancellationToken ct)
    {
        using (incoming)
        using (var outgoing = new TcpClient(destination.AddressFamily))
        {
            try
            {
                await outgoing.ConnectAsync(destination, destinationPort, ct);
                var input = incoming.GetStream(); var output = outgoing.GetStream();
                using var pair = CancellationTokenSource.CreateLinkedTokenSource(ct);
                var send = input.CopyToAsync(output, pair.Token);
                var receive = output.CopyToAsync(input, pair.Token);
                await Task.WhenAny(send, receive);
                await pair.CancelAsync();
                try { await Task.WhenAll(send, receive); }
                catch (OperationCanceledException) when (pair.IsCancellationRequested) { }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
            catch (IOException) { /* Closing one PostgreSQL connection retires only this relay pair. */ }
        }
    }

    internal async Task StopAsync()
    {
        if (listener is null) return;
        await stopping!.CancelAsync(); listener.Stop();
        try
        {
            await accepting!;
            await Task.WhenAll(connections);
        }
        finally { listener = null; connections.Clear(); stopping.Dispose(); stopping = null; }
    }

    public async ValueTask DisposeAsync() => await StopAsync();
}
