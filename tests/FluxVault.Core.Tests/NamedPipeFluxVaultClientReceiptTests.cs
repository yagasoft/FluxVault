using System.Text;
using FluxVault.Abstractions.Ipc;
using FluxVault.Core.Ipc;

namespace FluxVault.Core.Tests;

public sealed class NamedPipeFluxVaultClientReceiptTests
{
    [Theory]
    [InlineData("closed")]
    [InlineData("cancelled")]
    [InlineData("stalled")]
    public async Task Confirmed_response_survives_a_failed_receipt(string failure)
    {
        using var transport = new ResponseThenFailedReceipt(failure);
        var limits = new FluxVaultIpcLimits { FrameWriteTimeout = TimeSpan.FromMilliseconds(50) };
        var client = new NamedPipeFluxVaultClient(new ConnectedFixtureTransport(transport), limits);
        using var lifetime = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var response = await client.SendAsync(FluxVaultIpcRequest.GetStatus(), lifetime.Token);
        Assert.True(response.Success);
        Assert.True(transport.ReceiptAttempted);
        Assert.True(transport.Disposed);
        Assert.Contains("\"command\":0", Encoding.UTF8.GetString(transport.Request.ToArray()), StringComparison.Ordinal);
    }

    private sealed class ConnectedFixtureTransport(Stream stream) : IFluxVaultPipeClientFactory
    {
        public Task<Stream> ConnectAsync(CancellationToken cancellationToken) => Task.FromResult(stream);
    }

    private sealed class ResponseThenFailedReceipt(string failure) : Stream
    {
        private readonly MemoryStream response = new(Encoding.UTF8.GetBytes(FluxVaultIpcSerializer.SerializeResponse(FluxVaultIpcResponse.Ok()) + "\n"));
        public MemoryStream Request { get; } = new();
        public bool ReceiptAttempted { get; private set; }
        public bool Disposed { get; private set; }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => response.ReadAsync(buffer, cancellationToken);
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (buffer.Length == 1 && buffer.Span[0] == 6)
            {
                ReceiptAttempted = true;
                if (failure == "closed") throw new IOException("Peer closed after delivering the response.");
                if (failure == "cancelled") throw new OperationCanceledException("Receipt cancelled after response delivery.");
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            else await Request.WriteAsync(buffer, cancellationToken);
        }
        protected override void Dispose(bool disposing)
        {
            Disposed = true;
            if (disposing) response.Dispose();
            base.Dispose(disposing);
        }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
