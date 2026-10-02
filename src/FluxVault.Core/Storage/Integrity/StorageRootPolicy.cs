namespace FluxVault.Core.Storage.Integrity;

internal static class StorageRootPolicy
{
    // Classify paths and observed drive properties without probing remote storage.
    internal static bool IsSupported(string path, DriveType driveType, string fileSystem) =>
        IsLocalDrivePath(path) && driveType is DriveType.Fixed or DriveType.Removable &&
        string.Equals(fileSystem, "NTFS", StringComparison.OrdinalIgnoreCase);

    private static bool IsLocalDrivePath(string path) => path.Length >= 3 && char.IsAsciiLetter(path[0]) &&
        path[1] == ':' && path[2] == Path.DirectorySeparatorChar;

    internal static void ValidatePathShape(string path)
    {
        if (!OperatingSystem.IsWindows() || !IsLocalDrivePath(path)) Unsupported();
    }

    internal static void Validate(string path)
    {
        ValidatePathShape(path);
        var drive = new DriveInfo(Path.GetPathRoot(path)!);
        if (drive.DriveType == DriveType.Network) Unsupported();
        // Missing media is an availability failure, distinct from a ready but unsupported volume.
        if (drive.DriveType == DriveType.NoRootDirectory || !drive.IsReady)
            throw new DirectoryNotFoundException($"Storage volume for '{path}' is unavailable.");
        if (!IsSupported(path, drive.DriveType, drive.DriveFormat)) Unsupported();
    }

    [System.Diagnostics.CodeAnalysis.DoesNotReturn]
    private static void Unsupported() => throw new RepositoryIntegrityException(RepositoryIntegrityFailure.OwnershipMismatch,
        "Verified repository storage requires a local Windows NTFS volume. Network, device and other filesystem roots are unsupported.");
}
