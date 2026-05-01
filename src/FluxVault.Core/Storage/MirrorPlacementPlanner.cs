using System.Security.Cryptography;
using System.Text;
using FluxVault.Abstractions.Configuration;

namespace FluxVault.Core.Storage;

public sealed class MirrorPlacementPlanner
{
    public MirrorPlacementSelection SelectChunkTargets(
        string chunkDigest,
        long storedLength,
        MirrorSetConfiguration mirrorSet,
        IReadOnlyDictionary<string, long> nodeUsedBytes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(chunkDigest);
        ArgumentNullException.ThrowIfNull(mirrorSet);

        var normalised = mirrorSet.Normalise();
        var enabledNodes = normalised.EnabledNodes;
        var eligibleNodes = enabledNodes
            .Where(node => HasCapacity(node, storedLength, nodeUsedBytes))
            .ToArray();
        var requiredCopyCount = RequiredCopyCount(normalised.PlacementPolicy, enabledNodes.Count);
        var targetCount = Math.Min(requiredCopyCount, eligibleNodes.Length);
        var targets = normalised.PlacementPolicy.Profile switch
        {
            MirrorPlacementProfile.FullCopy => enabledNodes,
            MirrorPlacementProfile.CapacityBalanced => SelectByRendezvous(chunkDigest, eligibleNodes, 1),
            MirrorPlacementProfile.Redundant when targetCount >= eligibleNodes.Length => eligibleNodes,
            MirrorPlacementProfile.Redundant => SelectByRendezvous(chunkDigest, eligibleNodes, targetCount),
            _ => enabledNodes
        };

        return new MirrorPlacementSelection(
            requiredCopyCount,
            targets,
            targets.Select(node => node.Id).ToArray(),
            targets.Count < requiredCopyCount);
    }

    private static int RequiredCopyCount(MirrorPlacementPolicyConfiguration policy, int enabledNodeCount)
    {
        return policy.Profile switch
        {
            MirrorPlacementProfile.FullCopy => enabledNodeCount,
            MirrorPlacementProfile.CapacityBalanced => Math.Min(1, enabledNodeCount),
            MirrorPlacementProfile.Redundant => policy.MinimumMirrorCopies,
            _ => enabledNodeCount
        };
    }

    private static bool HasCapacity(
        MirrorNodeConfiguration node,
        long storedLength,
        IReadOnlyDictionary<string, long> nodeUsedBytes)
    {
        if (node.CapacityBudgetBytes is not { } budget)
        {
            return true;
        }

        nodeUsedBytes.TryGetValue(node.Id, out var usedBytes);
        return budget - usedBytes >= storedLength;
    }

    private static IReadOnlyList<MirrorNodeConfiguration> SelectByRendezvous(
        string chunkDigest,
        IReadOnlyList<MirrorNodeConfiguration> nodes,
        int targetCount)
    {
        return nodes
            .Select(node => new
            {
                Node = node,
                Score = RendezvousScore(chunkDigest, node)
            })
            .OrderByDescending(candidate => candidate.Score)
            .ThenBy(candidate => candidate.Node.Id, StringComparer.OrdinalIgnoreCase)
            .Take(targetCount)
            .Select(candidate => candidate.Node)
            .ToArray();
    }

    private static double RendezvousScore(string chunkDigest, MirrorNodeConfiguration node)
    {
        var input = Encoding.UTF8.GetBytes($"{chunkDigest}|{node.Id}");
        var hash = SHA256.HashData(input);
        var value = BitConverter.ToUInt64(hash, 0);
        var normalised = (value + 1d) / (ulong.MaxValue + 1d);
        return normalised * Math.Max(1, node.Priority);
    }
}

public sealed record MirrorPlacementSelection(
    int RequiredCopyCount,
    IReadOnlyList<MirrorNodeConfiguration> TargetNodes,
    IReadOnlyList<string> TargetNodeIds,
    bool IsUnderSatisfied);
