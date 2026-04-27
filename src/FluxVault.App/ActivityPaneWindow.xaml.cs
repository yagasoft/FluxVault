using System.Windows;
using FluxVault.App.ViewModels;

namespace FluxVault.App;

public partial class ActivityPaneWindow : Window
{
    public ActivityPaneWindow()
    {
        InitializeComponent();
    }

    private async void ActivityPaneWindow_Loaded(object sender, RoutedEventArgs e)
    {
        if (DataContext is ActivityPaneViewModel viewModel)
        {
            await viewModel.RefreshAsync();
        }
    }
}
