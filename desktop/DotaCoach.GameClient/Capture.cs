using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.ComponentModel;
using DotaCoach.Desktop;

namespace DotaCoach.TwoPc;

internal sealed record CaptureResult(byte[] Jpeg, int Width, int Height);
internal static class Capture
{
    public static CaptureResult? Game(GameSurface surface, bool includeLayeredEvidence = false)
    {
        if (GameWindow.Foreground() != surface || surface.Width > 7680 || surface.Height > 4320) return null;
        using var full = new Bitmap(surface.Width, surface.Height, PixelFormat.Format24bppRgb);
        using (var g = Graphics.FromImage(full))
        {
            if (!includeLayeredEvidence)
                g.CopyFromScreen(surface.Left, surface.Top, 0, 0, full.Size, CopyPixelOperation.SourceCopy);
            else
            {
                // Graphics.CopyFromScreen rejects combined ROP enum values; use the underlying
                // documented BitBlt operation for this explicit local-bot evidence mode only.
                IntPtr screen = GetDC(IntPtr.Zero), target = g.GetHdc();
                try {
                    if (screen == IntPtr.Zero || !BitBlt(target, 0, 0, surface.Width, surface.Height,
                        screen, surface.Left, surface.Top, 0x00CC0020u | 0x40000000u))
                        throw new Win32Exception(Marshal.GetLastWin32Error());
                }
                finally { g.ReleaseHdc(target); if (screen != IntPtr.Zero) ReleaseDC(IntPtr.Zero, screen); }
            }
        }
        if (GameWindow.Foreground() != surface) return null;
        // This first version uses foreground-guarded screen capture, not isolated WGC.
        // System notifications/overlapping windows can appear: disclose before enabling.
        return new(Encode(full), surface.Width, surface.Height);
    }
    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr window);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr window, IntPtr dc);
    [DllImport("gdi32.dll", SetLastError = true)] private static extern bool BitBlt(
        IntPtr target, int x, int y, int width, int height, IntPtr source, int sourceX, int sourceY, uint operation);
    public static CaptureResult Diagnostic(long sequence)
    {
        using var image = new Bitmap(1280, 720, PixelFormat.Format24bppRgb);
        using var g = Graphics.FromImage(image);
        g.Clear(Color.FromArgb(13, 23, 36)); g.SmoothingMode = SmoothingMode.AntiAlias;
        using var title = new Font("Microsoft YaHei UI", 28);
        using var text = new Font("Microsoft YaHei UI", 18);
        using var line = new Pen(Color.FromArgb(32, 58, 76), 1);
        for (int x = 0; x < 1280; x += 80) g.DrawLine(line, x, 0, x, 720);
        for (int y = 0; y < 720; y += 80) g.DrawLine(line, 0, y, 1280, y);
        g.DrawString("双机传输诊断 · 非游戏画面", title, Brushes.White, 44, 35);
        g.DrawString($"帧 #{sequence}  ·  本机生成  ·  不采集桌面、不调用模型", text, Brushes.LightGray, 48, 100);
        g.FillRectangle(Brushes.DarkSlateGray, 36, 436, 240, 240);
        g.DrawString("标记测试区", text, Brushes.White, 54, 446);
        float x1 = 70 + (sequence * 13 % 150), y1 = 535;
        g.FillEllipse(Brushes.Aquamarine, x1, y1, 24, 24);
        g.DrawString("请在教练端点击左下测试区，回传一条测试字幕。", text, Brushes.Aquamarine, 340, 390);
        g.DrawString("真正游戏采集：切换来源后重新连接，并切回 Dota 2。", text, Brushes.LightGray, 340, 440);
        return new(Encode(image), 1280, 720);
    }
    private static byte[] Encode(Bitmap full)
    {
        double scale = Math.Min(1, Math.Min(1280.0 / full.Width, 900.0 / full.Height));
        using var reduced = new Bitmap((int)(full.Width * scale), (int)(full.Height * scale), PixelFormat.Format24bppRgb);
        using (var g = Graphics.FromImage(reduced)) {
            g.InterpolationMode = InterpolationMode.HighQualityBilinear;
            g.DrawImage(full, new Rectangle(0, 0, reduced.Width, reduced.Height));
        }
        using var output = new MemoryStream();
        using var parameters = new EncoderParameters(1);
        parameters.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, 65L);
        var codec = ImageCodecInfo.GetImageEncoders().Single(x => x.FormatID == ImageFormat.Jpeg.Guid);
        reduced.Save(output, codec, parameters);
        if (output.Length > Wire.MaxFrameBytes) throw new InvalidOperationException("画面过大，未发送。");
        return output.ToArray();
    }
}
