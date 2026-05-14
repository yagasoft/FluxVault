using System.IO;
using System.Diagnostics;
using System.Windows;
using FluxVault.App.ViewModels;
using Microsoft.Win32;

namespace FluxVault.App.Services;

public interface IRestoreDestinationPicker
{
    string? PickDestination(VersionRow version);
}

public sealed class SaveFileRestoreDestinationPicker : IRestoreDestinationPicker
{
    public string? PickDestination(VersionRow version)
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            FileName = Path.GetFileName(version.SourcePath),
            Title = "Restore FluxVault version"
        };
        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }
}

public interface IRestoreOverwriteConfirmation
{
    bool ConfirmOverwrite(string destinationPath);
}

public sealed class MessageBoxRestoreOverwriteConfirmation : IRestoreOverwriteConfirmation
{
    public bool ConfirmOverwrite(string destinationPath)
    {
        var result = System.Windows.MessageBox.Show(
            $"The destination file already exists:{Environment.NewLine}{destinationPath}{Environment.NewLine}{Environment.NewLine}Overwrite it?",
            "Overwrite restore destination?",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);
        return result == MessageBoxResult.Yes;
    }
}

public interface IVersionPreviewLauncher
{
    void OpenFile(string filePath);
}

public sealed class ShellVersionPreviewLauncher : IVersionPreviewLauncher
{
    public void OpenFile(string filePath)
    {
        Process.Start(new ProcessStartInfo
        {
            FileName = filePath,
            UseShellExecute = true
        });
    }
}
