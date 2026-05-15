using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;
using FluxVault.App.Services;
using FluxVault.App.ViewModels;
using WpfButton = System.Windows.Controls.Button;
using WpfDataGrid = System.Windows.Controls.DataGrid;
using WpfDataGridLength = System.Windows.Controls.DataGridLength;

namespace FluxVault.App;

public partial class MainWindow : Window
{
    private const string MirrorGridLayoutKey = "mirrors";
    private bool fileBrowserFilesGridAutoFitted;
    private bool fileBrowserPendingChangesGridAutoFitted;
    private readonly IDataGridLayoutStore layoutStore;
    private MainWindowViewModel? subscribedViewModel;

    public MainWindow()
        : this(new FileDataGridLayoutStore())
    {
    }

    internal MainWindow(IDataGridLayoutStore layoutStore)
    {
        this.layoutStore = layoutStore;
        InitializeComponent();
        DataContextChanged += MainWindow_DataContextChanged;
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
        viewModel.FileBrowser.RefreshBrowser();
    }

    private void MainWindow_DataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (subscribedViewModel is not null)
        {
            subscribedViewModel.VersionInventoryRequested -= ViewModel_VersionInventoryRequested;
        }

        subscribedViewModel = e.NewValue as MainWindowViewModel;
        if (subscribedViewModel is not null)
        {
            subscribedViewModel.VersionInventoryRequested += ViewModel_VersionInventoryRequested;
        }
    }

    private void ViewModel_VersionInventoryRequested(object? sender, VersionInventoryRequestedEventArgs e)
    {
        var window = new ProtectedFolderVersionsWindow(e.Inventory)
        {
            Owner = this
        };
        window.ShowDialog();
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

    private void FileBrowserAddress_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Enter && DataContext is MainWindowViewModel viewModel)
        {
            viewModel.FileBrowser.NavigateAddressCommand.Execute(null);
            e.Handled = true;
        }
    }

    private async void WatchedFoldersGrid_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (DataContext is not MainWindowViewModel viewModel || viewModel.SelectedWatchedFolder is null)
        {
            return;
        }

        var inventory = await viewModel.CreateVersionInventoryAsync(viewModel.SelectedWatchedFolder).ConfigureAwait(true);
        var window = new ProtectedFolderVersionsWindow(inventory)
        {
            Owner = this
        };
        window.ShowDialog();
    }

    private async void RecentVersionsGrid_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (DataContext is MainWindowViewModel viewModel)
        {
            await viewModel.OpenSelectedVersionPreviewCommand.ExecuteAsync(null).ConfigureAwait(true);
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

    private void MirrorNodesGrid_Loaded(object sender, RoutedEventArgs e)
    {
        ApplyDataGridLayout(MirrorNodesGrid, MirrorGridLayoutKey);
        MirrorNodesGrid.AddHandler(
            Thumb.DragCompletedEvent,
            new DragCompletedEventHandler(MirrorNodesGrid_ColumnDragCompleted),
            handledEventsToo: true);
    }

    private void MirrorNodesGrid_Unloaded(object sender, RoutedEventArgs e)
    {
        SaveDataGridLayout(MirrorNodesGrid, MirrorGridLayoutKey);
        MirrorNodesGrid.RemoveHandler(
            Thumb.DragCompletedEvent,
            new DragCompletedEventHandler(MirrorNodesGrid_ColumnDragCompleted));
    }

    private void MirrorNodesGrid_ColumnLayoutChanged(object sender, DataGridColumnEventArgs e)
    {
        SaveDataGridLayout(MirrorNodesGrid, MirrorGridLayoutKey);
    }

    private void MirrorNodesGrid_ColumnDragCompleted(object sender, DragCompletedEventArgs e)
    {
        SaveDataGridLayout(MirrorNodesGrid, MirrorGridLayoutKey);
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

    private void ApplyDataGridLayout(WpfDataGrid grid, string gridKey)
    {
        var savedColumns = layoutStore.Load(gridKey);
        if (savedColumns.Count == 0)
        {
            return;
        }

        foreach (var savedColumn in savedColumns)
        {
            var column = grid.Columns.FirstOrDefault(column => string.Equals(GetColumnKey(column), savedColumn.Key, StringComparison.Ordinal));
            if (column is null || savedColumn.Width <= 0)
            {
                continue;
            }

            column.Width = new WpfDataGridLength(savedColumn.Width);
        }

        foreach (var savedColumn in savedColumns.OrderBy(column => column.DisplayIndex))
        {
            var column = grid.Columns.FirstOrDefault(column => string.Equals(GetColumnKey(column), savedColumn.Key, StringComparison.Ordinal));
            if (column is null)
            {
                continue;
            }

            column.DisplayIndex = Math.Clamp(savedColumn.DisplayIndex, 0, grid.Columns.Count - 1);
        }
    }

    private void SaveDataGridLayout(WpfDataGrid grid, string gridKey)
    {
        var columns = grid.Columns
            .Select(column => new DataGridColumnLayout(
                GetColumnKey(column),
                Math.Ceiling(column.ActualWidth > 0 ? column.ActualWidth : column.Width.Value),
                column.DisplayIndex))
            .Where(column => !string.IsNullOrWhiteSpace(column.Key) && column.Width > 0)
            .OrderBy(column => column.DisplayIndex)
            .ToArray();
        layoutStore.Save(gridKey, columns);
    }

    private static string GetColumnKey(DataGridColumn column)
    {
        return column.Header?.ToString() ?? string.Empty;
    }
}
