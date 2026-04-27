namespace FluxVault.Core.Chunking;

public sealed class FastCdcChunker
{
    public FastCdcChunker(ChunkingOptions options)
    {
        options.Validate();
        Options = options;
    }

    public ChunkingOptions Options { get; }

    public IReadOnlyList<ContentChunk> Chunk(ReadOnlyMemory<byte> content)
    {
        var chunks = new List<ContentChunk>();
        if (content.Length == 0)
        {
            return chunks;
        }

        var span = content.Span;
        var offset = 0;

        while (offset < span.Length)
        {
            var remaining = span.Length - offset;
            if (remaining <= Options.MaximumSize)
            {
                chunks.Add(new ContentChunk(offset, remaining));
                return chunks;
            }

            var length = FastCdcBoundary.FindCut(span[offset..], Options);

            chunks.Add(new ContentChunk(offset, length));
            offset += length;
        }

        return chunks;
    }
}
