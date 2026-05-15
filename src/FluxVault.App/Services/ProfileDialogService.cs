using WinForms = System.Windows.Forms;

namespace FluxVault.App.Services;

public interface IProfileDialogService
{
    string? PromptForProfileName(string title, string initialValue);

    bool ConfirmDelete(string displayName);
}

public sealed class ProfileDialogService : IProfileDialogService
{
    public string? PromptForProfileName(string title, string initialValue)
    {
        using var form = new WinForms.Form
        {
            Text = title,
            Width = 420,
            Height = 150,
            StartPosition = WinForms.FormStartPosition.CenterParent,
            FormBorderStyle = WinForms.FormBorderStyle.FixedDialog,
            MaximizeBox = false,
            MinimizeBox = false
        };
        using var textBox = new WinForms.TextBox
        {
            Left = 14,
            Top = 16,
            Width = 372,
            Text = initialValue
        };
        using var ok = new WinForms.Button
        {
            Text = "OK",
            DialogResult = WinForms.DialogResult.OK,
            Left = 226,
            Width = 76,
            Top = 58
        };
        using var cancel = new WinForms.Button
        {
            Text = "Cancel",
            DialogResult = WinForms.DialogResult.Cancel,
            Left = 310,
            Width = 76,
            Top = 58
        };
        form.Controls.Add(textBox);
        form.Controls.Add(ok);
        form.Controls.Add(cancel);
        form.AcceptButton = ok;
        form.CancelButton = cancel;
        return form.ShowDialog() == WinForms.DialogResult.OK && !string.IsNullOrWhiteSpace(textBox.Text)
            ? textBox.Text.Trim()
            : null;
    }

    public bool ConfirmDelete(string displayName)
    {
        return WinForms.MessageBox.Show(
            $"Delete FluxVault profile '{displayName}'?",
            "Delete profile",
            WinForms.MessageBoxButtons.YesNo,
            WinForms.MessageBoxIcon.Warning) == WinForms.DialogResult.Yes;
    }
}
