namespace FluxVault.Abstractions.Configuration;

public sealed record MirrorNodeConfiguration(
    string Id,
    string Label,
    string Path,
    bool IsEnabled = true)
{
    public const string PlacementSemantics = "FullCopy";

    public static MirrorNodeConfiguration Create(string path, string? label = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return new MirrorNodeConfiguration(
            Id: Guid.NewGuid().ToString("N"),
            Label: string.IsNullOrWhiteSpace(label) ? "Mirror" : label,
            Path: System.IO.Path.GetFullPath(path),
            IsEnabled: true);
    }
}
