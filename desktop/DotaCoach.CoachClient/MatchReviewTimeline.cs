using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Threading;

namespace DotaCoach.TwoPc;

internal enum ReviewMode { Known, Replay, Compare }
internal enum ReviewFilter { All, State, Equipment, Vision, EstimatedWard, SeenWard, ReplayWard, Plan, Combat, Other }

// The review window has no reference to a mailbox, model, settings writer, or live publisher.
internal sealed record ReviewStore(Func<IReadOnlyList<MatchArchiveEntry>> List, Func<string, bool> IsSealed,
    Func<string, bool, IReadOnlyList<MatchFact>> Read, Action<string, string> Import, Func<bool>? CanAudit = null)
{
    internal const string LiveOnly = "仅实时显示：本机档案写入不可用，录像查看、下载和导入已锁定。请检查磁盘或权限后重启。";
    internal bool AuditReady => CanAudit?.Invoke() ?? true;
    public static ReviewStore From(MatchArchive archive, Func<bool> canAudit)
    {
        void RequireAudit() { if (!canAudit()) throw new InvalidOperationException(LiveOnly); }
        return new(archive.List, id => canAudit() && archive.IsSealed(id),
            (id, replay) => { if (replay) RequireAudit(); return archive.Read(id, replay); },
            (id, file) => { RequireAudit(); archive.ImportReplay(id, file); }, canAudit);
    }
}

internal readonly record struct ReviewPoint(double At, MatchFact Fact, ReviewFilter Group);
internal sealed record ReviewPage(ReviewPoint[] Rows, int Number, int Pages, int Count);
internal sealed record ReviewHeroFrame(double Clock, long Tick, MatchFact?[] Slots);

/// <summary>A seekable, immutable index. Pages and as-of snapshots are slices of the very same eligible prefix.</summary>
internal sealed class ReviewLane
{
    public const int PageSize = 32;
    private readonly Dictionary<ReviewFilter, ReviewPoint[]> groups;
    private readonly Dictionary<string, ReviewPoint[]> snapshots;
    private readonly ReviewHeroFrame[] frames;
    private static readonly ReviewPoint[] Empty = [];

    public ReviewLane(List<ReviewPoint> events)
    {
        var ordered = events.OrderBy(p => p.At).ThenBy(p => p.Fact.Sequence).ToArray();
        groups = ordered.GroupBy(p => p.Group).ToDictionary(g => g.Key, g => g.ToArray());
        groups[ReviewFilter.All] = ordered;
        // The worker stores the actual inventory on each sampled hero, not in a separate "item" event.
        if (ordered.Any(p => p.Fact.Kind == "replay-hero"))
            groups[ReviewFilter.Equipment] = ordered.Where(p => p.Group == ReviewFilter.Equipment || p.Fact.Kind == "replay-hero").ToArray();
        snapshots = ordered.Where(p => p.Fact.Kind is "equipment" or "ward-estimate" or "ward-observed" or "gap" or "own-state" or "replay-ward")
            .GroupBy(p => p.Fact.Kind).ToDictionary(g => g.Key, g => g.ToArray());
        var sampled = new List<ReviewHeroFrame>();
        foreach (var p in ordered.Where(p => p.Fact.Kind == "replay-hero")) {
            var d = p.Fact.Data;
            if (d.ValueKind != JsonValueKind.Object || !d.TryGetProperty("tick", out var t) ||
                t.ValueKind != JsonValueKind.Number || !t.TryGetInt64(out long tick) ||
                !d.TryGetProperty("playerId", out var id) || id.ValueKind != JsonValueKind.Number ||
                !id.TryGetInt32(out int slot) || slot is < 0 or >= 10) continue;
            // One packet, NOT ten independent histories: missing/unknown slots never inherit data from older frames.
            if (sampled.Count == 0 || sampled[^1].Clock != p.At || sampled[^1].Tick != tick)
                sampled.Add(new(p.At, tick, new MatchFact?[10]));
            sampled[^1].Slots[slot] = p.Fact;
        }
        frames = sampled.ToArray();
    }

    public double Start => groups[ReviewFilter.All] is { Length: > 0 } a ? Math.Min(0, a[0].At) : 0;
    public double End => groups[ReviewFilter.All] is { Length: > 0 } a ? Math.Max(0, a[^1].At) : 0;
    public double? First => groups[ReviewFilter.All] is { Length: > 0 } a ? a[0].At : null;

    private static int Through(ReviewPoint[] rows, double cursor)
    {
        int lo = 0, hi = rows.Length;
        while (lo < hi) {
            int m = lo + (hi - lo) / 2;
            if (rows[m].At <= cursor) lo = m + 1; else hi = m;
        }
        return lo;
    }

    public ReviewPage Page(double cursor, ReviewFilter filter, int page)
    {
        var all = groups.GetValueOrDefault(filter, Empty);
        int count = Through(all, cursor), pages = Math.Max(1, (count + PageSize - 1) / PageSize);
        page = Math.Clamp(page, 0, pages - 1);
        int end = count - page * PageSize;
        var rows = new ReviewPoint[Math.Min(PageSize, end)];
        for (int i = 0; i < rows.Length; i++) rows[i] = all[end - 1 - i];
        return new(rows, page, pages, count);
    }

