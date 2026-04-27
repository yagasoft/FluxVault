using ZstdSharp;

namespace FluxVault.Core.Content;

public sealed class ZstdChunkCodec
{
    public byte[] Compress(ReadOnlySpan<byte> payload, int level)
    {
        using var compressor = new Compressor(level);
        return compressor.Wrap(payload).ToArray();
    }

    public byte[] Decompress(ReadOnlySpan<byte> payload, int originalLength)
    {
        using var decompressor = new Decompressor();
        return decompressor.Unwrap(payload, originalLength).ToArray();
    }
}
