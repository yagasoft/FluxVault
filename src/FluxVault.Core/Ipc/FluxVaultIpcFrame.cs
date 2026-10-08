using System.Buffers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FluxVault.Core.Ipc;

internal static class FluxVaultIpcFrame
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    internal static async Task<T> ReadAsync<T>(Stream stream, int maximumBytes, int maximumDepth,
        TimeSpan timeout, CancellationToken cancellationToken, bool waitForResponse = false)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var buffer = ArrayPool<byte>.Shared.Rent(4096);
        try
        {
            using var payload = new MemoryStream();
            if (waitForResponse)
            {
                // Only the frame is timed. Long operations retain their cancellation/status contract.
                var first = await stream.ReadAsync(buffer.AsMemory(0, 1), cancellationToken).ConfigureAwait(false);
                if (first == 0) throw new IOException("The service closed the connection without a response.");
                if (buffer[0] == '\n') throw new InvalidDataException("Empty IPC frame.");
                payload.WriteByte(buffer[0]);
            }
            deadline.CancelAfter(timeout);
            while (true)
            {
                var count = await stream.ReadAsync(buffer.AsMemory(0, Math.Min(4096,
                    maximumBytes - checked((int)payload.Length) + 1)), deadline.Token).ConfigureAwait(false);
                if (count == 0) throw new InvalidDataException("Incomplete IPC frame.");
                var end = Array.IndexOf(buffer, (byte)'\n', 0, count);
                var length = end < 0 ? count : end;
                if (payload.Length + length > maximumBytes) throw new InvalidDataException("IPC frame exceeds the supported size.");
                payload.Write(buffer, 0, length);
                if (end < 0) continue;
                var text = StrictUtf8.GetString(payload.GetBuffer(), 0, checked((int)payload.Length));
                return JsonSerializer.Deserialize<T>(text, Options(maximumDepth)) ?? throw new InvalidDataException("Empty IPC payload.");
            }
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        { throw new IOException("IPC frame read timed out.", exception); }
        finally { ArrayPool<byte>.Shared.Return(buffer); }
    }

    internal static async Task WriteAsync<T>(Stream stream, T value, int maximumBytes, int maximumDepth,
        TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var payload = new BoundedBuffer(maximumBytes);
        JsonSerializer.Serialize(payload, value, Options(maximumDepth));
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        try
        {
            await stream.WriteAsync(payload.GetBuffer().AsMemory(0, checked((int)payload.Length)), deadline.Token).ConfigureAwait(false);
            await stream.WriteAsync(new byte[] { (byte)'\n' }, deadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        { throw new IOException("IPC frame write timed out.", exception); }
    }

    private static JsonSerializerOptions Options(int maximumDepth) => new(JsonSerializerDefaults.Web)
    { MaxDepth = maximumDepth, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };

    private sealed class BoundedBuffer(int maximumBytes) : MemoryStream
    {
        public override void Write(byte[] buffer, int offset, int count)
        {
            Check(count);
            base.Write(buffer, offset, count);
        }
        public override void Write(ReadOnlySpan<byte> buffer)
        {
            Check(buffer.Length);
            base.Write(buffer);
        }
        private void Check(int count)
        {
            if (Length + count > maximumBytes) throw new InvalidDataException("IPC frame exceeds the supported size; use a paged request.");
            if (Capacity < Length + count) Capacity = checked((int)Math.Min(maximumBytes, Math.Max(Length + count, Capacity * 2L)));
        }
    }
}
