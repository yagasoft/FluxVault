using System.Numerics;

namespace FluxVault.Core.Chunking;

internal static class FastCdcBoundary
{
    private static readonly ulong[] Gear = CreateGearTable();

    public static int FindCut(ReadOnlySpan<byte> span, ChunkingOptions options)
    {
        if (span.Length <= options.MaximumSize)
        {
            return span.Length;
        }

        var hash = 0UL;
        var cutMask = (ulong)BitOperations.RoundUpToPowerOf2((uint)options.AverageSize) - 1UL;
        var limit = Math.Min(options.MaximumSize, span.Length);

        for (var length = 1; length <= limit; length++)
        {
            hash = (hash << 1) + Gear[span[length - 1]];

            if (length >= options.MinimumSize && (hash & cutMask) == 0)
            {
                return length;
            }
        }

        return limit;
    }

    private static ulong[] CreateGearTable()
    {
        var table = new ulong[256];
        var state = 0x9E3779B97F4A7C15UL;

        for (var i = 0; i < table.Length; i++)
        {
            state ^= state >> 12;
            state ^= state << 25;
            state ^= state >> 27;
            table[i] = state * 0x2545F4914F6CDD1DUL;
        }

        return table;
    }
}
