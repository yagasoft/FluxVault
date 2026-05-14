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
    }

    private async void VersionsGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is VersionInventoryViewModel viewModel
            && sender is WpfDataGrid { SelectedItem: VersionInventoryVersionRow version })
        {
            await viewModel.OpenVersionPreviewCommand.ExecuteAsync(version).ConfigureAwait(true);
        }
    }
}
