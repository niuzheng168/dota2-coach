using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace DotaCoach.TwoPc;

internal sealed record ReviewRow(string When, string Title, string Origin, string Preview, string Detail);
internal sealed record ReviewDot(double X, double Y, int? Team, string Label, bool Ward = false);
internal sealed record ReplayRoster(string Caption, string Text, ReviewDot[] Dots, string[] SlotLabels);

internal sealed class MatchReviewText(HeroNames names)
{
    private IReadOnlyDictionary<string, string> itemLabels = new Dictionary<string, string>();
    private static readonly Lazy<IReadOnlyDictionary<string, string>> ShippedItems = new(() => {
        var labels = new Dictionary<string, string>(StringComparer.Ordinal);
        try {
            var file = new FileInfo(Path.Combine(AppContext.BaseDirectory, "data", "dota2-dictionary.json"));
            if (!file.Exists || file.Length > 8 * 1024 * 1024) return labels;
            using var s = file.OpenRead(); using var doc = JsonDocument.Parse(s);
            foreach (string section in new[] { "shopItems", "neutralItems" }) {
                foreach (var row in Elements(Get(doc.RootElement, section), 4096)) {
                    string key = Val(row, "key", ""), zh = Val(row, "zh", "");
                    if (key.StartsWith("item_", StringComparison.Ordinal) && zh.Length > 0) labels[key] = zh;
                }
            }
        } catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { /* Internal names still work. */ }
        return labels;
    });
    // Called only by the background catalog reader. Formatting and scrubbing never load assets on the dispatcher.
    internal static IReadOnlyDictionary<string, string> ReadItemLabels() => ShippedItems.Value;
    internal void UseItemLabels(IReadOnlyDictionary<string, string> labels) => itemLabels = labels;
    private string ItemLabel(string key) => itemLabels.TryGetValue(key, out var zh) ? $"{zh} ({key})" : key;
    private static string Time(double n) => MatchReviewTimeline.Time(n);

    public ReviewRow Row(MatchFact f)
    {
        bool replay = MatchReviewTimeline.IsReplay(f.Source);
        string title = (f.Source, f.Kind) switch {
            ("own-gsi", "phase") => "比赛阶段", ("own-gsi", "own-state") => "本人状态",
            ("team-vision", "equipment") => "最后看见的等级 / 装备", ("team-vision", "vision-alert") => "队伍视野提醒",
            ("team-vision", "team-sight") => "队伍视野中的英雄",
            ("team-vision", "ward-charges") => "守卫携带量 · 装备观察，不是放置",
            ("team-vision", "ward-observed") => "实际看见眼 · 不是放置记录",
            ("inference", "ward-estimate") => "疑似眼位 · 推测", ("inference", "ward-alert") => "疑似眼位提醒 · 推测",
            ("coach", "farm-input") => "分钟路线 · 模型输入快照",
            ("coach", "farm-decision") => "分钟路线 · 决策记录", ("coach", "gap") => "记录缺口",
            _ when replay && f.Kind == "replay-ward" => Val(f.Data, "action") switch {
                "placed" => "眼位放置 · 录像首次记录存活实体", "removed" => "眼位移除 · 录像实体证据", _ => "眼位 · 录像证据" },
            _ when replay && f.Kind == "replay-hero" => "英雄 · 录像采样",
            _ when replay && f.Kind == "replay-kill" => "英雄击杀 · 录像",
            _ when replay && f.Kind == "replay-objective" => "重要目标 · 录像",
            _ when replay && f.Kind == "replay-end" => "录像终局",
            _ => Clean(f.Kind, 70)
        };
        string body = Body(f), preview = body.Split('\n', 2)[0];
        string origin = Clean(f.Source, 60) + " / " + Clean(f.Kind, 80);
        string header = $"事件 {Time(f.Clock)}  ·  " + (replay ? "赛后录像，不代表队伍当时知情" : $"获知 {Time(f.KnownAt)}");
        return new(replay || f.Clock == f.KnownAt ? Time(f.Clock) : $"{Time(f.Clock)} → {Time(f.KnownAt)}",
            title, origin, Clean(preview, 170), $"{header}\n{origin}  ·  #{f.Sequence}\n{body}");
    }

