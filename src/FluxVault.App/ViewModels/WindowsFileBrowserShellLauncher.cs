using System.Diagnostics;
using System.IO;

namespace FluxVault.App.ViewModels;

public sealed class WindowsFileBrowserShellLauncher : IFileBrowserShellLauncher
{
    public void ShowFolder(string folderPath)
    {
        Start("explorer.exe", $"\"{Path.GetFullPath(folderPath)}\"");
    }

    public void OpenFile(string filePath)
    {
        Process.Start(new ProcessStartInfo
        {
            FileName = Path.GetFullPath(filePath),
            UseShellExecute = true
        });
    }

    public void ShowFileInExplorer(string filePath)
    {
        Start("explorer.exe", $"/select,\"{Path.GetFullPath(filePath)}\"");
    }

    private static void Start(string fileName, string arguments)
    {
        Process.Start(new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            UseShellExecute = true
        });
    }
}
