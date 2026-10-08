namespace FluxVault.App.Services;

public interface IDiagnosticsExportFolderPicker
{
    string? PickFolder();
}

public sealed class DiagnosticsExportFolderPicker : IDiagnosticsExportFolderPicker
{
    public string? PickFolder()
    {
        using var dialog = new System.Windows.Forms.FolderBrowserDialog
        { Description = "Choose a folder for the diagnostics report", UseDescriptionForTitle = true };
        return dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK ? dialog.SelectedPath : null;
    }
}