    public string Snapshot(ReviewLane lane, double cursor)
    {
        var equipment = lane.AsOf("equipment", cursor); var wards = lane.AsOf("ward-estimate", cursor);
        var gap = lane.AsOf("gap", cursor);
        bool Interrupted(MatchFact? f) => gap != null && (f == null || f.KnownAt < gap.KnownAt ||
            f.KnownAt == gap.KnownAt && f.Sequence <= gap.Sequence);
        var equipmentText = Interrupted(equipment) ? $"记录在 {Time(gap!.KnownAt)} 中断；装备未知，不沿用中断前的快照。" :
            equipment == null ? "截至游标，没有装备观察。" :
            $"最近一次装备观察　{Time(equipment.Clock)}（获知 {Time(equipment.KnownAt)}；不保证仍持有）\n" + Equipment(equipment.Data);
        var wardText = Interrupted(wards) ? "中断前的疑似眼位不再作为当前候选；不表示眼已消失。" :
            wards == null ? "截至游标，没有眼位推测。" :
            $"最近一次眼位推测　{Time(wards.Clock)}（获知 {Time(wards.KnownAt)}）\n" + Estimates(wards.Data, Math.Max(0, cursor - wards.Clock));
        var own = lane.AsOf("own-state", cursor); var seen = lane.AsOf("ward-observed", cursor);
        string ownText = own != null && !Interrupted(own) ? $"本人最近状态　{Time(own.Clock)}\n" + Own(own.Data) : "截至游标，没有连续的本人状态样本。";
        string observed = seen != null && !Interrupted(seen) ? $"最近一次队伍眼位观察　{Time(seen.Clock)}\n" + ObservedWards(seen.Data) :
            "截至游标，没有连续的队伍眼位观察。";
        return equipmentText + "\n\n" + wardText + "\n\n" + observed + "\n\n" + ownText;
    }

    public ReplayRoster Roster(ReviewLane lane, double cursor, int focus)
    {
        var frame = lane.HeroesAt(cursor); var lastWard = lane.AsOf("replay-ward", cursor);
        var lines = new List<string>(); var dots = new List<ReviewDot>(); var unreported = new List<string>();
        string[] labels = new string[11]; labels[0] = "全部 10 个槽位";
        for (int i = 0; i < 10; i++) {
            var f = frame?.Slots[i]; var d = f?.Data ?? default;
            string hero = Hero(Get(d, "hero"));
            labels[i + 1] = $"槽 {i} · {hero}";
            var x = Number(d, "x"); var y = Number(d, "y"); var team = Number(d, "team");
            if (x.HasValue && y.HasValue) dots.Add(new(x.Value, y.Value, team is 2 or 3 ? (int)team.Value : null, i.ToString(CultureInfo.InvariantCulture)));
            if (focus >= 0 && i != focus) continue;
            if (f == null) { unreported.Add($"槽 {i}"); continue; }
            lines.Add($"槽 {i}　{hero} · {Side(d)} · 等级 {Val(d, "level", "未知")} · 生命 {Val(d, "hp")}/{Val(d, "maxHp")} · " +
                (x.HasValue && y.HasValue ? "世界位置 " + Point(d) : "位置未知") + "\n　　　" + ReplayEconomy(d, details: focus == i) +
                "\n　　　装备：" + Items(Get(d, "items"), compact: true));
        }
        if (unreported.Count > 0) lines.Add(string.Join("、", unreported) + "：该帧未记录，身份、位置、装备和经济均未知。");
        if (lastWard != null) {
            var d = lastWard.Data; string action = Val(d, "action") switch { "placed" => "放置", "removed" => "移除", _ => "变更" };
            if (Number(d, "x") is double x && Number(d, "y") is double y) dots.Add(new(x, y, null, action == "移除" ? "×" : "+", true));
            lines.Add($"◇ 最近录像眼事件　{Time(lastWard.Clock)} · {action} · {Ward(d, "type")} · 世界位置 {Point(d)}（不代表仍存活）");
        }
        return new(frame == null ? "还没有英雄采样 · 十个槽位均未知" :
            $"采样 {Time(frame.Clock)} · tick {frame.Tick} · 不插值、不沿用缺失槽位", string.Join("\n", lines), dots.ToArray(), labels);
    }

