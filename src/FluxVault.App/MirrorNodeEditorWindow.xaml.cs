using System.Windows;
using FluxVault.App.Services;
using FluxVault.App.ViewModels;

namespace FluxVault.App;

public partial class MirrorNodeEditorWindow : Window
{
    private readonly MirrorNodeEditorViewModel viewModel;

    public MirrorNodeEditorWindow(MirrorNodeEditorViewModel viewModel)
    {
        this.viewModel = viewModel;
        DataContext = viewModel;
        InitializeComponent();
    }

    public MirrorNodeDraft? Result { get; private set; }

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        viewModel.Path = WpfMirrorNodeDialogService.BrowseFolder(viewModel.Path);
    }

    private void Accept_Click(object sender, RoutedEventArgs e)
    {
        if (!viewModel.TryCreateDraft(out var draft))
        {
            return;
        }

        Result = draft;
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }
}
