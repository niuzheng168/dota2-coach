using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;

namespace DotaCoach.Desktop;

internal sealed record GameSurface(IntPtr Handle, int Left, int Top, int Width, int Height);

internal static class GameWindow
{
    // Only the visible, foreground Dota client. Never capture another app after Alt+Tab.
    public static GameSurface? Foreground()
    {
        try
        {
            IntPtr handle = GetForegroundWindow();
            GetWindowThreadProcessId(handle, out uint pid);
            using var process = Process.GetProcessById((int)pid);
            if (!process.ProcessName.Equals("dota2", StringComparison.OrdinalIgnoreCase) || IsIconic(handle) ||
                !GetClientRect(handle, out var r) || r.Right < 640 || r.Bottom < 480) return null;
            var origin = new POINT();
            if (!ClientToScreen(handle, ref origin)) return null;
            return new(handle, origin.X, origin.Y, r.Right, r.Bottom);
        }
        catch { return null; }
    }

    private static IntPtr cachedHandle;
    private static uint cachedProcess;
    /// <summary>
    /// The Dota client window whether or not it is in front. Null when Dota is not running, minimised
    /// (<paramref name="minimized"/> = true; exclusive fullscreen minimises on Alt+Tab) or too small.
    /// </summary>
    public static GameSurface? Find(out bool minimized)
    {
        minimized = false;
        try
        {
            IntPtr handle = IntPtr.Zero;
            if (cachedHandle != IntPtr.Zero && IsWindow(cachedHandle) &&
                GetWindowThreadProcessId(cachedHandle, out uint owner) != 0 && owner == cachedProcess) handle = cachedHandle;
            else
            {
                foreach (var process in Process.GetProcessesByName("dota2"))
                    using (process)
                        if (handle == IntPtr.Zero && process.MainWindowHandle != IntPtr.Zero)
                        { handle = process.MainWindowHandle; cachedProcess = (uint)process.Id; }
                cachedHandle = handle;
            }
            if (handle == IntPtr.Zero || !IsWindowVisible(handle)) return null;
            if (IsIconic(handle)) { minimized = true; return null; }
            if (!GetClientRect(handle, out var r) || r.Right < 640 || r.Bottom < 480) return null;
            var origin = new POINT();
            if (!ClientToScreen(handle, ref origin)) return null;
            return new(handle, origin.X, origin.Y, r.Right, r.Bottom);
        }
        catch { return null; }
    }
    public static bool IsForeground(GameSurface game) => GetForegroundWindow() == game.Handle;
    /// <summary>
    /// Client-area rectangle inside a Windows.Graphics.Capture image of the window, which covers the DWM
    /// frame bounds (title bar/borders in windowed mode; nothing extra in borderless/fullscreen).
    /// </summary>
    public static (int Left, int Top)? ClientOffset(GameSurface game)
    {
        if (DwmGetWindowAttribute(game.Handle, 9 /* DWMWA_EXTENDED_FRAME_BOUNDS */, out var frame, 16) != 0) return null;
        return (Math.Max(0, game.Left - frame.Left), Math.Max(0, game.Top - frame.Top));
    }

    public static byte[]? CaptureHud(GameSurface game)
    {
        if (Foreground() != game) return null;
        using var screenshot = new Bitmap(game.Width, game.Height, PixelFormat.Format24bppRgb);
        using (var graphics = Graphics.FromImage(screenshot))
            graphics.CopyFromScreen(game.Left, game.Top, 0, 0, screenshot.Size, CopyPixelOperation.SourceCopy);
        if (Foreground() != game) return null;
        // Default-HUD crop, not a window-isolated WGC capture. Custom layouts and
        // system overlays still need validation; only these crops go to localhost.
        using var hud = new Bitmap(1200, 360, PixelFormat.Format24bppRgb);
        using (var graphics = Graphics.FromImage(hud))
        {
            graphics.Clear(Color.Black);
            graphics.DrawImage(screenshot, new Rectangle(490, 0, 220, 90),
                new Rectangle((int)(game.Width * .465), 0, (int)(game.Width * .07), (int)(game.Height * .065)), GraphicsUnit.Pixel);
            graphics.DrawImage(screenshot, new Rectangle(0, 100, 1200, 260),
                new Rectangle((int)(game.Width * .21), (int)(game.Height * .84), (int)(game.Width * .59), (int)(game.Height * .16)), GraphicsUnit.Pixel);
        }
        using var bytes = new MemoryStream(); hud.Save(bytes, ImageFormat.Jpeg); return bytes.ToArray();
    }

    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] private static extern bool GetClientRect(IntPtr h, out RECT rect);
    [DllImport("user32.dll")] private static extern bool ClientToScreen(IntPtr h, ref POINT point);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr h);
    [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr h);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr h);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr h, int attribute, out RECT value, int size);
}
