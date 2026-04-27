namespace FluxVault.Abstractions.Policies;

public enum CompressionPreference
{
    Off = 0,
    Zstd = 1,
    Lz4 = 2,
    Brotli = 3,
    Lzma = 4
}
