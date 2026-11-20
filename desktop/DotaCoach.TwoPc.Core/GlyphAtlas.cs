using System.Runtime.InteropServices;

namespace DotaCoach.TwoPc;

/// <summary>
/// White glyphs with a black outline, premultiplied BGRA, rendered once with GDI (grayscale anti-aliasing).
/// The overlay tints the white and keeps the outline, so numbers stay legible on any terrain.
/// </summary>
public sealed unsafe class GlyphAtlas
{
    public const string Characters = "0123456789?~+-%";
    public int Width { get; }
    public int Height { get; }
    public byte[] Bgra { get; }
    /// <summary>Source rectangle of each glyph including <see cref="Pad"/> pixels of outline room per side, and its text advance.</summary>
    public IReadOnlyDictionary<char, Glyph> Glyphs { get; }
    public int Pad { get; }
    public readonly record struct Glyph(int X, int Y, int Width, int Height, int Advance);

    public GlyphAtlas(int pixelHeight)
    {
        if (pixelHeight is < 8 or > 160) throw new ArgumentOutOfRangeException(nameof(pixelHeight));
        int outline = Math.Max(1, (int)Math.Round(pixelHeight / 9d)), pad = outline + 1, cell = pixelHeight + 2 * pad;
        Pad = pad;
        Width = (cell * Characters.Length + 3) & ~3; Height = cell;
        nint dc = CreateCompatibleDC(0), bitmap = 0, font = 0;
        var coverage = new byte[Width * Height];
        var glyphs = new Dictionary<char, Glyph>();
        try {
            var info = stackalloc int[10];
            info[0] = 40; info[1] = Width; info[2] = -Height; info[3] = 1 | (32 << 16);
            nint bits;
            bitmap = CreateDIBSection(dc, info, 0, &bits, 0, 0);
            if (bitmap == 0) throw new InvalidOperationException("GDI bitmap");
            SelectObject(dc, bitmap);
            font = CreateFontW(-pixelHeight, 0, 0, 0, 700, 0, 0, 0, 1, 0, 0, 4 /* antialiased, no ClearType */, 0, "Segoe UI");
            SelectObject(dc, font);
            SetTextColor(dc, 0xFFFFFF); SetBkMode(dc, 1);
            for (int i = 0; i < Characters.Length; i++) {
                string text = Characters[i].ToString();
                GetTextExtentPoint32W(dc, text, 1, out var size);
                TextOutW(dc, i * cell + pad, pad, text, 1);
                glyphs[Characters[i]] = new(i * cell, 0, Math.Min(cell, size.Cx + 2 * pad), cell, size.Cx);
            }
            GdiFlush();
            byte* p = (byte*)bits;
            for (int i = 0; i < coverage.Length; i++) coverage[i] = p[i * 4 + 1];
        } finally {
            if (font != 0) DeleteObject(font);
            if (bitmap != 0) DeleteObject(bitmap);
            DeleteDC(dc);
        }
        Glyphs = glyphs;
        Bgra = new byte[Width * Height * 4];
        int r = outline;
        for (int y = 0; y < Height; y++)
        for (int x = 0; x < Width; x++) {
            int ring = 0;
            for (int dy = -r; dy <= r; dy++)
            for (int dx = -r; dx <= r; dx++) {
                int xx = x + dx, yy = y + dy;
                if (xx < 0 || yy < 0 || xx >= Width || yy >= Height || dx * dx + dy * dy > r * r + r) continue;
                ring = Math.Max(ring, coverage[yy * Width + xx]);
            }
            int fill = coverage[y * Width + x], alpha = fill + (255 - fill) * ring / 255, o = (y * Width + x) * 4;
            Bgra[o] = Bgra[o + 1] = Bgra[o + 2] = (byte)fill; Bgra[o + 3] = (byte)alpha;
        }
    }
    /// <summary>Pixel width of a label laid out glyph by glyph, outline included.</summary>
    public int Measure(string text) => text.Sum(c => Glyphs.TryGetValue(c, out var g) ? g.Advance : 0) + 2 * Pad;

    [StructLayout(LayoutKind.Sequential)] private struct Size32 { public int Cx, Cy; }
    [DllImport("gdi32.dll")] private static extern nint CreateCompatibleDC(nint dc);
    [DllImport("gdi32.dll")] private static extern nint CreateDIBSection(nint dc, int* info, uint usage, nint* bits, nint section, uint offset);
    [DllImport("gdi32.dll")] private static extern nint SelectObject(nint dc, nint item);
    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)] private static extern nint CreateFontW(int height, int width, int escapement, int orientation,
        int weight, uint italic, uint underline, uint strike, uint charset, uint precision, uint clip, uint quality, uint pitch, string face);
    [DllImport("gdi32.dll")] private static extern uint SetTextColor(nint dc, uint color);
    [DllImport("gdi32.dll")] private static extern int SetBkMode(nint dc, int mode);
    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)] private static extern bool GetTextExtentPoint32W(nint dc, string text, int count, out Size32 size);
    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)] private static extern bool TextOutW(nint dc, int x, int y, string text, int count);
    [DllImport("gdi32.dll")] private static extern bool GdiFlush();
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(nint item);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(nint dc);
}
