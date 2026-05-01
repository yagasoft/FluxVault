using System.Text.Json.Serialization;

namespace FluxVault.Abstractions.Configuration;

public sealed record MirrorSetConfiguration(
    IReadOnlyList<MirrorNodeConfiguration> Nodes,
    MirrorPlacementPolicyConfiguration PlacementPolicy = null!)
{
    public static MirrorSetConfiguration CreateDefault()
    {
        return new MirrorSetConfiguration([], MirrorPlacementPolicyConfiguration.CreateDefault());
    }

    public static MirrorSetConfiguration FromLegacyPath(string? mirrorPath)
    {
        return string.IsNullOrWhiteSpace(mirrorPath)
            ? CreateDefault()
            : new MirrorSetConfiguration(
            [
                new MirrorNodeConfiguration(
                    Id: "default",
                    Label: "Default mirror",
                    Path: Path.GetFullPath(mirrorPath),
                    IsEnabled: true)
            ],
            MirrorPlacementPolicyConfiguration.CreateDefault());
    }

    [JsonIgnore]
    public IReadOnlyList<MirrorNodeConfiguration> EnabledNodes => Nodes
        .Where(node => node.IsEnabled && !string.IsNullOrWhiteSpace(node.Path))
        .ToArray();

    public MirrorSetConfiguration Normalise()
    {
        return new MirrorSetConfiguration((Nodes ?? [])
            .Select(NormaliseNode)
            .ToArray(),
            (PlacementPolicy ?? MirrorPlacementPolicyConfiguration.CreateDefault()).Normalise());
    }

    private static MirrorNodeConfiguration NormaliseNode(MirrorNodeConfiguration node)
    {
        var path = string.IsNullOrWhiteSpace(node.Path)
            ? string.Empty
            : Path.GetFullPath(node.Path);
        return node with
        {
            Id = string.IsNullOrWhiteSpace(node.Id) ? Guid.NewGuid().ToString("N") : node.Id,
            Label = string.IsNullOrWhiteSpace(node.Label) ? "Mirror" : node.Label,
            Path = path,
            Priority = Math.Max(1, node.Priority),
            CapacityBudgetBytes = node.CapacityBudgetBytes is > 0 ? node.CapacityBudgetBytes : null
        };
    }
}
