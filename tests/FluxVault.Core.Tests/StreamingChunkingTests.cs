using System.Text;
using FluxVault.Core.Chunking;

namespace FluxVault.Core.Tests;

public sealed class StreamingChunkingTests
{
    [Fact]
    public async Task Streaming_chunker_matches_in_memory_boundaries_with_small_read_buffer()
    {
        var data = Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("streaming-content-boundary-", 900)));
        var options = new ChunkingOptions(MinimumSize: 128, AverageSize: 256, MaximumSize: 512);
        var inMemory = new FastCdcChunker(options).Chunk(data).ToArray();
        var streaming = new StreamingFastCdcChunker(options, readBufferSize: 137);

        var streamed = new List<StreamedContentChunk>();
        await foreach (var chunk in streaming.ChunkAsync(new MemoryStream(data)))
        {
            streamed.Add(chunk);
        }

        Assert.Equal(inMemory.Select(chunk => (long)chunk.Offset).ToArray(), streamed.Select(chunk => chunk.Offset).ToArray());
        Assert.Equal(inMemory.Select(chunk => chunk.Length).ToArray(), streamed.Select(chunk => chunk.Payload.Length).ToArray());
        Assert.Equal(data, streamed.SelectMany(chunk => chunk.Payload).ToArray());
    }
}
