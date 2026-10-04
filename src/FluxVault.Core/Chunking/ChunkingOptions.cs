namespace FluxVault.Core.Chunking;

public sealed record ChunkingOptions(int MinimumSize = 64 * 1024, int AverageSize = 256 * 1024, int MaximumSize = 1024 * 1024)
{
    public void Validate()
    {
        if (MinimumSize <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(MinimumSize), "Minimum chunk size must be positive.");
        }

        if (AverageSize < MinimumSize)
        {
            throw new ArgumentOutOfRangeException(nameof(AverageSize), "Average chunk size must be at least the minimum size.");
        }

        if (MaximumSize < AverageSize)
        {
            throw new ArgumentOutOfRangeException(nameof(MaximumSize), "Maximum chunk size must be at least the average size.");
        }
    }
}
