using System.Drawing;
using FluxVault.App;

namespace FluxVault.App.Tests;

public sealed class TrayPanePlacementTests
{
    [Fact]
    public void Bottom_taskbar_position_keeps_pane_inside_working_area()
    {
        var placement = TrayPanePlacement.Calculate(
            anchorPixels: new Point(1800, 1030),
            workingAreaPixels: new Rectangle(0, 0, 1920, 1040),
            paneWidthDip: 420,
            paneHeightDip: 520,
            dpiScaleX: 1,
            dpiScaleY: 1);

        Assert.True(placement.Left >= 8);
        Assert.True(placement.Top >= 8);
        Assert.True(placement.Right <= 1912);
        Assert.True(placement.Bottom <= 1032);
        Assert.True(placement.Bottom < 1040);
    }

    [Fact]
    public void Right_edge_position_clamps_to_working_area()
    {
        var placement = TrayPanePlacement.Calculate(
            anchorPixels: new Point(1915, 700),
            workingAreaPixels: new Rectangle(0, 0, 1920, 1040),
            paneWidthDip: 420,
            paneHeightDip: 520,
            dpiScaleX: 1,
            dpiScaleY: 1);

        Assert.True(placement.Right <= 1912);
    }

    [Fact]
    public void Left_edge_position_clamps_to_working_area()
    {
        var placement = TrayPanePlacement.Calculate(
            anchorPixels: new Point(4, 700),
            workingAreaPixels: new Rectangle(0, 0, 1920, 1040),
            paneWidthDip: 420,
            paneHeightDip: 520,
            dpiScaleX: 1,
            dpiScaleY: 1);

        Assert.True(placement.Left >= 8);
    }

    [Fact]
    public void High_dpi_position_converts_pixels_to_device_independent_units()
    {
        var placement = TrayPanePlacement.Calculate(
            anchorPixels: new Point(2700, 1545),
            workingAreaPixels: new Rectangle(0, 0, 2880, 1560),
            paneWidthDip: 420,
            paneHeightDip: 520,
            dpiScaleX: 1.5,
            dpiScaleY: 1.5);

        Assert.True(placement.Right <= 1912);
        Assert.True(placement.Bottom <= 1032);
    }

    [Fact]
    public void Small_working_area_reduces_pane_size_to_fit()
    {
        var placement = TrayPanePlacement.Calculate(
            anchorPixels: new Point(380, 300),
            workingAreaPixels: new Rectangle(0, 0, 420, 320),
            paneWidthDip: 420,
            paneHeightDip: 520,
            dpiScaleX: 1,
            dpiScaleY: 1);

        Assert.True(placement.Width <= 404);
        Assert.True(placement.Height <= 304);
        Assert.True(placement.Left >= 8);
        Assert.True(placement.Top >= 8);
        Assert.True(placement.Right <= 412);
        Assert.True(placement.Bottom <= 312);
    }
}