    // Normalized v1 extension: optional per-packet, per-slot counters, not a GPM/level-derived estimate.
    private static string ReplayEconomy(JsonElement sample, bool details)
    {
        var economy = Get(sample, "economy");
        if (economy.ValueKind != JsonValueKind.Object) return "录像经济：未知（本帧未记录）";
        string Count(string key) => economy.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Number &&
            v.TryGetInt32(out int n) && n >= 0 ? n.ToString(CultureInfo.InvariantCulture) : "未知";
        return $"净资产 {Count("netWorth")} · 补刀/反补 {Count("lastHits")}/{Count("denies")} · 当前经验 {Count("currentXP")}" +
            (details ? $" · 累计赚金 {Count("totalEarnedGold")} · 小兵击杀金 {Count("creepKillGold")}" : "");
    }

    private string Own(JsonElement d)
    {
        var lines = new List<string> { $"{Hero(Get(d, "hero"))} · {Side(d)} · 等级 {Val(d, "level", "未知")} · 世界位置 {Point(d)}",
            $"生命 {Val(d, "health")}/{Val(d, "maxHealth")} · 魔法 {Val(d, "mana")}/{Val(d, "maxMana")} · 存活 {Val(d, "alive", "未知")}",
            $"金钱 {Val(d, "gold")} · 净资产 {Val(d, "netWorth")} · 补刀/反补 {Val(d, "lastHits")}/{Val(d, "denies")}",
            $"击杀/死亡/助攻 {Val(d, "kills")}/{Val(d, "deaths")}/{Val(d, "assists")} · GPM {Val(d, "gpm")} · XPM {Val(d, "xpm")}",
            "自身装备：" + Items(Get(d, "items")), "— 表示未报告，并非零。" };
        return string.Join("\n", lines);
    }

    private static string ObservedWards(JsonElement d)
    {
        if (Get(d, "available").ValueKind == JsonValueKind.False) return "眼位观察来源已中断，状态未知；并非眼已被移除。";
        var seen = Get(d, "wards");
        if (seen.ValueKind != JsonValueKind.Array) return Compact(d);
        if (seen.GetArrayLength() == 0) return "这次队伍视野中未见眼；不证明眼已移除。";
        return "队伍视角中确实看见；不等于看到了放置动作。\n" +
            string.Join("\n", Elements(seen, 16).Select(w => $"{Side(w)} · {Ward(w, "kind")} · 世界位置 {Point(w)}")) +
            (seen.GetArrayLength() > 16 ? $"\n显示前 16 / {seen.GetArrayLength()} 个。" : "");
    }

