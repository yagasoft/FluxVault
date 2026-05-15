using System.IO;
using System.Diagnostics;
using System.Windows;
using FluxVault.Abstractions.Storage;
using FluxVault.App.ViewModels;
using Microsoft.Win32;
using WinForms = System.Windows.Forms;

namespace FluxVault.App.Services;

public interface IRestoreDestinationPicker
{
    string? PickDestination(VersionRow version);

    string? PickFolderDestination(string sourcePath)
    {
        return null;
    }
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

    public string? PickFolderDestination(string sourcePath)
    {
        using var dialog = new WinForms.FolderBrowserDialog
        {
            Description = "Restore FluxVault versions to folder",
            UseDescriptionForTitle = true
        };
        return dialog.ShowDialog() == WinForms.DialogResult.OK ? dialog.SelectedPath : null;
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

public interface IProtectionRemovalConfirmation
{
    bool ConfirmPurge(IReadOnlyList<RepositoryPurgeScope> scopes);
}

public sealed class MessageBoxProtectionRemovalConfirmation : IProtectionRemovalConfirmation
{
    public bool ConfirmPurge(IReadOnlyList<RepositoryPurgeScope> scopes)
    {
        var sample = string.Join(
            Environment.NewLine,
            scopes.Take(8).Select(scope => $"{scope.Kind}: {scope.SourcePath}"));
        var suffix = scopes.Count > 8
            ? $"{Environment.NewLine}...and {scopes.Count - 8} more."
            : string.Empty;
        var result = System.Windows.MessageBox.Show(
            $"Remove FluxVault backup history for the removed selection(s)?{Environment.NewLine}{Environment.NewLine}{sample}{suffix}{Environment.NewLine}{Environment.NewLine}This deletes matching FluxVault versions from the repository and mirrors. It does not delete live source files.",
            "Permanently purge removed selections?",
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
