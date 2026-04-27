using System.Text;
using FluxVault.Abstractions.Storage;
using FluxVault.Core.Content;

namespace FluxVault.Core.Tests;

public sealed class ContentCodecTests
{
    [Fact]
    public void Blake3_hasher_returns_known_digest_for_abc()
    {
        var hasher = new Blake3ContentHasher();

        var digest = hasher.Hash(Encoding.ASCII.GetBytes("abc"));

        Assert.Equal("6437b3ac38465133ffb63b75273a8db548c558465d79db03fd359c6cd5bd9d85", digest);
    }

    [Fact]
    public void Zstd_compressor_round_trips_payload()
    {
        var codec = new ZstdChunkCodec();
        var payload = Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("compressible FluxVault payload ", 200)));

        var compressed = codec.Compress(payload, level: 3);
        var restored = codec.Decompress(compressed, payload.Length);

        Assert.Equal(payload, restored);
        Assert.True(compressed.Length < payload.Length);
    }

    [Theory]
    [InlineData(ChunkEncoding.Zstd)]
    [InlineData(ChunkEncoding.Lz4)]
    [InlineData(ChunkEncoding.Brotli)]
    [InlineData(ChunkEncoding.Lzma)]
    public void Expanded_codecs_round_trip_payload(ChunkEncoding encoding)
    {
        var codec = new ZstdChunkCodec();
        var payload = Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("compressible FluxVault payload ", 200)));

        var compressed = codec.Compress(payload, encoding, level: 3);
        var restored = codec.Decompress(compressed, payload.Length, encoding);

        Assert.Equal(payload, restored);
    }
}
