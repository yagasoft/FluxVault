using System.Windows;
using FluxVault.App.ViewModels;

namespace FluxVault.App;

public partial class OptionsWindow : Window
{
    public OptionsWindow(OptionsViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        Closing += (_, e) =>
        {
            if (!viewModel.SaveCommand.IsRunning && !viewModel.CheckSaveOutcomeCommand.IsRunning && !viewModel.ReloadSavedOptionsCommand.IsRunning) return;
            e.Cancel = true;
            viewModel.StatusText = "Please wait for the Options operation to finish before closing.";
        };
    }

    private async void OptionsWindow_Loaded(object sender, RoutedEventArgs e)
    {
        if (DataContext is OptionsViewModel viewModel)
        {
            await viewModel.InitialiseAsync().ConfigureAwait(true);
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }
}
