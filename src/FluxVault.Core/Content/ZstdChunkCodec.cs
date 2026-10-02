using System.IO.Compression;
using System.Buffers.Binary;
using System.Numerics;
using FluxVault.Abstractions.Storage;
using FluxVault.Core.Storage.Integrity;
using K4os.Compression.LZ4;
using K4os.Compression.LZ4.Streams;
using SharpCompress.Compressors.LZMA;
using ZstdSharp;
using ZstdSharp.Unsafe;
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
        return Decompress(payload, originalLength, ChunkEncoding.Zstd);
    }

    public byte[] Compress(ReadOnlySpan<byte> payload, ChunkEncoding encoding, int level)
    {
        return encoding switch
        {
            ChunkEncoding.Zstd => Compress(payload, level),
            ChunkEncoding.Lz4 => CompressLz4(payload),
            ChunkEncoding.Brotli => CompressBrotli(payload, level),
            ChunkEncoding.Lzma => CompressLzma(payload),
            ChunkEncoding.Raw => payload.ToArray(),
            _ => throw new InvalidDataException("Unknown chunk encoding.")
        };
    }

    public byte[] Decompress(ReadOnlySpan<byte> payload, int originalLength, ChunkEncoding encoding)
    {
        return Decompress(payload, originalLength, encoding, new RepositoryIntegrityLimits());
    }

    internal byte[] Decompress(ReadOnlySpan<byte> payload, int originalLength, ChunkEncoding encoding, RepositoryIntegrityLimits limits)
    {
        limits.Validate();
        if (originalLength < 0 || originalLength > limits.MaxDecodedChunkBytes || payload.Length > limits.MaxStoredChunkBytes)
            throw new RepositoryIntegrityException(RepositoryIntegrityFailure.LimitExceeded, "Chunk decode exceeds its supported size limit.");
        try
        {
            if (encoding == ChunkEncoding.Zstd) ValidateZstdWindow(payload, originalLength, limits);
            var output = new byte[originalLength];
            switch (encoding)
            {
                case ChunkEncoding.Raw:
                    if (payload.Length != originalLength) throw new InvalidDataException("Raw chunk length mismatch.");
                    payload.CopyTo(output);
                    break;
                case ChunkEncoding.Zstd:
                    using (var decompressor = new Decompressor())
                    {
                        decompressor.SetParameter(ZSTD_dParameter.ZSTD_d_windowLogMax, BitOperations.Log2((uint)limits.MaxDecoderWindowBytes));
                        if (decompressor.Unwrap(payload, output.AsSpan()) != originalLength)
                            throw new InvalidDataException("Decoded chunk length mismatch.");
                    }
                    break;
                case ChunkEncoding.Lz4:
                    if (payload.Length < 7 || !payload[..4].SequenceEqual(new byte[] { 4, 34, 77, 24 }))
                        throw new InvalidDataException("Invalid LZ4 frame.");
                    var blockCode = (payload[5] >> 4) & 7;
                    if (blockCode < 4 || blockCode > 7 || (payload[5] & 0x8f) != 0)
                        throw new InvalidDataException("Invalid LZ4 block size.");
                    if ((1 << (8 + 2 * blockCode)) * 2L > limits.MaxDecoderWindowBytes)
                        throw new RepositoryIntegrityException(RepositoryIntegrityFailure.LimitExceeded, "LZ4 decoder window exceeds the limit.");
                    using (var input = new MemoryStream(payload.ToArray()))
                    using (var decoder = LZ4Stream.Decode(input, extraMemory: 0, leaveOpen: false, interactive: false))
                        ReadExactDecoded(decoder, output);
                    break;
                case ChunkEncoding.Brotli:
                    // The standard Brotli decoder supports windows up to 2^24; large-window mode is not enabled.
                    if (limits.MaxDecoderWindowBytes < 16 * 1024 * 1024)
                        throw new RepositoryIntegrityException(RepositoryIntegrityFailure.LimitExceeded, "Brotli requires the standard bounded decoder window.");
                    using (var input = new MemoryStream(payload.ToArray()))
                    using (var decoder = new BrotliStream(input, CompressionMode.Decompress))
                        ReadExactDecoded(decoder, output);
                    break;
                case ChunkEncoding.Lzma:
                    if (payload.Length < 26 || !payload[..5].SequenceEqual(new byte[] { 76, 90, 73, 80, 1 }))
                        throw new InvalidDataException("Invalid LZIP frame.");
                    var power = payload[5] & 31;
                    if (power < 12 || power > 24)
                        throw new RepositoryIntegrityException(RepositoryIntegrityFailure.LimitExceeded, "LZIP dictionary exceeds its supported limit.");
                    var dictionaryBytes = (1L << power) - ((payload[5] >> 5) * (1L << (power - 4)));
                    if (dictionaryBytes > limits.MaxDecoderWindowBytes)
                        throw new RepositoryIntegrityException(RepositoryIntegrityFailure.LimitExceeded, "LZIP dictionary exceeds its supported limit.");
                    using (var input = new MemoryStream(payload.ToArray()))
                    using (var decoder = new LZipStream(input, SharpCompressionMode.Decompress, leaveOpen: false))
                        ReadExactDecoded(decoder, output);
                    break;
                default:
                    throw new InvalidDataException("Unknown chunk encoding.");
            }
            return output;
        }
        catch (Exception exception) when (exception is ZstdException or InvalidDataException or EndOfStreamException or SharpCompress.Common.SharpCompressException || (encoding == ChunkEncoding.Brotli && exception is InvalidOperationException))
        {
            throw new RepositoryIntegrityException(RepositoryIntegrityFailure.CorruptObject, $"Chunk could not be decoded within its expected length: {exception.Message}");
        }
    }

    private static void ValidateZstdWindow(ReadOnlySpan<byte> payload, int originalLength, RepositoryIntegrityLimits limits)
    {
        // Frame header layout: https://github.com/facebook/zstd/blob/dev/doc/zstd_compression_format.md#frame_header
        // The one-shot decoder does not enforce the streaming window parameter.
        if (payload.Length < 5 || BinaryPrimitives.ReadUInt32LittleEndian(payload) != 0xfd2fb528 || (payload[4] & 8) != 0)
            throw new InvalidDataException("Invalid Zstandard frame header.");
        var descriptor = payload[4];
        var singleSegment = (descriptor & 32) != 0;
        var cursor = 5;
        ulong window = 0;
        if (!singleSegment)
        {
            if (payload.Length <= cursor) throw new InvalidDataException("Truncated Zstandard window descriptor.");
            var windowDescriptor = payload[cursor++];
            var windowBase = 1UL << (10 + (windowDescriptor >> 3));
            window = windowBase + (windowBase / 8) * (ulong)(windowDescriptor & 7);
        }
        var dictionaryFlag = descriptor & 3;
        cursor += dictionaryFlag == 3 ? 4 : dictionaryFlag;
        var sizeFlag = descriptor >> 6;
        var sizeBytes = sizeFlag == 0 ? (singleSegment ? 1 : 0) : 1 << sizeFlag;
        if (payload.Length - cursor < sizeBytes) throw new InvalidDataException("Truncated Zstandard content size.");
        if (sizeBytes != 0)
        {
            ulong contentSize = sizeBytes switch
            {
                1 => payload[cursor],
                2 => (ulong)BinaryPrimitives.ReadUInt16LittleEndian(payload[cursor..]) + 256,
                4 => BinaryPrimitives.ReadUInt32LittleEndian(payload[cursor..]),
                _ => BinaryPrimitives.ReadUInt64LittleEndian(payload[cursor..])
            };
            if (contentSize > (ulong)limits.MaxDecodedChunkBytes)
                throw new RepositoryIntegrityException(RepositoryIntegrityFailure.LimitExceeded, "Zstandard content size exceeds the decode limit.");
            if (contentSize != (ulong)originalLength) throw new InvalidDataException("Zstandard declared content length mismatch.");
            if (singleSegment) window = contentSize;
        }
        if (window > (ulong)limits.MaxDecoderWindowBytes)
            throw new RepositoryIntegrityException(RepositoryIntegrityFailure.LimitExceeded, "Zstandard decoder window exceeds the limit.");
    }

    private static void ReadExactDecoded(Stream decoder, byte[] output)
    {
        decoder.ReadExactly(output);
        if (decoder.ReadByte() != -1) throw new InvalidDataException("Decoded chunk exceeds its expected length.");
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

    private static byte[] CompressBrotli(ReadOnlySpan<byte> payload, int level)
    {
        using var output = new MemoryStream();
        using (var brotli = new BrotliStream(output, new BrotliCompressionOptions { Quality = Math.Clamp(level + 2, 1, 11) }, leaveOpen: true))
        {
            brotli.Write(payload);
        }

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

}
