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
    public static Rect ImageRect(double width, double height, double sourceWidth, double sourceHeight) {
        double scale = Math.Min(width / sourceWidth, height / sourceHeight);
        double w = sourceWidth * scale, h = sourceHeight * scale;
        return new Rect((width - w) / 2, (height - h) / 2, w, h);
    }
}
