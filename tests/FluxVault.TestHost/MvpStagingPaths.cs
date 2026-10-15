using System.IO;

namespace FluxVault.Testing;

internal static class MvpStagingPaths
{
    internal const string Parent = @"I:\FluxVault Staging\MVP";

    internal static string ResolveRoot(string root)
    {
        if (!Path.IsPathFullyQualified(root))
            throw new InvalidDataException("Use an absolute owned MVP staging directory.");
        root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        if (!string.Equals(Path.GetDirectoryName(root), Parent, StringComparison.OrdinalIgnoreCase) ||
            !Guid.TryParseExact(Path.GetFileName(root), "N", out var id) || id == Guid.Empty)
            throw new InvalidDataException("Use an exact owned MVP staging directory on I:.");
        WindowsDatabaseProbeConfiguration.RejectReparseComponents(root);
        return root;
    }
}
