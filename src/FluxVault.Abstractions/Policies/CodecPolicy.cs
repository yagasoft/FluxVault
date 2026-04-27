namespace FluxVault.Abstractions.Policies;

public enum CodecProfile
{
    Adaptive = 0,
    Speed = 1,
    Balanced = 2,
    Ratio = 3,
    ColdArchive = 4
}

public sealed record CodecPolicy(
    CompressionPreference Codec,
    CodecProfile Profile,
    int Level,
    long MinimumBytes,
    IReadOnlyList<string> SkipExtensions,
    CompressionPreference HotFileOverride,
    bool EnableDictionaryMode,
    bool EnableLongDistanceMode,
    int Threads)
{
    public static CodecPolicy CreateDefault()
    {
        return new CodecPolicy(
            Codec: CompressionPreference.Zstd,
            Profile: CodecProfile.Adaptive,
            Level: 3,
            MinimumBytes: 256 * 1024,
            SkipExtensions:
            [
                ".7z", ".br", ".cab", ".gif", ".gz", ".jpg", ".jpeg", ".m4v", ".mp3", ".mp4",
                ".mov", ".png", ".rar", ".webp", ".xz", ".zip", ".zst"
            ],
            HotFileOverride: CompressionPreference.Lz4,
            EnableDictionaryMode: false,
            EnableLongDistanceMode: false,
            Threads: 0);
    }
}
