using System;

namespace DotaCoach.Desktop;

public readonly record struct OverlayBounds(int Left, int Top, int Width, int Height);

/// <summary>Physical-pixel placement: 0 = top, 50 = center, 100 = bottom of available space.</summary>
public static class OverlayPlacement
{
    public const double EdgeMarginDip = 8;

    public static OverlayBounds Calculate(
        int screenLeft, int screenTop, int screenWidth, int screenHeight,
        double measuredHeightDip, double dpiScale, double positionPercent)
    {
        if (screenWidth < 1 || screenHeight < 1) throw new ArgumentOutOfRangeException(nameof(screenWidth));
        if (!double.IsFinite(dpiScale) || dpiScale <= 0) dpiScale = 1;
        if (!double.IsFinite(measuredHeightDip) || measuredHeightDip < 0) measuredHeightDip = 1;
        if (!double.IsFinite(positionPercent)) positionPercent = 5;

        int width = Math.Max(1, (int)Math.Round(screenWidth * 0.8));
        int margin = (int)Math.Min(Math.Ceiling(EdgeMarginDip * dpiScale), (screenHeight - 1) / 2);
        int availableHeight = screenHeight - 2 * margin;
        int height = (int)Math.Clamp(Math.Ceiling(measuredHeightDip * dpiScale), 1, availableHeight);
        // Subtract the ACTUAL caption panel height, not a fixed, mostly empty overlay window.
        int travel = availableHeight - height;
        int top = screenTop + margin + (int)Math.Round(travel * Math.Clamp(positionPercent, 0, 100) / 100);
        return new(screenLeft + (screenWidth - width) / 2, top, width, height);
    }
}
