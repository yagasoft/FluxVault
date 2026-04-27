using System.Drawing;

namespace FluxVault.App;

internal static class TrayPanePlacement
{
    private const double Margin = 8;
    private const double MinimumWidth = 320;
    private const double MinimumHeight = 260;

    public static TrayPaneBounds Calculate(
        Point anchorPixels,
        Rectangle workingAreaPixels,
        double paneWidthDip,
        double paneHeightDip,
        double dpiScaleX,
        double dpiScaleY)
    {
        dpiScaleX = dpiScaleX <= 0 ? 1 : dpiScaleX;
        dpiScaleY = dpiScaleY <= 0 ? 1 : dpiScaleY;

        var workingLeft = workingAreaPixels.Left / dpiScaleX;
        var workingTop = workingAreaPixels.Top / dpiScaleY;
        var workingRight = workingAreaPixels.Right / dpiScaleX;
        var workingBottom = workingAreaPixels.Bottom / dpiScaleY;
        var workingWidth = Math.Max(1, workingAreaPixels.Width / dpiScaleX);
        var workingHeight = Math.Max(1, workingAreaPixels.Height / dpiScaleY);

        var availableWidth = Math.Max(1, workingWidth - (Margin * 2));
        var availableHeight = Math.Max(1, workingHeight - (Margin * 2));
        var width = Math.Clamp(paneWidthDip, Math.Min(MinimumWidth, availableWidth), availableWidth);
        var height = Math.Clamp(paneHeightDip, Math.Min(MinimumHeight, availableHeight), availableHeight);

        var anchorX = anchorPixels.X / dpiScaleX;
        var anchorY = anchorPixels.Y / dpiScaleY;
        var left = anchorX - width + 24;
        var top = anchorY - height - 12;

        left = Clamp(left, workingLeft + Margin, workingRight - Margin - width);
        top = Clamp(top, workingTop + Margin, workingBottom - Margin - height);

        return new TrayPaneBounds(left, top, width, height);
    }

    private static double Clamp(double value, double minimum, double maximum)
    {
        if (maximum < minimum)
        {
            return minimum;
        }

        return Math.Min(Math.Max(value, minimum), maximum);
    }
}

internal readonly record struct TrayPaneBounds(double Left, double Top, double Width, double Height)
{
    public double Right => Left + Width;

    public double Bottom => Top + Height;
}
