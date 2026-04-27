using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Running;
using FluxVault.Core.Chunking;
using FluxVault.Core.Content;

BenchmarkRunner.Run<ChunkingBenchmarks>();

[MemoryDiagnoser]
public class ChunkingBenchmarks
{
    private readonly FastCdcChunker chunker = new(new ChunkingOptions(64 * 1024, 256 * 1024, 1024 * 1024));
    private readonly Blake3ContentHasher hasher = new();
    private byte[] payload = [];

    [Params(1, 64)]
    public int SizeMiB { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        payload = new byte[SizeMiB * 1024 * 1024];
        var random = new Random(42);
        random.NextBytes(payload);
    }

    [Benchmark]
    public int ChunkAndHash()
    {
        var count = 0;
        foreach (var chunk in chunker.Chunk(payload))
        {
            _ = hasher.Hash(payload.AsSpan(chunk.Offset, chunk.Length));
            count++;
        }

        return count;
    }
}
