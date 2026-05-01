using FluxVault.Abstractions.Configuration;
using FluxVault.Core.Storage;

namespace FluxVault.Core.Tests;

public sealed class MirrorPlacementPlannerTests
{
    [Fact]
    public void Full_copy_selects_every_enabled_node()
    {
        var planner = new MirrorPlacementPlanner();
        var mirrorSet = MirrorSet(
            new MirrorPlacementPolicyConfiguration(MirrorPlacementProfile.FullCopy),
            Node("first"),
            Node("second"),
            Node("disabled", isEnabled: false));

        var result = planner.SelectChunkTargets(
            "chunk-a",
            storedLength: 256,
            mirrorSet,
            new Dictionary<string, long>());

        Assert.Equal(["first", "second"], result.TargetNodeIds);
        Assert.False(result.IsUnderSatisfied);
    }

    [Fact]
    public void Redundant_selects_requested_copy_count_capped_to_enabled_nodes()
    {
        var planner = new MirrorPlacementPlanner();
        var mirrorSet = MirrorSet(
            new MirrorPlacementPolicyConfiguration(MirrorPlacementProfile.Redundant, MinimumMirrorCopies: 2),
            Node("first"),
            Node("second"),
            Node("third", isEnabled: false));

        var result = planner.SelectChunkTargets(
            "chunk-a",
            storedLength: 256,
            mirrorSet,
            new Dictionary<string, long>());

        Assert.Equal(2, result.TargetNodeIds.Count);
        Assert.Subset(new HashSet<string>(["first", "second"]), result.TargetNodeIds.ToHashSet());
        Assert.False(result.IsUnderSatisfied);
    }

    [Fact]
    public void Redundant_reports_under_satisfied_when_enabled_nodes_are_too_few()
    {
        var planner = new MirrorPlacementPlanner();
        var mirrorSet = MirrorSet(
            new MirrorPlacementPolicyConfiguration(MirrorPlacementProfile.Redundant, MinimumMirrorCopies: 3),
            Node("first"),
            Node("second"));

        var result = planner.SelectChunkTargets(
            "chunk-a",
            storedLength: 256,
            mirrorSet,
            new Dictionary<string, long>());

        Assert.Equal(["first", "second"], result.TargetNodeIds);
        Assert.True(result.IsUnderSatisfied);
        Assert.Equal(3, result.RequiredCopyCount);
    }

    [Fact]
    public void Capacity_balanced_avoids_nodes_without_enough_remaining_budget()
    {
        var planner = new MirrorPlacementPlanner();
        var mirrorSet = MirrorSet(
            new MirrorPlacementPolicyConfiguration(MirrorPlacementProfile.CapacityBalanced),
            Node("full", capacityBudgetBytes: 1_000),
            Node("available", capacityBudgetBytes: 1_000));
        var usedBytes = new Dictionary<string, long>
        {
            ["full"] = 900,
            ["available"] = 100
        };

        var result = planner.SelectChunkTargets(
            "chunk-a",
            storedLength: 256,
            mirrorSet,
            usedBytes);

        Assert.Equal(["available"], result.TargetNodeIds);
        Assert.False(result.IsUnderSatisfied);
    }

    private static MirrorSetConfiguration MirrorSet(
        MirrorPlacementPolicyConfiguration placementPolicy,
        params MirrorNodeConfiguration[] nodes)
    {
        return new MirrorSetConfiguration(nodes, placementPolicy);
    }

    private static MirrorNodeConfiguration Node(
        string id,
        bool isEnabled = true,
        long? capacityBudgetBytes = null,
        int priority = 100)
    {
        return new MirrorNodeConfiguration(
            Id: id,
            Label: id,
            Path: Path.Combine(Path.GetTempPath(), id),
            IsEnabled: isEnabled,
            CapacityBudgetBytes: capacityBudgetBytes,
            Priority: priority);
    }
}
