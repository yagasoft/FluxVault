using System.Windows;
using FluxVault.App.ViewModels;

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
}
