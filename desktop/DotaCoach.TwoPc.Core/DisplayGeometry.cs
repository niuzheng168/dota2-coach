namespace DotaCoach.TwoPc;

/// <summary>User-confirmed minimap rectangle in original game-client pixels, not JPEG or WPF pixels.</summary>
public sealed record MinimapLayout(int SourceWidth, int SourceHeight, double Left, double Top, double Size)
{
    public bool IsValidFor(int width, int height) =>
        SourceWidth == width && SourceHeight == height && width is >= 640 and <= 7680 &&
        height is >= 480 and <= 4320 && Wire.Finite(Size, height * .10, height * .45) &&
        Wire.Finite(Left, 0, width - Size) && Wire.Finite(Top, height * .5, height - Size) &&
        (Left <= width * .10 || Left + Size >= width * .90);
    public bool Contains(ScreenMark point) => IsValidFor(SourceWidth, SourceHeight) &&
        Wire.Finite(point.X, Left / SourceWidth, (Left + Size) / SourceWidth) &&
        Wire.Finite(point.Y, Top / SourceHeight, (Top + Size) / SourceHeight);
    // Default-HUD box scaled from the height (bottom-left unless right). Used as the automatic layout when the
    // coach enables it and nothing is confirmed; never a live map observation.
    public static MinimapLayout Suggest(int width, int height, bool right = false)
    {
        double size = height * (160.0 / 720);
        return new(width, height, right ? width - size : 0, height - size, size);
    }
}

public static class DisplayGeometry
{
    public static (int Width, int Height) EncodedSize(int width, int height)
    {
        if (width is < 640 or > 7680 || height is < 480 or > 4320) throw new ArgumentException("Unsupported game surface");
        double scale = Math.Min(1, Math.Min(1600.0 / width, 900.0 / height));
        return ((int)(width * scale), (int)(height * scale));
    }
    // Physical-size scaling follows the actual game viewport, then converts to WPF DIPs.
    public static double OverlayUnit(int sourceHeight, double dpiScale)
    {
        if (sourceHeight is < 480 or > 4320 || !Wire.Finite(dpiScale, 1, 8))
            throw new ArgumentException("Invalid display geometry");
        return Math.Clamp(sourceHeight / 720.0, 1, 4) / dpiScale;
    }
}
