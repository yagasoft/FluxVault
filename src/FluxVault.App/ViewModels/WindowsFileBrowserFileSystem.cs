using System.IO;

namespace FluxVault.App.ViewModels;

public sealed class WindowsFileBrowserFileSystem : IFileBrowserFileSystem
{
    public IReadOnlyList<FileBrowserFolderInfo> GetRoots()
    {
        return DriveInfo.GetDrives()
            .Select(drive => new FileBrowserFolderInfo(
                drive.RootDirectory.FullName,
                drive.Name,
                drive.IsReady,
                drive.IsReady ? null : "Drive is not ready."))
            .ToArray();
    }

    public IReadOnlyList<FileBrowserFolderInfo> GetChildFolders(string path)
    {
        try
        {
            return Directory.EnumerateDirectories(path)
                .Select(folder => new FileBrowserFolderInfo(
                    System.IO.Path.GetFullPath(folder),
                    System.IO.Path.GetFileName(folder),
                    IsAccessible: true,
                    ErrorMessage: null))
                .OrderBy(folder => folder.Name, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DirectoryNotFoundException)
        {
            return [new FileBrowserFolderInfo(path, "Unavailable", IsAccessible: false, ex.Message)];
        }
    }

    public IReadOnlyList<FileBrowserFileInfo> GetFiles(string path)
    {
        try
        {
            return Directory.EnumerateFiles(path)
                .Select(file =>
                {
                    var info = new FileInfo(file);
                    return new FileBrowserFileInfo(
                        info.FullName,
                        info.Name,
                        info.Exists ? info.Length : 0);
                })
                .OrderBy(file => file.Name, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        catch (Exception) when (!Directory.Exists(path))
        {
            return [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [new FileBrowserFileInfo(System.IO.Path.Combine(path, ex.GetType().Name), ex.Message, 0)];
        }
    }
}
