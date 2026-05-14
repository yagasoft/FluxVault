using System.IO;
using System.Windows;
using FluxVault.App.ViewModels;
using WinForms = System.Windows.Forms;

namespace FluxVault.App.Services;

public sealed record MirrorNodeDraft(
    string Label,
    string Path,
    bool IsEnabled,
    long? CapacityBudgetBytes = null,
    int Priority = 100);

public interface IMirrorNodeDialogService
{
    MirrorNodeDraft? ShowAddMirrorDialog(MirrorNodeDraft initialDraft);

    MirrorNodeDraft? ShowEditMirrorDialog(MirrorNodeDraft currentDraft);

    string BrowseMirrorPath(string currentPath);
}

public sealed class WpfMirrorNodeDialogService : IMirrorNodeDialogService
{
    public MirrorNodeDraft? ShowAddMirrorDialog(MirrorNodeDraft initialDraft)
    {
        return ShowDialog("New mirror", "Add", initialDraft);
    }

    public MirrorNodeDraft? ShowEditMirrorDialog(MirrorNodeDraft currentDraft)
    {
        return ShowDialog("Edit mirror", "Save", currentDraft);
    }

    public string BrowseMirrorPath(string currentPath)
    {
        return BrowseFolder(currentPath);
    }

    private static MirrorNodeDraft? ShowDialog(string title, string primaryButtonText, MirrorNodeDraft draft)
    {
        var viewModel = new MirrorNodeEditorViewModel(title, primaryButtonText, draft);
        var window = new MirrorNodeEditorWindow(viewModel)
        {
            Owner = System.Windows.Application.Current?.Windows.OfType<Window>().FirstOrDefault(window => window.IsActive)
        };
        return window.ShowDialog() == true ? window.Result : null;
    }

    internal static string BrowseFolder(string selectedPath)
    {
        using var dialog = new WinForms.FolderBrowserDialog
        {
            SelectedPath = Directory.Exists(selectedPath) ? selectedPath : string.Empty,
            UseDescriptionForTitle = true,
            Description = "Select mirror folder"
        };
        return dialog.ShowDialog() == WinForms.DialogResult.OK ? dialog.SelectedPath : selectedPath;
    }
}
