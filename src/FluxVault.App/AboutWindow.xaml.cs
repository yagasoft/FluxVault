using System.Windows;
using FluxVault.App.ViewModels;

namespace FluxVault.App;

public partial class AboutWindow : Window
{
    public AboutWindow(AboutViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }
}
