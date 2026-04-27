namespace FluxVault.Abstractions.Storage;

public enum ChunkEncoding
{
    Raw = 0,
    Zstd = 1,
    Lz4 = 2,
    Brotli = 3,
    Lzma = 4
}
