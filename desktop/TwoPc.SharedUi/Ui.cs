using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace DotaCoach.TwoPc;

internal static class Ui
{
    public static readonly Brush Background = new SolidColorBrush(Color.FromRgb(15, 21, 30));
    public static readonly Brush Panel = new SolidColorBrush(Color.FromRgb(25, 35, 48));
    public static readonly Brush Accent = new SolidColorBrush(Color.FromRgb(89, 221, 188));
    public static TextBlock Text(string text, double size = 14, Brush? color = null) => new() {
        Text = text, FontSize = size, Foreground = color ?? Brushes.White, TextWrapping = TextWrapping.Wrap,
        Margin = new Thickness(0, 4, 0, 8),
    };
    public static TextBox Input(string value = "") => new() {
        Text = value, FontSize = 14, Padding = new Thickness(8), Margin = new Thickness(0, 0, 0, 8),
        Background = Panel, Foreground = Brushes.White, BorderBrush = Brushes.SlateGray,
    };
    public static Button Button(string text, RoutedEventHandler click) {
        var b = new Button { Content = text, Padding = new Thickness(12, 9, 12, 9), Margin = new Thickness(0, 3, 8, 7),
            FontSize = 14, Background = Panel, Foreground = Brushes.White, BorderBrush = Brushes.SlateGray };
        b.Click += click; return b;
    }
    public static Border Card(UIElement content) => new() {
        Child = content, Background = Panel, Padding = new Thickness(16), CornerRadius = new CornerRadius(10),
        Margin = new Thickness(0, 0, 0, 12),
    };
    public static BitmapImage Image(byte[] bytes) {
        using var stream = new MemoryStream(bytes);
        var result = new BitmapImage(); result.BeginInit(); result.CacheOption = BitmapCacheOption.OnLoad;
        result.StreamSource = stream; result.EndInit(); result.Freeze(); return result;
    }
    public static BitmapSource VideoImage(VideoPixels frame)
    {
        var image = BitmapSource.Create(frame.Width, frame.Height, 96, 96, PixelFormats.Bgra32, null,
            frame.ToBgra(), frame.Width * 4);
        image.Freeze(); return image;
    }
    public static BitmapSource FrameImage(FrameView frame) => frame.Video is { } video ? VideoImage(video) : Image(frame.Jpeg);
    public static byte[] ExtractJpeg(VideoPixels frame)
    {
        var image = BitmapFrame.Create(VideoImage(frame));
        foreach (int quality in new[] { 85, 70, 55, 40 }) {
            var encoder = new JpegBitmapEncoder { QualityLevel = quality };
            encoder.Frames.Add(image);
            using var output = new MemoryStream(); encoder.Save(output);
            if (output.Length > Wire.MaxFrameBytes) continue;
            var data = output.ToArray(); Jpeg.Dimensions(data); return data;
        }
        throw new InvalidOperationException("Selected video image exceeds the JPEG budget");
    }
    public static string LaunchOptionsText(LaunchOptionsInfo info) => (!info.Found
        ? "启动参数：没读到 Steam 里的 Dota 启动选项；请确认其中含 -gamestateintegration。"
        : info.HasGsiFlag ? $"启动参数：已含 -gamestateintegration（{info.Options}）。"
        : "启动参数：缺少 -gamestateintegration。点“复制启动参数”，粘贴到 Steam → Dota 2 → 属性 → 启动选项，再重启 Dota。") +
        (info.Found && !info.ActiveAccount ? "\n（Steam 未登录，读的是最近使用的账号；Steam 可能稍后才写盘，以实际收到数据为准）" : "");
    /// <summary>Copies the user's own options plus the GSI flag; Steam's files are never edited.</summary>
    public static string CopyLaunchOptions(LaunchOptionsInfo info)
    {
        string text = SteamLaunch.WithGsiFlag(info.Options);
        try { Clipboard.SetText(text); }
        catch { return "无法写入剪贴板：请在 Dota 启动选项末尾手动加上 -gamestateintegration。"; }
        return $"已复制：{text}\n粘贴到 Steam → Dota 2 → 属性 → 启动选项（替换原内容），然后完全退出并重启 Dota。";
    }
    public static Rect ImageRect(double width, double height, double sourceWidth, double sourceHeight) {
        double scale = Math.Min(width / sourceWidth, height / sourceHeight);
        double w = sourceWidth * scale, h = sourceHeight * scale;
        return new Rect((width - w) / 2, (height - h) / 2, w, h);
    }
}
