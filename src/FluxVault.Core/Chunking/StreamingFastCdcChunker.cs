namespace FluxVault.Core.Chunking;

public sealed class StreamingFastCdcChunker
{
    private readonly ChunkingOptions options;
    private readonly int readBufferSize;

    public StreamingFastCdcChunker(ChunkingOptions options, int readBufferSize = 1024 * 1024)
    {
        options.Validate();
        if (readBufferSize <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(readBufferSize), "Read buffer size must be positive.");
        }

        this.options = options;
        this.readBufferSize = readBufferSize;
    }

    public async IAsyncEnumerable<StreamedContentChunk> ChunkAsync(
        Stream stream,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);

        var offset = 0L;
        var carry = Array.Empty<byte>();

        while (true)
        {
            var (window, isEndOfStream) = await FillWindowAsync(stream, carry, cancellationToken).ConfigureAwait(false);
            if (window.Length == 0)
            {
                yield break;
            }

            if (isEndOfStream && window.Length <= options.MaximumSize)
            {
                yield return new StreamedContentChunk(offset, window);
                yield break;
            }

            var cut = FastCdcBoundary.FindCut(window, options);
            var payload = new byte[cut];
            Buffer.BlockCopy(window, 0, payload, 0, cut);
            yield return new StreamedContentChunk(offset, payload);

            offset += cut;
            carry = window.Length == cut ? [] : window[cut..];
        }
    }

    private async Task<(byte[] Window, bool IsEndOfStream)> FillWindowAsync(
        Stream stream,
        byte[] carry,
        CancellationToken cancellationToken)
    {
        using var window = new MemoryStream(capacity: options.MaximumSize + 1);
        if (carry.Length > 0)
        {
            await window.WriteAsync(carry, cancellationToken).ConfigureAwait(false);
        }

        var readBuffer = new byte[Math.Min(readBufferSize, Math.Max(1, options.MaximumSize + 1))];
        while (window.Length < options.MaximumSize + 1)
        {
            var requested = (int)Math.Min(readBuffer.Length, options.MaximumSize + 1 - window.Length);
            var read = await stream.ReadAsync(readBuffer.AsMemory(0, requested), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                return (window.ToArray(), true);
            }

            await window.WriteAsync(readBuffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
        }

        return (window.ToArray(), false);
    }
}
