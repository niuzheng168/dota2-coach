using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace DotaCoach.TwoPc;

/// <summary>
/// A client window's two pages: the home page with the status and everyday switches, and the settings page with
/// one group at a time chosen on the left. Hidden pages and groups stay in the window (collapsed, never removed),
/// so their controls keep working and UI tests reach them. Tests find the parts by name: HomePage, SettingsPage,
/// SettingsGroup (Tag = group name) and SettingsTab.
/// </summary>
internal sealed class AppPages : Grid
{
    public const string SettingsLabel = "设置", HomeLabel = "返回主页";
    private readonly Border home = new() { Name = "HomePage" };
    private readonly Grid settings = new() { Name = "SettingsPage", Visibility = Visibility.Collapsed };
    private readonly StackPanel tabs = new();
    private readonly Grid groupHost = new();
    private readonly List<(string Name, Button Tab, FrameworkElement Body)> groups = [];
    private readonly Button toggle;
    /// <summary>After every switch of page or group.</summary>
    public event Action? Changed;
    public bool SettingsShown => settings.Visibility == Visibility.Visible;
    public string Group { get; private set; } = "";
    public IReadOnlyList<string> Groups => groups.Select(g => g.Name).ToArray();

    public AppPages(string title, UIElement homeContent)
    {
        RowDefinitions.Add(new() { Height = GridLength.Auto });
        RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) });
        toggle = Ui.Button(SettingsLabel, (_, _) => Show(!SettingsShown));
        toggle.MinWidth = 110; toggle.VerticalAlignment = VerticalAlignment.Center;
        Children.Add(Ui.Row(Ui.Text(title, 26, Ui.Accent), toggle));
        home.Child = homeContent;
        SetRow(home, 1); Children.Add(home);
        settings.ColumnDefinitions.Add(new() { Width = new GridLength(190) });
        settings.ColumnDefinitions.Add(new() { Width = new GridLength(20) });
        settings.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        settings.Children.Add(tabs);
        var scroll = new ScrollViewer { Content = groupHost, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        SetColumn(scroll, 2); settings.Children.Add(scroll);
        SetRow(settings, 1); Children.Add(settings);
    }

    /// <summary>A settings group: its name on the left, a heading, an optional one-line note and the controls.</summary>
    public void Add(string name, UIElement content, string? note = null)
    {
        var body = new StackPanel { Name = "SettingsGroup", Tag = name, Visibility = Visibility.Collapsed };
        body.Children.Add(Ui.Text(name, 20, Ui.Accent));
        if (note != null) body.Children.Add(Ui.Text(note, 12, Brushes.LightGray));
        body.Children.Add(content);
        groupHost.Children.Add(body);
        var tab = Ui.Button(name, (_, _) => Show(true, name));
        tab.Tag = "SettingsTab"; tab.HorizontalContentAlignment = HorizontalAlignment.Left;
        tab.Margin = new Thickness(0, 0, 0, 6);
        tabs.Children.Add(tab);
        groups.Add((name, tab, body));
        if (groups.Count == 1) Select(name);
    }

    public void Show(bool settingsPage, string? group = null)
    {
        if (group != null) Select(group);
        home.Visibility = settingsPage ? Visibility.Collapsed : Visibility.Visible;
        settings.Visibility = settingsPage ? Visibility.Visible : Visibility.Collapsed;
        toggle.Content = settingsPage ? HomeLabel : SettingsLabel;
        Changed?.Invoke();
    }

    private void Select(string name)
    {
        if (!groups.Any(g => g.Name == name)) throw new ArgumentException("No settings group " + name);
        Group = name;
        foreach (var (n, tab, body) in groups) {
            bool on = n == name;
            body.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
            tab.Background = on ? Ui.Accent : Ui.Panel;
            tab.Foreground = on ? Ui.Background : Brushes.White;
        }
    }
}
