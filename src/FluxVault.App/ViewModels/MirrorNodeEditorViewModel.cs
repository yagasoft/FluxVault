using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using FluxVault.App.Services;

namespace FluxVault.App.ViewModels;

public sealed partial class MirrorNodeEditorViewModel : ObservableObject
{
    [ObservableProperty]
    private string label;

    [ObservableProperty]
    private string path;

    [ObservableProperty]
    private bool isEnabled;

    [ObservableProperty]
    private string capacityBudgetBytesText;

    [ObservableProperty]
    private int priority;

    [ObservableProperty]
    private string errorMessage = string.Empty;

    public MirrorNodeEditorViewModel(string title, string primaryButtonText, MirrorNodeDraft draft)
    {
        Title = title;
        PrimaryButtonText = primaryButtonText;
        label = draft.Label;
        path = draft.Path;
        isEnabled = draft.IsEnabled;
        capacityBudgetBytesText = draft.CapacityBudgetBytes?.ToString() ?? string.Empty;
        priority = Math.Max(1, draft.Priority);
    }

    public string Title { get; }

    public string PrimaryButtonText { get; }

    public bool TryCreateDraft(out MirrorNodeDraft draft)
    {
        ErrorMessage = string.Empty;
        draft = new MirrorNodeDraft(string.Empty, string.Empty, IsEnabled);

        if (string.IsNullOrWhiteSpace(Label))
        {
            ErrorMessage = "Enter a mirror label.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(Path))
        {
            ErrorMessage = "Choose a mirror path.";
            return false;
        }

        long? capacityBudgetBytes = null;
        if (!string.IsNullOrWhiteSpace(CapacityBudgetBytesText))
        {
            if (!long.TryParse(CapacityBudgetBytesText, out var parsedCapacityBudgetBytes) || parsedCapacityBudgetBytes <= 0)
            {
                ErrorMessage = "Capacity bytes must be blank or a positive whole number.";
                return false;
            }

            capacityBudgetBytes = parsedCapacityBudgetBytes;
        }

        if (Priority < 1)
        {
            ErrorMessage = "Priority must be at least 1.";
            return false;
        }

        string fullPath;
        try
        {
            fullPath = System.IO.Path.GetFullPath(Path.Trim());
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            ErrorMessage = "Enter a valid mirror path.";
            return false;
        }

        draft = new MirrorNodeDraft(Label.Trim(), fullPath, IsEnabled, capacityBudgetBytes, Priority);
        return true;
    }
}