    private string Body(MatchFact f)
    {
        var d = f.Data;
        switch (f.Kind) {
            case "equipment": return Equipment(d);
            case "ward-estimate": return Estimates(d);
            case "farm-input" when f.Source == "coach": {
                var input = Get(d, "input"); var self = Get(input, "self"); var options = Get(input, "options");
                int count = options.ValueKind == JsonValueKind.Array ? options.GetArrayLength() : 0;
                string shared = Get(d, "includesTeamKnowledge").ValueKind == JsonValueKind.True ? "已授权队伍观测" : "仅本人，未共享队伍观测";
                string at = Number(d, "inputClock") is double clock ? Time(clock) : "未知";
                string? Id(string key) => Get(d, key) is { ValueKind: JsonValueKind.String } v ? v.GetString() : null;
                var (provider, model) = MatchArchive.MinuteTarget(Id("providerId"), Id("modelId"));
                string target = provider switch {
                    CopilotGateway.ProviderId => $"{provider}（本地代理） / {model ?? "模型 ID 未记录"}",
                    LiveCoachSettings.FoundryProvider => $"{provider} / {model ?? "部署名未记录"}",
                    _ => "未记录"
                };
                var lines = new List<string> { $"请求目标：{target} · 依据快照 #{Val(d, "inputSequence")} · 游戏 {at} · {count} 个候选 · {shared}",
                    $"{Hero(Get(self, "hero"))} · 生命 {Val(self, "health")} · 世界位置 {Point(self)} · 剩余规划 {Val(input, "horizonSeconds")} 秒" };
                foreach (var option in Elements(options, 6))
                    lines.Add("• " + string.Join(" → ", Elements(Get(option, "stops"), 3).Select(s =>
                        $"{Val(s, "label")}（+{Val(s, "arrivalSeconds")} 秒）")));
                if (count > 6) lines.Add($"仅展示前 6 / {count} 个候选。");
                lines.Add("发送前记录；不证明服务已收到、已选路线或实际到达。");
                return string.Join("\n", lines);
            }
            case "farm-decision": {
                var lines = new List<string> { $"第 {Val(d, "minute")} 分钟 · {Val(d, "status")}（计划不等于实际到达）" };
                foreach (var s in Elements(Get(d, "stops"), 8))
                    lines.Add($"• {Val(s, "label", Val(s, "id"))} · {Val(s, "kind")} {Point(s)} · 预计到达 +{Val(s, "arrivalSeconds")} 秒，完成 +{Val(s, "finishSeconds")} 秒");
                AppendIf(lines, d, "chosen", "选择"); AppendIf(lines, d, "inputClock", "依据游戏秒");
                AppendIf(lines, d, "reason", "说明"); AppendIf(lines, d, "message", "提示");
                return string.Join("\n", lines);
            }
            case "own-state": return Own(d) + "\n" + Compact(d);
            case "ward-observed": return ObservedWards(d);
            case "ward-charges": return $"{Hero(Get(d, "hero"))} · 携带侦察 {Val(d, "observer")} / 岗哨 {Val(d, "sentry")} / 未分类 {Val(d, "unknown")}" +
                "\n只是栏位读数，不证明放置或眼位。\n" + Compact(d);
            case "ward-alert": return "来自眼位推断，不是已验证的插眼。\n" + Compact(d);
            case "team-sight": {
                if (Get(d, "available").ValueKind == JsonValueKind.False) return "队伍视野来源已撤回；英雄当前位置未知。";
                var a = Get(d, "heroes");
                return a.ValueKind == JsonValueKind.Array && a.GetArrayLength() == 0 ? "此份队伍视野样本未记录英雄。" :
                    a.ValueKind == JsonValueKind.Array ? string.Join("\n", Elements(a, 10).Select(h => $"{Hero(Get(h, "hero"))} · {Side(h)} · 世界坐标 {Point(h)}")) : Compact(d);
            }
            case "replay-hero": return $"{Hero(Get(d, "hero"))} · {Side(d)} · 等级 {Val(d, "level")} · 生命 {Val(d, "hp")}/{Val(d, "maxHp")}" +
                $"\n{ReplayEconomy(d, details: true)}\n录像装备：{Items(Get(d, "items"))}\n世界坐标 {Point(d)} · 存活 {Val(d, "alive")} · 玩家槽 {Val(d, "playerId")}";
            case "replay-ward": return $"{Ward(d, "type")} · {Side(d)} · 世界坐标 {Point(d)}" +
                $"\n证据 {Val(d, "evidence")} · 归属玩家槽 {Val(d, "ownerPlayerId")} · 眼实体 {Val(d, "wardId")}" +
                $"\n录像可见性位：天辉 {Val(d, "visibleRadiant", "未知")} / 夜魇 {Val(d, "visibleDire", "未知")}。不反填当时已知视角。";
            default: return Compact(d);
        }
    }

