using System.IO.Compression;
using FluxVault.Abstractions.Storage;
using K4os.Compression.LZ4;
using K4os.Compression.LZ4.Streams;
using SharpCompress.Compressors.LZMA;
using ZstdSharp;
using SharpCompressionMode = SharpCompress.Compressors.CompressionMode;

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

    public byte[] Compress(ReadOnlySpan<byte> payload, ChunkEncoding encoding, int level)
    {
        return encoding switch
        {
            ChunkEncoding.Zstd => Compress(payload, level),
            ChunkEncoding.Lz4 => CompressLz4(payload),
            ChunkEncoding.Brotli => CompressBrotli(payload, level),
            ChunkEncoding.Lzma => CompressLzma(payload),
            _ => payload.ToArray()
        };
    }

    public byte[] Decompress(ReadOnlySpan<byte> payload, int originalLength, ChunkEncoding encoding)
    {
        return encoding switch
        {
            ChunkEncoding.Zstd => Decompress(payload, originalLength),
            ChunkEncoding.Lz4 => DecompressLz4(payload, originalLength),
            ChunkEncoding.Brotli => DecompressBrotli(payload),
            ChunkEncoding.Lzma => DecompressLzma(payload),
            _ => payload.ToArray()
        };
    }

    private static byte[] CompressLz4(ReadOnlySpan<byte> payload)
    {
        using var output = new MemoryStream();
        using (var lz4 = LZ4Stream.Encode(output, LZ4Level.L00_FAST, 0, leaveOpen: true))
        {
            lz4.Write(payload);
        }

        return output.ToArray();
    }

    private static byte[] DecompressLz4(ReadOnlySpan<byte> payload, int originalLength)
    {
        using var input = new MemoryStream(payload.ToArray());
        using var lz4 = LZ4Stream.Decode(input, extraMemory: originalLength, leaveOpen: false, interactive: false);
        using var output = new MemoryStream(originalLength);
        lz4.CopyTo(output);
        return output.ToArray();
    }

    private static byte[] CompressBrotli(ReadOnlySpan<byte> payload, int level)
    {
        using var output = new MemoryStream();
        using (var brotli = new BrotliStream(output, new BrotliCompressionOptions { Quality = Math.Clamp(level + 2, 1, 11) }, leaveOpen: true))
        {
            brotli.Write(payload);
        }

        return output.ToArray();
    }

    private static byte[] DecompressBrotli(ReadOnlySpan<byte> payload)
    {
        using var input = new MemoryStream(payload.ToArray());
        using var brotli = new BrotliStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        brotli.CopyTo(output);
        return output.ToArray();
    }

    private static byte[] CompressLzma(ReadOnlySpan<byte> payload)
    {
        using var output = new MemoryStream();
        using (var lzma = new LZipStream(output, SharpCompressionMode.Compress, leaveOpen: true))
        {
            lzma.Write(payload);
        }

        return output.ToArray();
    }

    private static byte[] DecompressLzma(ReadOnlySpan<byte> payload)
    {
        using var input = new MemoryStream(payload.ToArray());
        using var lzma = new LZipStream(input, SharpCompressionMode.Decompress, leaveOpen: false);
        using var output = new MemoryStream();
        lzma.CopyTo(output);
        return output.ToArray();
    }
}
