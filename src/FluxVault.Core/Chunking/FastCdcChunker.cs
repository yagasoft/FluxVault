using System.Numerics;

namespace FluxVault.Core.Chunking;

public sealed class FastCdcChunker
{
    private static readonly ulong[] Gear = CreateGearTable();

    private readonly ChunkingOptions options;
    private readonly ulong cutMask;

    public FastCdcChunker(ChunkingOptions options)
    {
        options.Validate();
        this.options = options;
        cutMask = (ulong)BitOperations.RoundUpToPowerOf2((uint)options.AverageSize) - 1UL;
    }

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
            if (remaining <= options.MaximumSize)
            {
                chunks.Add(new ContentChunk(offset, remaining));
                return chunks;
            }

            var hash = 0UL;
            var length = 0;
            var limit = Math.Min(options.MaximumSize, remaining);

            while (length < limit)
            {
                hash = (hash << 1) + Gear[span[offset + length]];
                length++;

                if (length >= options.MinimumSize && (hash & cutMask) == 0)
                {
                    break;
                }
            }

            chunks.Add(new ContentChunk(offset, length));
            offset += length;
        }

        return chunks;
    }

    private static ulong[] CreateGearTable()
    {
        var table = new ulong[256];
        var state = 0x9E3779B97F4A7C15UL;

        for (var i = 0; i < table.Length; i++)
        {
            state ^= state >> 12;
            state ^= state << 25;
            state ^= state >> 27;
            table[i] = state * 0x2545F4914F6CDD1DUL;
        }

        return table;
    }
}
