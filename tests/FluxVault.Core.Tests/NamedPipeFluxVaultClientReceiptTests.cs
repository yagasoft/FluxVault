using System.Text;
using FluxVault.Abstractions.Ipc;
using FluxVault.Core.Ipc;

namespace FluxVault.Core.Tests;

public sealed class NamedPipeFluxVaultClientReceiptTests
{
    [Fact]
    public async Task Caller_cancellation_while_waiting_for_response_is_preserved_and_closes_transport()
    {
        using var transport = new ResponseThenFailedReceipt("read-cancelled");
        var client = new NamedPipeFluxVaultClient(new ConnectedFixtureTransport(transport));
        using var lifetime = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.SendAsync(FluxVaultIpcRequest.GetStatus(), lifetime.Token));
        Assert.Equal(lifetime.Token, exception.CancellationToken);
        Assert.True(transport.Disposed);
        Assert.False(transport.ReceiptAttempted);
    }

    [Theory]
    [InlineData("truncated")]
    [InlineData("malformed")]
    [InlineData("utf8")]
    [InlineData("empty")]
    [InlineData("null")]
    [InlineData("oversized")]
    [InlineData("depth")]
    public async Task Invalid_response_is_transport_uncertainty_and_sends_no_receipt(string fault)
    {
        var bytes = fault switch
        {
            "truncated" => Encoding.UTF8.GetBytes("{\"success\":true"),
            "malformed" => Encoding.UTF8.GetBytes("{]\n"),
            "utf8" => new byte[] { 255, 10 },
            "empty" => new byte[] { 10 },
            "null" => Encoding.UTF8.GetBytes("null\n"),
            "oversized" => Encoding.UTF8.GetBytes(new string(' ', 1025) + "\n"),
            _ => Encoding.UTF8.GetBytes("{\"status\":{\"configuration\":{\"watchedFolders\":[{\"includePatterns\":[\"*.docx\"]}]}}}\n")
        };
        using var transport = new ResponseThenFailedReceipt("closed", bytes);
        var client = new NamedPipeFluxVaultClient(new ConnectedFixtureTransport(transport),
            new FluxVaultIpcLimits { MaximumResponseBytes = 1024, MaximumJsonDepth = 4 });
        using var lifetime = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var exception = await Assert.ThrowsAsync<IOException>(() => client.SendAsync(FluxVaultIpcRequest.GetStatus(), lifetime.Token));
        Assert.NotNull(exception.InnerException);
        Assert.False(transport.ReceiptAttempted);
        Assert.True(transport.Disposed);
        Assert.NotEmpty(transport.Request.ToArray());
    }

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

    private sealed class ResponseThenFailedReceipt(string failure, byte[]? bytes = null) : Stream
    {
        private readonly MemoryStream response = new(bytes ?? Encoding.UTF8.GetBytes(FluxVaultIpcSerializer.SerializeResponse(FluxVaultIpcResponse.Ok()) + "\n"));
        public MemoryStream Request { get; } = new();
        public bool ReceiptAttempted { get; private set; }
        public bool Disposed { get; private set; }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (failure == "read-cancelled") await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return await response.ReadAsync(buffer, cancellationToken);
        }
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
