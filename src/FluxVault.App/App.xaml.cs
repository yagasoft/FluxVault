using System.Windows;
using FluxVault.App.ViewModels;
using WinForms = System.Windows.Forms;

namespace FluxVault.App;

public partial class App : System.Windows.Application
{
    private WinForms.NotifyIcon? notifyIcon;
    private MainWindow? mainWindow;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        mainWindow = new MainWindow
        {
            DataContext = MainWindowViewModel.DesignTime()
        };
        mainWindow.Show();

        notifyIcon = new WinForms.NotifyIcon
        {
            Icon = System.Drawing.SystemIcons.Application,
            Text = "FluxVault",
            Visible = true,
            ContextMenuStrip = BuildTrayMenu()
        };
        notifyIcon.DoubleClick += (_, _) => ShowDashboard();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        notifyIcon?.Dispose();
        base.OnExit(e);
    }

    private WinForms.ContextMenuStrip BuildTrayMenu()
    {
        var menu = new WinForms.ContextMenuStrip();
        menu.Items.Add("Open dashboard", null, (_, _) => ShowDashboard());
        menu.Items.Add("Exit", null, (_, _) => Shutdown());
        return menu;
    }

    private void ShowDashboard()
    {
        if (mainWindow is null)
        {
            mainWindow = new MainWindow
            {
                DataContext = MainWindowViewModel.DesignTime()
            };
        }

        mainWindow.Show();
        mainWindow.WindowState = WindowState.Normal;
        mainWindow.Activate();
    }
}
