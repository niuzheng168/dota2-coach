using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace DotaCoach.TwoPc;

internal partial class MatchReviewPane : UserControl
{
    private MatchReviewText formatter = null!;
    private bool isReplay;
    private int page;
    private ReviewLane? lane;
    private double cursor;
    private ReviewFilter filter;
    private string[] slotLabels = [];
    private bool changingSlot;

    public MatchReviewPane()
    {
        InitializeComponent();
        Older.Click += (_, _) => { page++; Draw(); };
        Newer.Click += (_, _) => { page--; Draw(); };
        Events.SelectionChanged += (_, _) => DetailText.Text = (Events.SelectedItem as ReviewRow)?.Detail ?? "";
        SnapshotBox.Expanded += (_, _) => ShowSnapshot();
        HeroSlot.SelectionChanged += (_, _) => { if (isReplay && !changingSlot && SnapshotBox.IsExpanded) ShowSnapshot(); };
        SizeChanged += (_, _) => {
            bool compact = ActualHeight < 370, tight = ActualHeight < 320;
            Sketch.Width = Sketch.Height = tight ? 44 : compact ? 55 : 99;
            SnapshotText.MaxHeight = tight ? 44 : compact ? 65 : 115;
            DetailText.Height = tight ? 36 : compact ? 42 : 83;
            Explanation.Visibility = SketchLegend.Visibility = tight ? Visibility.Collapsed : Visibility.Visible;
            FrameCaption.TextWrapping = tight ? TextWrapping.NoWrap : TextWrapping.Wrap;
            FrameCaption.TextTrimming = tight ? TextTrimming.CharacterEllipsis : TextTrimming.None;
        };
    }

    internal void Configure(MatchReviewText text, bool replay)
    {
        formatter = text; isReplay = replay;
        Heading.Text = replay ? "录像全图" : "当时已知";
        if (replay) Heading.Foreground = Brushes.Wheat;
        Explanation.Text = replay ? "赛后证据 · 按发生时间排列，不代表队伍当时可见。" :
            "队伍视角 · 按获知时间排列；箭头表示「发生 → 获知」。";
        if (replay) {
            SnapshotBox.Header = "十人位置与装备 · 游标处坐标示意";
            SketchBox.Visibility = RosterPicker.Visibility = Visibility.Visible;
            SnapshotBox.IsExpanded = true;
        }
    }

    internal void Show(ReviewLane? source, double at, ReviewFilter selected)
    {
        lane = source; cursor = at; filter = selected; page = 0;
        Draw();
        if (SnapshotBox.IsExpanded) ShowSnapshot();
        else { SnapshotText.Clear(); Sketch.Set([], -1); FrameCaption.Text = ""; SetSlots([], -1); }
    }

    private void ShowSnapshot()
    {
        if (isReplay && lane != null) {
            int focus = Math.Max(-1, HeroSlot.SelectedIndex - 1);
            var roster = formatter.Roster(lane, cursor, focus);
            SetSlots(roster.SlotLabels, focus + 1);
            SnapshotText.Text = roster.Text; FrameCaption.Text = roster.Caption;
            Sketch.Set(roster.Dots, focus);
        } else {
            SnapshotText.Text = !isReplay && lane != null ? formatter.Snapshot(lane, cursor) : "";
            FrameCaption.Text = ""; Sketch.Set([], -1); SetSlots([], -1);
        }
    }

    private void SetSlots(string[] names, int selected)
    {
        if (names.SequenceEqual(slotLabels)) return;
        changingSlot = true;
        slotLabels = names; HeroSlot.ItemsSource = names; HeroSlot.SelectedIndex = selected;
        changingSlot = false;
    }

    private void Draw()
    {
        var slice = lane?.Page(cursor, filter, page);
        page = slice?.Number ?? 0;
        Newer.IsEnabled = page > 0; Older.IsEnabled = slice != null && page + 1 < slice.Pages;
        PageText.Text = slice is { Count: > 0 } ? $"已到达 {slice.Count} 条 · 第 {page + 1}/{slice.Pages} 页" : "0 条";
        var rows = slice?.Rows.Select(r => formatter.Row(r.Fact)).ToArray() ?? Array.Empty<ReviewRow>();
        DetailText.Clear();
        Events.ItemsSource = rows;
        EmptyText.Visibility = rows.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (rows.Length != 0) {
            Events.SelectedIndex = 0;
            // WPF can preserve the first selected record by value across ItemsSource replacements, without an event.
            DetailText.Text = rows[0].Detail;
            Events.ScrollIntoView(rows[0]);
        }
    }
}
