using System.Text;
using FluxVault.Core.Chunking;

namespace FluxVault.Core.Tests;

public sealed class ChunkingTests
{
    [Fact]
    public void Chunk_splits_stream_without_losing_bytes()
    {
        var data = Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("FluxVault backup data ", 512)));
        var chunker = new FastCdcChunker(new ChunkingOptions(MinimumSize: 128, AverageSize: 256, MaximumSize: 512));

        var chunks = chunker.Chunk(data).ToArray();

        Assert.True(chunks.Length > 1);
        Assert.Equal(data.Length, chunks.Sum(chunk => chunk.Length));
        Assert.All(chunks[..^1], chunk => Assert.InRange(chunk.Length, 128, 512));

        var restored = chunks.SelectMany(chunk => data.AsSpan(chunk.Offset, chunk.Length).ToArray()).ToArray();
        Assert.Equal(data, restored);
    }

    [Fact]
    public void Chunk_is_deterministic_for_same_input()
    {
        var data = Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("deterministic-content-", 400)));
        var chunker = new FastCdcChunker(new ChunkingOptions(MinimumSize: 96, AverageSize: 192, MaximumSize: 384));

        var first = chunker.Chunk(data).ToArray();
        var second = chunker.Chunk(data).ToArray();

        Assert.Equal(first, second);
    }
}
