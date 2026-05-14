using System.ComponentModel;
using System.IO;

namespace FluxVault.App.Tests;

public sealed class DashboardWindowLifetimeTests
{
    [Fact]
    public void Dashboard_close_hides_to_tray_when_app_is_not_exiting()
    {
        var controller = new DashboardWindowLifetimeController();
        var args = new CancelEventArgs();
        var hideCount = 0;
        Action hide = () => hideCount++;

        controller.HandleClosing(args, hide);

        Assert.True(args.Cancel);
        Assert.Equal(1, hideCount);
    }

    [Fact]
    public void Dashboard_close_is_allowed_after_exit_begins()
    {
        var controller = new DashboardWindowLifetimeController();
        var args = new CancelEventArgs();
        var hideCount = 0;
        Action hide = () => hideCount++;

        controller.BeginExit();
        controller.HandleClosing(args, hide);

        Assert.False(args.Cancel);
        Assert.Equal(0, hideCount);
    }

    [Fact]
    public void Dashboard_reopen_shows_normalises_and_activates_each_time()
    {
        var controller = new DashboardWindowLifetimeController();
        var calls = new List<string>();

        controller.ShowDashboard(
            () => calls.Add("show"),
            () => calls.Add("normalise"),
            () => calls.Add("activate"));
        controller.ShowDashboard(
            () => calls.Add("show"),
            () => calls.Add("normalise"),
            () => calls.Add("activate"));

        Assert.Equal(
            ["show", "normalise", "activate", "show", "normalise", "activate"],
            calls);
    }

    [Fact]
    public void Dashboard_close_policy_is_wired_into_app_window_and_exit_paths()
    {
        var text = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "src", "FluxVault.App", "App.xaml.cs"));

        Assert.Contains("window.Closing += DashboardWindow_Closing", text, StringComparison.Ordinal);
        Assert.Contains("dashboardWindowLifetime.HandleClosing", text, StringComparison.Ordinal);
        Assert.Contains("ExitApplication", text, StringComparison.Ordinal);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "FluxVault.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("Could not locate repository root.");
    }
}
