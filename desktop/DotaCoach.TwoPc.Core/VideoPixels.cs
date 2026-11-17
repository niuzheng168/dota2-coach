namespace DotaCoach.TwoPc;

/// <summary>Immutable, tightly packed NV12 picture. JPEG conversion belongs to the coach's consumers.</summary>
public sealed class VideoPixels
{
    // H.264 level 5.2 frame-size ceiling (36864 macroblocks): native 4K, 4096×2160 and below.
    public const int MaxWidth = 4096, MaxHeight = 2304;
    internal byte[] Nv12 { get; }
    public int Width { get; }
    public int Height { get; }
    public VideoPixels(int width, int height, byte[] nv12) : this(width, height, nv12, copy: true) { }
    private VideoPixels(int width, int height, byte[] nv12, bool copy)
    {
        ValidateSize(width, height);
        if (nv12.Length != width * height * 3 / 2) throw new ArgumentException("Invalid NV12 size");
        Width = width; Height = height; Nv12 = copy ? nv12.ToArray() : nv12;
    }
    /// <summary>Takes ownership of a buffer nobody else writes (decoder/scaler output): no extra copy per frame.</summary>
    internal static VideoPixels Wrap(int width, int height, byte[] nv12) => new(width, height, nv12, copy: false);
    public static void ValidateSize(int width, int height)
    {
        if (width is < 16 or > MaxWidth || height is < 16 or > MaxHeight || (width & 1) != 0 || (height & 1) != 0)
            throw new ArgumentException("Video requires bounded even dimensions");
    }
    /// <summary>Largest even size within <paramref name="maxWidth"/>×<paramref name="maxHeight"/> keeping the source aspect.</summary>
    public static (int Width, int Height) SizeFor(int width, int height, int maxWidth = 1600, int maxHeight = 900)
    {
        if (width is < 640 or > 7680 || height is < 480 or > 4320) throw new ArgumentException("Unsupported game surface");
        double scale = Math.Min(1, Math.Min((double)Math.Min(maxWidth, MaxWidth) / width, (double)Math.Min(maxHeight, MaxHeight) / height));
        return ((int)(width * scale) & ~1, (int)(height * scale) & ~1);
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
        return new(width, height, output, copy: false);
    }
    public byte[] ToBgra() => ToBgra(Width, Height);
    /// <summary>
    /// BGRA at <paramref name="width"/>×<paramref name="height"/>. Downscaling averages a 2×2 sample grid
    /// inside each output pixel, which keeps HUD text legible in previews and model JPEGs.
    /// </summary>
    public byte[] ToBgra(int width, int height)
    {
        if (width < 1 || height < 1 || width > Width || height > Height) throw new ArgumentException("Only same-size or downscaled output");
        var result = GC.AllocateUninitializedArray<byte>(width * height * 4);
        int plane = Width * Height;
        bool same = width == Width && height == Height;
        int samples = same ? 1 : 2;
        // Precomputed source columns/rows for each sub-sample.
        var xs = new int[width * samples];
        for (int x = 0; x < width; x++)
        for (int s = 0; s < samples; s++)
            xs[x * samples + s] = same ? x : Math.Min(Width - 1, (int)((x + .25 + .5 * s) * Width / width));
        Parallel.For(0, height, y => {
            Span<int> rows = stackalloc int[2];
            for (int s = 0; s < samples; s++) rows[s] = same ? y : Math.Min(Height - 1, (int)((y + .25 + .5 * s) * Height / height));
            int output = y * width * 4;
            for (int x = 0; x < width; x++, output += 4) {
                int r = 0, g = 0, b = 0;
                for (int sy = 0; sy < samples; sy++) {
                    int row = rows[sy], luma = row * Width, chroma = plane + row / 2 * Width;
                    for (int sx = 0; sx < samples; sx++) {
                        int column = xs[x * samples + sx], uv = chroma + (column & ~1);
                        int c = Nv12[luma + column] - 16, d = Nv12[uv] - 128, e = Nv12[uv + 1] - 128;
                        b += Clamp((298 * c + 516 * d + 128) >> 8);
                        g += Clamp((298 * c - 100 * d - 208 * e + 128) >> 8);
                        r += Clamp((298 * c + 409 * e + 128) >> 8);
                    }
                }
                int count = samples * samples, half = count / 2;
                result[output] = (byte)((b + half) / count);
                result[output + 1] = (byte)((g + half) / count);
                result[output + 2] = (byte)((r + half) / count);
                result[output + 3] = 255;
            }
        });
        return result;
    }
}
