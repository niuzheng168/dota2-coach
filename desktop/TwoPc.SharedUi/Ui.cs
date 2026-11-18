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
    public static TextBlock Label(string text) => new() {
        Text = text, FontSize = 12, Foreground = Brushes.LightGray, TextWrapping = TextWrapping.Wrap,
        Margin = new Thickness(0, 6, 0, 3),
    };
    /// <summary>Collapsed-by-default group for settings that are rarely touched.</summary>
    public static Expander Section(string header, UIElement content, bool expanded = false) => new() {
        Header = header, Content = new Border { Child = content, Padding = new Thickness(14, 6, 0, 4) },
        Foreground = Brushes.White, FontSize = 14, IsExpanded = expanded, Margin = new Thickness(0, 2, 0, 6),
    };
    /// <summary>One stretching element followed by fixed-width ones (e.g. input + button).</summary>
    public static Grid Row(FrameworkElement fill, params FrameworkElement[] fixedItems)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        foreach (var _ in fixedItems) grid.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        Place(grid, [fill, .. fixedItems]);
        return grid;
    }
    /// <summary>Equal-width columns.</summary>
    public static Grid Columns(params FrameworkElement[] items)
    {
        var grid = new Grid();
        foreach (var _ in items) grid.ColumnDefinitions.Add(new());
        Place(grid, items);
        return grid;
    }
    private static void Place(Grid grid, FrameworkElement[] items)
    {
        for (int i = 0; i < items.Length; i++) {
            if (i < items.Length - 1) items[i].Margin = new Thickness(items[i].Margin.Left, items[i].Margin.Top, 8, items[i].Margin.Bottom);
            Grid.SetColumn(items[i], i); grid.Children.Add(items[i]);
        }
    }
    public static TextBox Input(string value = "") => new() {
        Text = value, FontSize = 14, Padding = new Thickness(8), Margin = new Thickness(0, 0, 0, 8),
        Background = Panel, Foreground = Brushes.White, BorderBrush = Brushes.SlateGray,
    };
    public static Button Button(string text, RoutedEventHandler click) {
        var b = new Button { Content = text, Padding = new Thickness(12, 9, 12, 9), Margin = new Thickness(0, 3, 8, 7),
            FontSize = 14, Background = Panel, Foreground = Brushes.White, BorderBrush = Brushes.SlateGray };
        b.Click += click; return b;
    }
    public static BitmapImage Image(byte[] bytes) {
        using var stream = new MemoryStream(bytes);
        var result = new BitmapImage(); result.BeginInit(); result.CacheOption = BitmapCacheOption.OnLoad;
        result.StreamSource = stream; result.EndInit(); result.Freeze(); return result;
    }
    public static ComboBox Choice() => new() { FontSize = 13, Padding = new Thickness(5), Margin = new Thickness(0, 0, 0, 8) };
    public static BitmapSource VideoImage(VideoPixels frame) => VideoImage(frame, frame.Width, frame.Height);
    public static BitmapSource VideoImage(VideoPixels frame, int width, int height)
    {
        var image = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null,
            frame.ToBgra(width, height), width * 4);
        image.Freeze(); return image;
    }
    /// <summary>Largest size within the box that keeps the frame's aspect ratio; never upscales.</summary>
    public static (int Width, int Height) Fit(int width, int height, double maxWidth, double maxHeight)
    {
        double scale = Math.Min(1, Math.Min(maxWidth / width, maxHeight / height));
        return (Math.Max(16, (int)(width * scale)), Math.Max(16, (int)(height * scale)));
    }
    public static BitmapSource PreviewImage(VideoPixels frame, double maxWidth, double maxHeight)
    {
        var size = Fit(frame.Width, frame.Height, maxWidth, maxHeight);
        return VideoImage(frame, size.Width, size.Height);
    }
    public static BitmapSource FrameImage(FrameView frame) => frame.Video is { } video ? VideoImage(video) : Image(frame.Jpeg);
    /// <summary>Model/Debug JPEG: scaled on the coach to at most 1600×900, whatever resolution the game machine sent.</summary>
    public static byte[] ExtractJpeg(VideoPixels frame)
    {
        var size = Fit(frame.Width, frame.Height, 1600, 900);
        var image = BitmapFrame.Create(VideoImage(frame, size.Width, size.Height));
        foreach (int quality in new[] { 85, 70, 55, 40 }) {
            var encoder = new JpegBitmapEncoder { QualityLevel = quality };
            encoder.Frames.Add(image);
            using var output = new MemoryStream(); encoder.Save(output);
            if (output.Length > Wire.MaxFrameBytes) continue;
            var data = output.ToArray(); Jpeg.Dimensions(data); return data;
        }
        throw new InvalidOperationException("Selected video image exceeds the JPEG budget");
    }
    public static string LaunchOptionsText(LaunchOptionsInfo info) =>
        !info.Found ? "启动参数：没读到 Steam 里的 Dota 启动选项，请确认含 -gamestateintegration。" :
        info.HasGsiFlag ? "启动参数已含 -gamestateintegration" + (info.ActiveAccount ? "" : "（Steam 未登录，读的是最近使用的账号）") :
        "启动参数缺少 -gamestateintegration：点“复制启动参数”，粘贴到 Steam → Dota 2 → 属性 → 启动选项，再重启 Dota。";
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