    private string Equipment(JsonElement d)
    {
        var heroes = Get(d, "heroes");
        if (Get(d, "available").ValueKind == JsonValueKind.False) return "读数已撤回：此时装备未知，不沿用上一份快照。";
        if (heroes.ValueKind != JsonValueKind.Array) return "没有可读的装备快照。\n" + Compact(d);
        if (heroes.GetArrayLength() == 0) return "本次快照没有可展示的英雄；不能据此判断装备为空。";
        return string.Join("\n", Elements(heroes, 12).Select(h => {
            var age = Val(h, "age", "");
            return $"{Hero(Get(h, "hero"))}　等级 {Val(h, "level")}　·　{Items(Get(h, "items"))}" +
                (Val(h, "neutral", "") is { Length: > 0 } neutral ? $"　·　中立 {ItemLabel(neutral)}" : "") +
                (age.Length > 0 ? $"　·　采样时已距观察 {age} 秒" : "");
        }));
    }

    private string Estimates(JsonElement d, double? elapsed = null)
    {
        var wards = Get(d, "wards");
        if (Get(d, "available").ValueKind == JsonValueKind.False) return "推测来源已中断，旧候选已撤回。";
        if (wards.ValueKind != JsonValueKind.Array) return "没有可读的推测快照。\n" + Compact(d);
        if (wards.GetArrayLength() == 0) return "此时已撤回全部候选；不表示全图无眼。";
        var valid = Elements(wards, 64).Where(w => elapsed == null || Get(w, "left") is not { ValueKind: JsonValueKind.Number } left ||
            !left.TryGetDouble(out double ttl) || ttl > elapsed).ToArray();
        if (valid.Length == 0) return "按当时估计的寿命，游标处已无未到期候选；不表示眼已证实移除。";
        var lines = valid.Take(16).Select(w => {
            string remaining = elapsed is double age && Get(w, "left") is { ValueKind: JsonValueKind.Number } left && left.TryGetDouble(out double ttl) ?
                $"游标处估计剩余 {ttl - age:0.#} 秒" : $"采样时估计剩余 {Val(w, "left")} 秒";
            return $"{Hero(Get(w, "hero"))} · {Val(w, "kind")} · 候选 {Val(w, "rank")}　{Point(w)}" +
                $"　·　比重 {Val(w, "share")} · {remaining}" + (Get(w, "rough").ValueKind == JsonValueKind.True ? " · 粗略" : "");
        });
        return string.Join("\n", lines) + (valid.Length > 16 ? $"\n显示前 16 / {valid.Length} 个候选。" : "") +
            $"\n坐标：{Val(d, "coordinates", "未标注")}；推测位置及寿命均未经录像证实。";
    }

    private string Hero(JsonElement value)
    {
        string key = Scalar(value);
        return key.Length == 0 ? "英雄未记录" : Clean(names[key], 100);
    }

    private string Items(JsonElement a, bool compact = false)
    {
        if (a.ValueKind != JsonValueKind.Array) return "装备未记录";
        if (a.GetArrayLength() == 0) return "未记录到装备";
        var slots = Elements(a, 32).Select(v => v.ValueKind == JsonValueKind.Object
            ? Val(v, "name", Val(v, "id")) : Scalar(v) switch { "" => "未知", "empty" => "空栏", var item => item }).ToArray();
        if (!compact) return string.Join(" / ", slots.Select(ItemLabel));
        var held = slots.Select((item, i) => (item, i)).Where(s => s.item is not ("未知" or "空栏")).Select(s => $"[{s.i}] {ItemLabel(s.item)}").ToList();
        if (slots.Count(s => s == "空栏") is > 0 and var empty) held.Add($"空栏 ×{empty}");
        if (slots.Count(s => s == "未知") is > 0 and var unknown) held.Add($"未知栏位 ×{unknown}");
        return string.Join(" / ", held);
    }

