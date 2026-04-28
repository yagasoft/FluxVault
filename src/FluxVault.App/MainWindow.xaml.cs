using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using FluxVault.App.ViewModels;
using WpfButton = System.Windows.Controls.Button;
using WpfDataGrid = System.Windows.Controls.DataGrid;
using WpfDataGridLength = System.Windows.Controls.DataGridLength;

namespace FluxVault.App;

public partial class MainWindow : Window
{
    private bool fileBrowserFilesGridAutoFitted;
    private bool fileBrowserPendingChangesGridAutoFitted;

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

    private void About_Click(object sender, RoutedEventArgs e)
    {
        var window = new AboutWindow(new AboutViewModel(new ExternalLinkLauncher()))
        {
            Owner = this
        };
        window.ShowDialog();
    }

    private void FileBrowserTree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (DataContext is MainWindowViewModel viewModel && e.NewValue is FileBrowserFolderNode folder)
        {
            viewModel.FileBrowser.SelectFolder(folder);
            AutoFitFileBrowserFilesGridOnce();
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

    private void FileBrowserGrid_Loaded(object sender, RoutedEventArgs e)
    {
        if (ReferenceEquals(sender, FileBrowserFilesGrid))
        {
            AutoFitDataGridColumnsOnce(FileBrowserFilesGrid);
            return;
        }

        if (ReferenceEquals(sender, FileBrowserPendingChangesGrid))
        {
            AutoFitDataGridColumnsOnce(FileBrowserPendingChangesGrid);
        }
    }

    private void AutoFitFileBrowserFilesGridOnce()
    {
        Dispatcher.BeginInvoke(
            () => AutoFitDataGridColumnsOnce(FileBrowserFilesGrid),
            DispatcherPriority.Loaded);
    }

    private void AutoFitDataGridColumnsOnce(WpfDataGrid grid)
    {
        if (ReferenceEquals(grid, FileBrowserFilesGrid))
        {
            if (fileBrowserFilesGridAutoFitted || grid.Items.Count == 0)
            {
                return;
            }

            fileBrowserFilesGridAutoFitted = true;
        }
        else if (ReferenceEquals(grid, FileBrowserPendingChangesGrid))
        {
            if (fileBrowserPendingChangesGridAutoFitted)
            {
                return;
            }

            fileBrowserPendingChangesGridAutoFitted = true;
        }

        foreach (var column in grid.Columns)
        {
            column.Width = WpfDataGridLength.SizeToCells;
        }

        grid.UpdateLayout();

        foreach (var column in grid.Columns)
        {
            var measuredWidth = Math.Ceiling(column.ActualWidth) + 18;
            column.Width = new WpfDataGridLength(Math.Max(column.MinWidth, measuredWidth));
        }
    }
}
