using System.Windows;
using System.Windows.Controls;
using FluxVault.App.ViewModels;
using WpfButton = System.Windows.Controls.Button;

namespace FluxVault.App;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
    }

    private async void Options_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel viewModel)
        {
            return;
        }

        var optionsViewModel = new OptionsViewModel(viewModel.ServiceClient);
        var window = new OptionsWindow(optionsViewModel)
        {
            Owner = this
        };
        window.ShowDialog();
        await viewModel.RefreshAsync().ConfigureAwait(true);
    }

    private void FileBrowserTree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (DataContext is MainWindowViewModel viewModel && e.NewValue is FileBrowserFolderNode folder)
        {
            viewModel.FileBrowser.SelectFolder(folder);
        }
    }

    private void FileBrowserFolder_Expanded(object sender, RoutedEventArgs e)
    {
        if (DataContext is MainWindowViewModel viewModel
            && e.OriginalSource is TreeViewItem { DataContext: FileBrowserFolderNode folder })
        {
            viewModel.FileBrowser.LoadChildren(folder);
        }
    }

    private void FolderSelection_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is MainWindowViewModel viewModel && sender is WpfButton { DataContext: FileBrowserFolderNode folder })
        {
            viewModel.FileBrowser.ToggleFolderSelection(folder);
            e.Handled = true;
        }
    }
}
