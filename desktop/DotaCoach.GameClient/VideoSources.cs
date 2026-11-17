using System;
using DotaCoach.Desktop;

namespace DotaCoach.TwoPc;

/// <summary>Picture source for one streaming session. Created and used on the capture thread only.</summary>
internal interface IVideoSource : IDisposable
{
    string Name { get; }
    /// <summary>Game client-area pixels: the coordinate system of subtitles and minimap marks.</summary>
    int SourceWidth { get; }
    int SourceHeight { get; }
    /// <summary>Encoded picture size.</summary>
    int Width { get; }
    int Height { get; }
    /// <summary>A NEW picture, or null when nothing changed since the last call.</summary>
    VideoPixels? Grab();
    /// <summary>The window went away, changed size or (legacy mode) lost the foreground: restart the session.</summary>
    bool Ended { get; }
}

/// <summary>
/// Windows.Graphics.Capture of the Dota window → GPU crop/scale/NV12. Keeps working while Dota is behind other
/// windows; never includes other applications. The game machine's CPU does no per-pixel work.
/// </summary>
internal sealed class WindowVideoSource : IVideoSource
{
    private readonly GpuDevice gpu;
    private readonly WindowCapture capture;
    private readonly GpuNv12Scaler scaler;
    private readonly Rect32 client;
    private (int Width, int Height)? content;
    public string Name => capture.Unthrottled ? "窗口采集（GPU缩放）" : "窗口采集（GPU缩放；此Windows版本采集上限约60fps）";
    public int SourceWidth { get; }
    public int SourceHeight { get; }
    public int Width => scaler.Width;
    public int Height => scaler.Height;
    public bool Ended { get; private set; }
    public WindowVideoSource(GameSurface game, int width, int height, int fps)
    {
        SourceWidth = game.Width; SourceHeight = game.Height;
        var offset = GameWindow.ClientOffset(game) ?? throw new InvalidOperationException("Window bounds unavailable");
        gpu = new GpuDevice();
        try {
            capture = new WindowCapture(gpu, game.Handle);
            if (offset.Left + game.Width > capture.Width || offset.Top + game.Height > capture.Height)
                throw new InvalidOperationException("Client area outside the captured window");
            client = new Rect32 { Left = offset.Left, Top = offset.Top, Right = offset.Left + game.Width, Bottom = offset.Top + game.Height };
            scaler = new GpuNv12Scaler(gpu, capture.Width, capture.Height, width, height, fps);
        } catch { capture?.Dispose(); gpu.Dispose(); throw; }
    }
    public VideoPixels? Grab()
    {
        using var frame = capture.Latest();
        if (frame == null) return null;
        // A resized window gets a differently sized image: encoder and coordinates must restart.
        content ??= (frame.ContentWidth, frame.ContentHeight);
        if (content != (frame.ContentWidth, frame.ContentHeight) ||
            client.Right > frame.ContentWidth || client.Bottom > frame.ContentHeight) { Ended = true; return null; }
        return scaler.Convert(frame, client);
    }
    public void Dispose() { scaler.Dispose(); capture.Dispose(); gpu.Dispose(); }
}

/// <summary>v0.3 behaviour: GDI copy of the screen area, only while Dota is the foreground window.</summary>
internal sealed class ForegroundVideoSource(GameSurface game, int width, int height) : IVideoSource
{
    public string Name => "前台屏幕复制（CPU，切出即暂停）";
    public int SourceWidth => game.Width;
    public int SourceHeight => game.Height;
    public int Width => width;
    public int Height => height;
    public bool Ended { get; private set; }
    public VideoPixels? Grab()
    {
        var pixels = Capture.GameVideo(game, width, height);
        if (pixels == null) Ended = true;
        return pixels;
    }
    public void Dispose() { }
}

internal sealed class DiagnosticVideoSource : IVideoSource
{
    private long sequence;
    public string Name => "诊断画面（本机生成）";
    public int SourceWidth => 1280;
    public int SourceHeight => 720;
    public int Width => 1280;
    public int Height => 720;
    public bool Ended => false;
    public VideoPixels? Grab() => Capture.DiagnosticVideo(++sequence);
    public void Dispose() { }
}