    private static string Point(JsonElement d) => $"({Val(d, "x")}, {Val(d, "y")})";
    private static string Side(JsonElement d) => Val(d, "team") switch {
        "2" or "radiant" => "天辉", "3" or "dire" => "夜魇", _ => "队伍未知" };
    private static string Ward(JsonElement d, string field) => Val(d, field) switch {
        "observer" => "侦察守卫", "sentry" => "岗哨守卫", var other => other };
    private static double? Number(JsonElement d, string key) => Get(d, key) is { ValueKind: JsonValueKind.Number } value &&
        value.TryGetDouble(out double n) && double.IsFinite(n) ? n : null;
    private static void AppendIf(List<string> into, JsonElement d, string key, string label)
    {
        if (Val(d, key, "") is { Length: > 0 } value) into.Add(label + "：" + value);
    }

    private static IEnumerable<JsonElement> Elements(JsonElement a, int cap) =>
        a.ValueKind == JsonValueKind.Array ? a.EnumerateArray().Take(cap) : [];
    private static JsonElement Get(JsonElement d, string key)
    {
        if (d.ValueKind == JsonValueKind.Object) {
            if (d.TryGetProperty(key, out var v)) return v;
            foreach (var p in d.EnumerateObject().Take(80))
                if (p.Name.Equals(key, StringComparison.OrdinalIgnoreCase)) return p.Value;
        }
        return default;
    }
    private static string Val(JsonElement d, string key, string missing = "—") =>
        Scalar(Get(d, key)) is { Length: > 0 } s ? s : missing;

    private static string Scalar(JsonElement v) => v.ValueKind switch {
        JsonValueKind.String => Clean(v.GetString() ?? "", 180),
        JsonValueKind.Number => v.TryGetInt64(out long n) ? n.ToString(CultureInfo.InvariantCulture) :
            v.TryGetDouble(out double x) && double.IsFinite(x) ? x.ToString("0.###", CultureInfo.InvariantCulture) : "",
        JsonValueKind.True => "是", JsonValueKind.False => "否", _ => ""
    };

    // Unrecognized worker payloads remain inspectable, without dumping unbounded JSON, controls or private metadata.
    private static string Compact(JsonElement root)
    {
        var b = new StringBuilder(); int remaining = 96;
        void Write(JsonElement v, int depth) {
            if (remaining-- <= 0 || b.Length >= 2200 || depth > 3) { b.Append('…'); return; }
            if (v.ValueKind == JsonValueKind.Object) {
                int shown = 0;
                foreach (var p in v.EnumerateObject().Take(32)) {
                    if (PrivateKey(p.Name)) continue;
                    if (shown++ >= 14 || remaining <= 0 || b.Length >= 2200) { b.Append(" …"); break; }
                    if (shown > 1) b.Append(depth == 0 ? "　·　" : ", ");
                    b.Append(Clean(p.Name, 56)).Append('=');
                    Write(p.Value, depth + 1);
                }
            } else if (v.ValueKind == JsonValueKind.Array) {
                b.Append('['); int i = 0;
                foreach (var e in Elements(v, 10)) { if (i++ > 0) b.Append(", "); Write(e, depth + 1); }
                if (v.GetArrayLength() > i) b.Append(" …"); b.Append(']');
            } else b.Append(Scalar(v) is { Length: > 0 } s ? s : "—");
        }
        Write(root, 0);
        return b.Length == 0 ? "无其他已记录字段。" : b.ToString();
    }

    private static readonly string[] PrivateParts = ["token", "secret", "password", "credential", "auth", "ticket",
        "apikey", "api_key", "pairing", "steamid", "accountid", "sessionid", "filepath", "directory", "url"];
    private static bool PrivateKey(string key) => PrivateParts.Any(s => key.Contains(s, StringComparison.OrdinalIgnoreCase));
    internal static string Clean(string text, int max)
    {
        var b = new StringBuilder(Math.Min(max, text.Length));
        foreach (char c in text.AsSpan(0, Math.Min(max, text.Length)))
            b.Append(char.IsControl(c) || char.GetUnicodeCategory(c) == UnicodeCategory.Format ? ' ' : c);
        if (text.Length > max) b.Append('…');
        return b.ToString();
    }
}
