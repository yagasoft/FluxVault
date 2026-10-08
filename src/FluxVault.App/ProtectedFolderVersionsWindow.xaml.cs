using System.Windows;
using System.Windows.Input;
using FluxVault.App.ViewModels;
using WpfDataGrid = System.Windows.Controls.DataGrid;

namespace FluxVault.App;

public partial class ProtectedFolderVersionsWindow : Window
{
    public ProtectedFolderVersionsWindow(VersionInventoryViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        Closing += async (_, args) =>
        {
            if (canClose) return;
            args.Cancel = true;
            if (closeInProgress) return;
            closeInProgress = true;
            IsEnabled = false;
            await viewModel.DisposeAsync().ConfigureAwait(true);
            canClose = true;
            // A synchronous join can finish inside the original Closing event.
            // Queue the final close after WPF has left that event.
            await Dispatcher.InvokeAsync(Close);
        };
    }
    private bool canClose;
    private bool closeInProgress;

    private async void VersionsGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is VersionInventoryViewModel viewModel
            && sender is WpfDataGrid { SelectedItem: VersionInventoryVersionRow version })
        {
            await viewModel.OpenVersionPreviewCommand.ExecuteAsync(version).ConfigureAwait(true);
        }
    }

    private async void SnapshotGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is VersionInventoryViewModel viewModel
            && sender is WpfDataGrid { SelectedItem: VersionInventorySnapshotEntryRow })
        {
            await viewModel.OpenSelectedSnapshotEntryCommand.ExecuteAsync(null).ConfigureAwait(true);
        }
    }
}
