using System.ComponentModel;

namespace FluxVault.App;

internal sealed class DashboardWindowLifetimeController
{
    private bool isExiting;

    public void BeginExit()
    {
        isExiting = true;
    }

    public void HandleClosing(CancelEventArgs e, Action hide)
    {
        ArgumentNullException.ThrowIfNull(e);
        ArgumentNullException.ThrowIfNull(hide);

        if (isExiting)
        {
            return;
        }

        e.Cancel = true;
        hide();
    }

    public void ShowDashboard(Action show, Action normalise, Action activate)
    {
        ArgumentNullException.ThrowIfNull(show);
        ArgumentNullException.ThrowIfNull(normalise);
        ArgumentNullException.ThrowIfNull(activate);

        show();
        normalise();
        activate();
    }
}
