namespace FluxVault.Abstractions.Configuration;

public enum MirrorPlacementProfile
{
    FullCopy = 0,
    CapacityBalanced = 1,
    Redundant = 2
}

public sealed record MirrorPlacementPolicyConfiguration(
    MirrorPlacementProfile Profile = MirrorPlacementProfile.FullCopy,
    int MinimumMirrorCopies = 1)
{
    public static MirrorPlacementPolicyConfiguration CreateDefault()
    {
        return new MirrorPlacementPolicyConfiguration();
    }

    public MirrorPlacementPolicyConfiguration Normalise()
    {
        return this with
        {
            MinimumMirrorCopies = Math.Max(1, MinimumMirrorCopies)
        };
    }
}