    public MatchFact? AsOf(string kind, double cursor) =>
        snapshots.TryGetValue(kind, out var rows) && Through(rows, cursor) is > 0 and var n ? rows[n - 1].Fact : null;

    public ReviewHeroFrame? HeroesAt(double cursor)
    {
        int lo = 0, hi = frames.Length;
        while (lo < hi) {
            int m = lo + (hi - lo) / 2;
            if (frames[m].Clock <= cursor) lo = m + 1; else hi = m;
        }
        return lo == 0 ? null : frames[lo - 1];
    }
}

internal sealed record MatchReviewTimeline(ReviewLane Known, ReviewLane Replay)
{
    public static readonly (ReviewFilter Key, string Label)[] Filters = [
        (ReviewFilter.All, "全部事件"), (ReviewFilter.State, "对局 / 英雄状态"), (ReviewFilter.Equipment, "等级 / 装备"),
        (ReviewFilter.Vision, "队伍视野 / 提醒"), (ReviewFilter.EstimatedWard, "眼位 · 推测 / 提醒"),
        (ReviewFilter.SeenWard, "眼位 · 队伍实际看见"), (ReviewFilter.ReplayWard, "眼位 · 录像记录"),
        (ReviewFilter.Plan, "教练决策"), (ReviewFilter.Combat, "击杀 / 重要目标"), (ReviewFilter.Other, "缺口 / 其他")
    ];

    internal static bool IsReplay(string source) => source == "postgame-replay";

    public static MatchReviewTimeline Build(string matchId, IReadOnlyList<MatchFact> facts, bool allowReplay, CancellationToken token)
    {
        // Do not truncate and then present an incomplete prefix as a complete history.
        if (facts.Count > 500_000) throw new InvalidOperationException("这场记录超过 50 万条的查看上限；未显示部分历史。");
        var known = new List<ReviewPoint>(); var replay = new List<ReviewPoint>();
        foreach (var f in facts) {
            token.ThrowIfCancellationRequested();
            if (f.MatchId != matchId || !ClockInRange(f.Clock)) continue;
            if (IsReplay(f.Source)) {
                if (allowReplay && f.Kind.StartsWith("replay-", StringComparison.Ordinal)) replay.Add(new(f.Clock, f, ReplayGroup(f.Kind)));
            } else if (KnownGroup(f.Source, f.Kind) is { } group && ClockInRange(f.KnownAt)) {
                // A late discovery cannot be backdated, nor can an early/malformed KnownAt reveal a future event.
                known.Add(new(Math.Max(f.Clock, f.KnownAt), f, group));
            }
        }
        return new(new(known), new(replay));
    }

    private static bool ClockInRange(double n) => double.IsFinite(n) && Math.Abs(n) <= 7 * 24 * 3600;
    private static ReviewFilter? KnownGroup(string source, string kind) => (source, kind) switch {
        ("own-gsi", "phase" or "own-state") => ReviewFilter.State,
        ("team-vision", "equipment" or "ward-charges") => ReviewFilter.Equipment,
        ("team-vision", "vision-alert" or "team-sight") => ReviewFilter.Vision,
        ("team-vision", "ward-observed") => ReviewFilter.SeenWard,
        ("inference", "ward-estimate" or "ward-alert") => ReviewFilter.EstimatedWard,
        ("coach", "farm-input" or "farm-decision") => ReviewFilter.Plan, ("coach", "gap") => ReviewFilter.Other,
        _ => null // Unrecognized provenance is never a fallback for the live-knowledge lane.
    };

    private static ReviewFilter ReplayGroup(string kind) =>
        kind.Contains("ward", StringComparison.Ordinal) ? ReviewFilter.ReplayWard :
        kind.Contains("item", StringComparison.Ordinal) || kind.Contains("equipment", StringComparison.Ordinal) ? ReviewFilter.Equipment :
        kind is "replay-kill" or "replay-objective" ? ReviewFilter.Combat :
        kind is "replay-hero" or "replay-end" ? ReviewFilter.State : ReviewFilter.Other;

    public (double Min, double Max) Range(ReviewMode mode) => mode switch {
        ReviewMode.Known => (Math.Floor(Known.Start), Math.Ceiling(Known.End)),
        ReviewMode.Replay => (Math.Floor(Replay.Start), Math.Ceiling(Replay.End)),
        _ => (Math.Floor(Math.Min(Known.Start, Replay.Start)), Math.Ceiling(Math.Max(Known.End, Replay.End)))
    };

    public static string Time(double seconds)
    {
        long ms = (long)Math.Round(Math.Abs(seconds) * 1000, MidpointRounding.AwayFromZero);
        return (seconds < 0 && ms != 0 ? "−" : "") + (ms / 60_000).ToString(CultureInfo.InvariantCulture) + ":" +
            ((ms / 1000) % 60).ToString("00", CultureInfo.InvariantCulture) + (ms % 1000 == 0 ? "" :
                "." + (ms % 1000).ToString("000", CultureInfo.InvariantCulture).TrimEnd('0'));
    }
}
