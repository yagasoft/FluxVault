using System.IO;
using System.Windows;
using System.Windows.Media;
using FluxVault.App.ViewModels;
using WinForms = System.Windows.Forms;

namespace FluxVault.App;

public partial class App : System.Windows.Application
{
    private WinForms.NotifyIcon? notifyIcon;
    private MainWindow? mainWindow;
    private ActivityPaneWindow? activityPaneWindow;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        var viewModel = new MainWindowViewModel();

        mainWindow = new MainWindow
        {
            DataContext = viewModel
        };
        mainWindow.Activated += (_, _) => _ = viewModel.RefreshAsync();
        mainWindow.Show();
        viewModel.StartAutoRefresh();
        _ = viewModel.RefreshAsync();

        notifyIcon = new WinForms.NotifyIcon
        {
            Icon = LoadFluxVaultIcon(),
            Text = "FluxVault",
            Visible = true,
            ContextMenuStrip = BuildTrayMenu()
        };
        notifyIcon.DoubleClick += (_, _) => ShowDashboard();
        notifyIcon.MouseUp += (_, args) =>
        {
            if (args.Button == WinForms.MouseButtons.Left)
            {
                ShowActivityPane();
            }
        };
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (mainWindow?.DataContext is MainWindowViewModel viewModel)
        {
            viewModel.StopAutoRefresh();
        }

        notifyIcon?.Dispose();
        base.OnExit(e);
    }

    private WinForms.ContextMenuStrip BuildTrayMenu()
    {
        var menu = new WinForms.ContextMenuStrip();
        menu.Items.Add("Open dashboard", null, (_, _) => ShowDashboard());
        menu.Items.Add("Activity", null, (_, _) => ShowActivityPane());
        menu.Items.Add("Blocked files", null, (_, _) => ShowActivityPane());
        menu.Items.Add("Pause/resume protection", null, async (_, _) => await SetProtectionPausedAsync());
        menu.Items.Add("Options", null, (_, _) => ShowOptions());
        menu.Items.Add("Exit", null, (_, _) => Shutdown());
        return menu;
    }

    private void ShowDashboard()
    {
        if (mainWindow is null)
        {
            var viewModel = new MainWindowViewModel();
            mainWindow = new MainWindow
            {
                DataContext = viewModel
            };
            mainWindow.Activated += (_, _) => _ = viewModel.RefreshAsync();
            viewModel.StartAutoRefresh();
            _ = viewModel.RefreshAsync();
        }

        if (mainWindow.DataContext is MainWindowViewModel existingViewModel)
        {
            existingViewModel.StartAutoRefresh();
            _ = existingViewModel.RefreshAsync();
        }

        mainWindow.Show();
        mainWindow.WindowState = WindowState.Normal;
        mainWindow.Activate();
    }

    private void ShowActivityPane()
    {
        if (activityPaneWindow is null)
        {
            activityPaneWindow = new ActivityPaneWindow
            {
                DataContext = new ActivityPaneViewModel(new FluxVault.Core.Ipc.NamedPipeFluxVaultClient()),
                ShowInTaskbar = false
            };
            activityPaneWindow.Closed += (_, _) => activityPaneWindow = null;
        }

        var cursor = WinForms.Control.MousePosition;
        var screen = WinForms.Screen.FromPoint(cursor);
        var dpi = VisualTreeHelper.GetDpi(activityPaneWindow);
        var desiredWidth = activityPaneWindow.ActualWidth > 0 ? activityPaneWindow.ActualWidth : activityPaneWindow.Width;
        var desiredHeight = activityPaneWindow.ActualHeight > 0 ? activityPaneWindow.ActualHeight : activityPaneWindow.Height;
        var placement = TrayPanePlacement.Calculate(
            cursor,
            screen.WorkingArea,
            desiredWidth,
            desiredHeight,
            dpi.DpiScaleX,
            dpi.DpiScaleY);

        activityPaneWindow.MinWidth = Math.Min(activityPaneWindow.MinWidth, placement.Width);
        activityPaneWindow.MinHeight = Math.Min(activityPaneWindow.MinHeight, placement.Height);
        activityPaneWindow.Width = placement.Width;
        activityPaneWindow.Height = placement.Height;
        activityPaneWindow.Left = placement.Left;
        activityPaneWindow.Top = placement.Top;
        activityPaneWindow.Show();
        activityPaneWindow.Activate();
        if (activityPaneWindow.DataContext is ActivityPaneViewModel viewModel)
        {
            _ = viewModel.RefreshAsync();
        }
    }

    private void ShowOptions()
    {
        var owner = mainWindow;
        if (owner is null)
        {
            ShowDashboard();
            owner = mainWindow;
        }

        var window = new OptionsWindow(new OptionsViewModel(new FluxVault.Core.Ipc.NamedPipeFluxVaultClient()))
        {
            Owner = owner
        };
        window.ShowDialog();
        if (owner?.DataContext is MainWindowViewModel viewModel)
        {
            _ = viewModel.RefreshAsync();
        }
    }

    private static async Task SetProtectionPausedAsync()
    {
        var client = new FluxVault.Core.Ipc.NamedPipeFluxVaultClient();
        _ = await client.SendAsync(FluxVault.Abstractions.Ipc.FluxVaultIpcRequest.SetProtectionPaused());
    }

    private static System.Drawing.Icon LoadFluxVaultIcon()
    {
        var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "FluxVault.ico");
        return File.Exists(iconPath)
            ? new System.Drawing.Icon(iconPath)
            : System.Drawing.SystemIcons.Application;
    }
}
