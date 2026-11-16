namespace DotaCoach.TwoPc;

/// <summary>Immutable, tightly packed NV12 picture. JPEG conversion belongs to the coach's consumers.</summary>
public sealed class VideoPixels
{
    internal byte[] Nv12 { get; }
    public int Width { get; }
    public int Height { get; }
    public VideoPixels(int width, int height, byte[] nv12)
    {
        ValidateSize(width, height);
        if (nv12.Length != width * height * 3 / 2) throw new ArgumentException("Invalid NV12 size");
        Width = width; Height = height; Nv12 = nv12.ToArray();
    }
    public static void ValidateSize(int width, int height)
    {
        if (width is < 16 or > 1600 || height is < 16 or > 900 || (width & 1) != 0 || (height & 1) != 0)
            throw new ArgumentException("Video requires bounded even dimensions");
    }
    public static (int Width, int Height) SizeFor(int width, int height)
    {
        var size = DisplayGeometry.EncodedSize(width, height);
        return (size.Width & ~1, size.Height & ~1);
    }
    private static byte Clamp(int value) => (byte)Math.Clamp(value, 0, 255);
    public static VideoPixels FromBgra(int width, int height, byte[] bgra)
    {
        ValidateSize(width, height);
        if (bgra.Length != width * height * 4) throw new ArgumentException("Invalid BGRA size");
        int plane = width * height;
        var output = new byte[plane * 3 / 2];
        for (int y = 0; y < height; y += 2)
        for (int x = 0; x < width; x += 2) {
            int r = 0, g = 0, b = 0;
            for (int dy = 0; dy < 2; dy++)
            for (int dx = 0; dx < 2; dx++) {
                int p = (y + dy) * width + x + dx, i = p * 4;
                int blue = bgra[i], green = bgra[i + 1], red = bgra[i + 2];
                output[p] = Clamp(((66 * red + 129 * green + 25 * blue + 128) >> 8) + 16);
                r += red; g += green; b += blue;
            }
            r /= 4; g /= 4; b /= 4;
            int uv = plane + y / 2 * width + x;
            output[uv] = Clamp(((-38 * r - 74 * g + 112 * b + 128) >> 8) + 128);
            output[uv + 1] = Clamp(((112 * r - 94 * g - 18 * b + 128) >> 8) + 128);
        }
        return new(width, height, output);
    }
    public byte[] ToBgra()
    {
        var result = new byte[Width * Height * 4];
        int plane = Width * Height;
        for (int y = 0; y < Height; y++)
        for (int x = 0; x < Width; x++) {
            int p = y * Width + x, uv = plane + y / 2 * Width + (x & ~1);
            int c = Nv12[p] - 16, d = Nv12[uv] - 128, e = Nv12[uv + 1] - 128, i = p * 4;
            result[i] = Clamp((298 * c + 516 * d + 128) >> 8);
            result[i + 1] = Clamp((298 * c - 100 * d - 208 * e + 128) >> 8);
            result[i + 2] = Clamp((298 * c + 409 * e + 128) >> 8);
            result[i + 3] = 255;
        }
        return result;
    }
}
