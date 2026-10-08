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
            Description = "Choose the parent for a new recovery folder",
            UseDescriptionForTitle = true
        };
        if (dialog.ShowDialog() != WinForms.DialogResult.OK) return null;
        var initialName = Path.GetFileName(Path.TrimEndingDirectorySeparator(sourcePath)) + "-Recovered";
        while (true)
        {
            var name = new NamePromptService().PromptForName("Name the new recovery folder", initialName);
            if (name is null) return null;
            var destination = Path.Combine(dialog.SelectedPath, name);
            if (name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0 && name is not "." and not ".." &&
                !name.EndsWith('.') && !File.Exists(destination) && !Directory.Exists(destination)) return destination;
            WinForms.MessageBox.Show("Choose a valid folder name that does not already exist.", "New recovery folder", WinForms.MessageBoxButtons.OK, WinForms.MessageBoxIcon.Information);
            initialName = name;
        }
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
    bool ConfirmHistoryDeletion(RepositoryHistoryDeletionPreview preview)=>ConfirmPurge([preview.Scope]);
}

public sealed class MessageBoxProtectionRemovalConfirmation : IProtectionRemovalConfirmation
{
    public bool ConfirmHistoryDeletion(RepositoryHistoryDeletionPreview preview)=>System.Windows.MessageBox.Show(
        $"Permanently delete all {preview.CandidateVersionCount} reviewed version(s) in this {preview.Scope.Kind} scope?{Environment.NewLine}{Environment.NewLine}"+
        $"{preview.Scope.SourcePath}{Environment.NewLine}{preview.CandidateLogicalBytes:N0} logical bytes across the reviewed file versions.{Environment.NewLine}{Environment.NewLine}"+
        "This deletes the reviewed FluxVault history from the repository and mirrors. Recovery of these versions will no longer be available. Live source files remain. Software rollback cannot restore deliberately deleted history.",
        "Delete reviewed history permanently?",MessageBoxButton.YesNo,MessageBoxImage.Warning,MessageBoxResult.No)==MessageBoxResult.Yes;

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
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = filePath,
            UseShellExecute = true
        });
    }
}
