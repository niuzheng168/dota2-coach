"""Render the offline 7.42 route sheets. No basemap, network or third-party packages.

    py -3 scripts/replays/render-farming-742.py [--check]
"""

import argparse
import json
import math
import re
import unicodedata
import xml.etree.ElementTree as ET
from collections import Counter
from dataclasses import dataclass
from pathlib import Path


ROOT = Path(__file__).resolve().parents[2]
SOURCE = "data/farming-742-routes.json"
OUT = ROOT / "docs/assets"
HERO = {"luna": "露娜", "troll_warlord": "巨魔战将"}
TEAM = {"radiant": "天辉", "dire": "夜魇"}
ITEM = {
    "power_treads": "动力鞋", "phase_boots": "相位鞋", "mask_of_madness": "疯狂面具",
    "yasha": "夜叉", "manta": "幻影斧", "bfury": "狂战斧", "maelstrom": "漩涡",
    "mjollnir": "雷神之锤", "sange_and_yasha": "散夜对剑", "black_king_bar": "黑皇杖",
}
WINDOWS = ((600, 900), (900, 1200))
# The packed trace is sampled every 2 game seconds, with a little tick jitter.
# Its break-before bit also preserves discontinuities in the *unsampled* trace.
MAX_DT = 2.25
PAPER, WHITE, FIELD = "#f2f2eb", "#fffefa", "#f5f6ee"
INK, MUTED, RULE = "#1c3439", "#516569", "#cbd4cb"
GRID, ROAD, TOWER, CAMP = "#dce3d8", "#aab9b0", "#5b706f", "#466b49"
EARLY, LATE, DEATH, GEAR = "#006e83", "#ac4622", "#973951", "#74517f"
SMALL, PAD, INNER, MARGIN, GUTTER = 320, 18, 16, 28, 20
CARD_W = 2 * (PAD + SMALL) + INNER
CARD_H = 526


def fmt(n):
    return f"{n:.2f}".rstrip("0").rstrip(".") if isinstance(n, float) else str(n)


def el(parent, tag, **attrs):
    return ET.SubElement(parent, tag, {k.replace("_", "-"): fmt(v) for k, v in attrs.items()})


def note(parent, value, tag="title"):
    ET.SubElement(parent, tag).text = value


def text(parent, x, y, value, size=14, color=INK, weight=400, **attrs):
    n = el(parent, "text", x=x, y=y, fill=color, font_size=size, font_weight=weight, **attrs)
    n.text = str(value)
    return n


def rect(parent, x, y, w, h, fill, **attrs):
    return el(parent, "rect", x=x, y=y, width=w, height=h, fill=fill, **attrs)


def line(parent, x1, y1, x2, y2, color, width=1, **attrs):
    return el(parent, "line", x1=x1, y1=y1, x2=x2, y2=y2, stroke=color, stroke_width=width, **attrs)


def diamond(parent, x, y, r, fill, **attrs):
    return el(parent, "path", d=f"M{fmt(x)},{fmt(y-r)}l{fmt(r)},{fmt(r)} "
              f"{fmt(-r)},{fmt(r)} {fmt(-r)},{fmt(-r)}Z", fill=fill, **attrs)


def group(parent, x=0, y=0, **attrs):
    return el(parent, "g", transform=f"translate({fmt(x)} {fmt(y)})", **attrs)


def document(w, h, title, description):
    svg = ET.Element("svg", {"xmlns": "http://www.w3.org/2000/svg",
                     "xmlns:xlink": "http://www.w3.org/1999/xlink", "width": str(w),
                     "height": str(h), "viewBox": f"0 0 {w} {h}", "xml:lang": "zh-CN",
                     "role": "img", "aria-labelledby": "title desc"})
    el(svg, "title", id="title").text = title
    el(svg, "desc", id="desc").text = description
    el(svg, "style").text = (
        'text{font-family:"Segoe UI","Microsoft YaHei","Noto Sans CJK SC","Noto Sans SC",'
        '"PingFang SC",sans-serif;font-variant-numeric:tabular-nums}'
        '.halo{paint-order:stroke;stroke:#f5f6ee;stroke-width:3px;stroke-linejoin:round}'
    )
    rect(svg, 0, 0, w, h, PAPER)
    return svg


def finite(n):
    return isinstance(n, (int, float)) and math.isfinite(n)


def valid(p):
    return (isinstance(p, (list, tuple)) and len(p) == 5
            and all(finite(v) for v in p[:3]) and p[3] in (0, 1))


def walks(trace, start, end):
    """Maximal runs of consecutive, in-window live samples. Never filter out a gap."""
    run = []
    for p in trace:
        live = valid(p) and start <= p[0] < end and p[3] == 1
        if run and (not live or p[4] != 0 or not 0 < p[0] - run[-1][0] <= MAX_DT):
            yield run
            run = []
        if live:
            run.append(p)
    if run:
        yield run


def deaths(trace, start, end):
    return [b for a, b in zip(trace, trace[1:]) if valid(a) and valid(b)
            and a[3] == 1 and b[3] == 0 and start <= b[0] < end and 0 < b[0]-a[0] <= MAX_DT]


def minute_sample(trace, target):
    p = next((p for p in trace if p and finite(p[0]) and p[0] >= target), None)
    return p if p and valid(p) and p[3] == 1 and p[0]-target <= MAX_DT else None


def clock(t):
    m, s = divmod(int(t), 60)
    return f"{m:02}:{s:02}"


def camp_key(key):
    prefix, num = key.split("_")
    return {"evil": "E", "good": "G"}[prefix], int(num)


def short(key):
    prefix, num = camp_key(key)
    return f"{prefix}{num}"


def select(matches):
    # Dict order gives the printed order: heroes by row, factions by column.
    return [max((m for m in matches if (m["hero"], m["team"]) == (h, t)),
                key=lambda m: (m["mmr"], m["id"])) for h in HERO for t in TEAM]


@dataclass(frozen=True)
class View:
    size: int
    extent: int

    def xy(self, x, y):
        k = self.size / (2 * self.extent)
        return self.size / 2 + x * k, self.size / 2 - y * k

    def at(self, landmark):
        return self.xy(landmark["x"], landmark["y"])

    def sample(self, p):
        return self.xy(p[1], p[2])


class Landmarks:
    def __init__(self, data, matches):
        self.camps = {c["id"]: c for c in data["camps"]}
        self.towers = {}
        for t in data["towers"]:
            rank, lane = re.search(r"_tower([1-4])_(top|mid|bot)$", t["id"]).groups()
            self.towers[t["team"], int(rank), lane] = t
        coords = [(p["x"], p["y"]) for p in data["camps"] + data["towers"]]
        coords += [p[1:3] for m in matches for p in m["trace"] if valid(p) and 600 <= p[0] < 1200]
        self.extent = math.ceil((max(abs(v) for xy in coords for v in xy) + 500) / 2500) * 2500

    def lane(self, name):
        return [self.towers[side, rank, name] for side, rank in
                ((2, 3), (2, 2), (2, 1), (3, 1), (3, 2), (3, 3))]


def box(x, y, r):
    return x-r, y-r, x+r, y+r


def overlap(a, b):
    return max(0, min(a[2], b[2])-max(a[0], b[0])) * max(0, min(a[3], b[3])-max(a[1], b[1]))


def label_width(value, size):
    return size * sum(1 if unicodedata.east_asian_width(c) in "FW" else .60 for c in value)


def label(g, size, anchor, value, color, font, occupied, trace_xy=()):
    """Choose a short leader over an occluded label; never reposition the landmark."""
    x, y = anchor
    w, h = label_width(value, font) + 8, font + 4
    candidates = []
    for gap in (5, 12, 21):
        for dx, dy in ((gap, -h/2), (-gap-w, -h/2), (-w/2, -gap-h), (-w/2, gap),
                       (gap, -gap-h), (-w-gap, -gap-h), (gap, gap), (-w-gap, gap)):
            l = min(max(3, x+dx), size-w-3)
            t = min(max(3, y+dy), size-h-3)
            b = l, t, l+w, t+h
            ex, ey = min(max(x, l), l+w), min(max(y, t), t+h)
            score = sum(overlap(b, o) for o in occupied) * 10 + math.hypot(ex-x, ey-y)
            score += 6 * sum(l-1 <= px <= l+w+1 and t-1 <= py <= t+h+1 for px, py in trace_xy)
            candidates.append((score, (l, t, l+w, t+h), (ex, ey)))
    _, b, (ex, ey) = min(candidates, key=lambda c: c[0])
    l, t, r, bottom = b
    occupied.append((l-1, t-1, r+1, bottom+1))
    tag = el(g, "g", data_label=value)
    if math.hypot(ex-x, ey-y) > 6:
        line(tag, x, y, ex, ey, color, .85)
    rect(tag, l, t, w, h, WHITE, rx=3, fill_opacity=.94)
    text(tag, l+w/2, t+font, value, font, color, 650, text_anchor="middle")


def field(g, geo, view, large=False):
    """Tower chains are orientation aids, not creep tracks or terrain geometry."""
    s = view.size
    occupied = []
    rect(g, 0, 0, s, s, FIELD, rx=3, stroke=RULE)
    for i in range(1, 8):
        v = i*s/8
        color, width = (RULE, 1.05) if i == 4 else (GRID, .8 if i % 2 == 0 else .5)
        line(g, v, 1, v, s-1, color, width)
        line(g, 1, v, s-1, v, color, width)
    for lane in ("top", "mid", "bot"):
        pts = [view.at(t) for t in geo.lane(lane)]
        road = el(g, "polyline", points=" ".join(f"{fmt(x)},{fmt(y)}" for x, y in pts),
                  fill="none", stroke=ROAD, stroke_width=2.5 if large else 1.8,
                  stroke_dasharray="6 3" if large else "3.5 2.2", data_lane=lane)
        note(road, {"top": "上路", "mid": "中路", "bot": "下路"}[lane] + "：同路一至三塔直连")
    for c in geo.camps.values():
        x, y = view.at(c)
        r = 5 if large else 2.9
        n = diamond(g, x, y, r, CAMP if large else FIELD,
                    stroke=CAMP if large else "#83987f", stroke_width=1, data_camp=c["id"])
        note(n, f'{c["id"]}  ({c["x"]}, {c["y"]})')
        occupied.append(box(x, y, r+1))
    for (side, rank, _), t in geo.towers.items():
        x, y = view.at(t)
        r = (7.5 if rank < 4 else 5.5) if large else 2.4
        n = el(g, "g", data_tower=t["id"])
        note(n, f'{t["id"]}  ({t["x"]}, {t["y"]})')
        rect(n, x-r, y-r, 2*r, 2*r, TOWER if side == 2 else WHITE, stroke=TOWER, rx=.6)
        if large:
            text(n, x, y+(4 if rank < 4 else 3), rank, 12 if rank < 4 else 9,
                 WHITE if side == 2 else INK, 650, text_anchor="middle")
        occupied.append(box(x, y, r+1))
    fs = 16 if large else 12
    for key, name, small_offset, index_offset in (
        ((3, 1, "top"), "上路", (-35, -8), (-18, -12)),
        ((2, 2, "mid"), "中路", (-20, -5), (-26, -14)),
        ((3, 1, "bot"), "下路", (19, 15), (12, 18)),
    ):
        x, y = view.at(geo.towers[key])
        dx, dy = index_offset if large else small_offset
        x, y = x + dx, y + dy
        text(g, x, y, name, fs, MUTED, 600, **{"class": "halo"})
        occupied.append((x-1, y-fs, x+2*fs+1, y+3))
    for x, y, name, anchor in ((9, s-9, "天辉", "start"), (s-9, 18, "夜魇", "end")):
        text(g, x, y, name, fs, MUTED, 600, text_anchor=anchor)
        l = x if anchor == "start" else x-2*fs
        occupied.append((l-1, y-fs, l+2*fs+1, y+3))
    return occupied


def death_mark(g, x, y):
    d = f"M{fmt(x-4)},{fmt(y-4)}l8,8m-8,0l8,-8"
    for stroke, w in ((WHITE, 4.5), (DEATH, 2.3)):
        el(g, "path", d=d, fill="none", stroke=stroke, stroke_width=w, stroke_linecap="round")


def direction_marks(g, runs, view, start, color, avoid):
    pairs = [(a, b, view.sample(a), view.sample(b)) for run in runs for a, b in zip(run, run[1:])]
    for target in range(start+30, start+300, 60):
        for a, b, p, q in sorted(pairs, key=lambda pair: abs(pair[1][0]-target)):
            if abs(b[0]-target) > 12:
                break
            x, y = (p[0]+q[0])/2, (p[1]+q[1])/2
            if math.dist(p, q) < 6 or any(math.hypot(x-u, y-v) < 13 for u, v in avoid):
                continue
            angle = math.degrees(math.atan2(q[1]-p[1], q[0]-p[0]))
            el(g, "path", d="M4.3,0 -3.1,-3.5 -1.8,0 -3.1,3.5Z",
               transform=f"translate({fmt(x)} {fmt(y)}) rotate({fmt(angle)})",
               fill=color, stroke=WHITE, stroke_width=.7)
            avoid.append((x, y))
            break


def draw_walk(g, m, geo, view, start, end, camps, color, obstacles):
    el(g, "use", **{"xlink:href": "#field"})
    runs = list(walks(m["trace"], start, end))
    points = [view.sample(p) for run in runs for p in run]
    times = [p for t in (start+120, start+240) if (p := minute_sample(m["trace"], t))]
    dead = deaths(m["trace"], start, end)
    top = sorted(camps, key=lambda k: (-camps[k], camp_key(k)))[:2]
    avoid = [view.sample(p) for p in times+dead] + [view.at(geo.camps[k]) for k in top]
    if points:
        avoid += [points[0], points[-1]]
    occupied = obstacles.copy() + [box(x, y, 5) for x, y in avoid]
    ink = points.copy()

    for run in runs:
        attrs = {"data_start": str(run[0][0]), "data_end": str(run[-1][0]), "class": "walk"}
        xy = [view.sample(p) for p in run]
        if len(xy) == 1:
            n = el(g, "circle", cx=xy[0][0], cy=xy[0][1], r=1.25, fill=color, **attrs)
            note(n, f"孤立存活采样 {clock(run[0][0])}")
        else:
            ink.extend(((x+u)/2, (y+v)/2) for (x, y), (u, v) in zip(xy, xy[1:]))
            n = el(g, "polyline", points=" ".join(f"{fmt(x)},{fmt(y)}" for x, y in xy),
                   fill="none", stroke=color, stroke_width=2.1, stroke_linejoin="round",
                   stroke_linecap="round", **attrs)
            note(n, f"连续存活采样 {clock(run[0][0])}–{clock(run[-1][0])} · {len(run)} 个位置")
    direction_marks(g, runs, view, start, color, avoid)
    occupied.extend(box(x, y, 4) for x, y in avoid)
    for key, count in camps.items():
        x, y = view.at(geo.camps[key])
        n = diamond(g, x, y, 4.8 if key in top else 3.5, CAMP, stroke=WHITE, stroke_width=.9,
                    data_origin=key, data_kills=count)
        note(n, f"{key}：本段 {count} 个本体野怪正补（出生营地，非击杀坐标）")
    if points:
        x, y = points[0]
        n = el(g, "circle", cx=x, cy=y, r=4.7, fill=WHITE, stroke=color, stroke_width=2.2)
        note(n, f"首个存活采样 {clock(runs[0][0][0])}")
        x, y = points[-1]
        n = rect(g, x-4.3, y-4.3, 8.6, 8.6, color, stroke=WHITE, stroke_width=1.3)
        note(n, f"末个存活采样 {clock(runs[-1][-1][0])}")
    for p in dead:
        n = el(g, "g", data_death=str(p[0]))
        note(n, f"转为死亡的首个采样 {clock(p[0])}")
        death_mark(n, *view.sample(p))
    for key in top:
        label(g, view.size, view.at(geo.camps[key]), f"{short(key)} ×{camps[key]}",
              CAMP, 12, occupied, ink)
    for p in times:
        x, y = view.sample(p)
        el(g, "circle", cx=x, cy=y, r=2.5, fill=color, stroke=WHITE, stroke_width=1)
        label(g, view.size, (x, y), f"{int(p[0]//60)}′", color, 12, occupied, ink)


def rug(g, events, items, start, end):
    """Event positions are *times*, not estimated hero or victim locations."""
    x0, x1 = 51, SMALL-9
    tx = lambda t: x0 + (t-start)/(end-start)*(x1-x0)
    for t in range(start, end+1, 60):
        x = tx(t)
        line(g, x, 21, x, 54, RULE, .8)
        text(g, x, 17, f"{t//60}′", 11.5, MUTED, text_anchor="middle")
    for y, is_lane, name, color in ((28, True, "线", INK), (49, False, "野", CAMP)):
        times = [e[0] for e in events if e[1].startswith("lane:") == is_lane]
        text(g, 0, y+4, f"{name} {len(times)}", 14, color, 650)
        line(g, x0, y, x1, y, GRID)
        d = " ".join(f"M{fmt(tx(t))},{y-4}v8" for t in times)
        if d:
            el(g, "path", d=d, fill="none", stroke=color, stroke_width=1.5,
               data_kind="lane" if is_lane else "neutral", data_events=len(times))
    for t, key in items:
        x = tx(t)
        line(g, x, 21, x, 55, GEAR, 1, stroke_dasharray="2 2")
        note(diamond(g, x, 58, 3.6, GEAR, data_item=key, data_seconds=str(t)),
             f"{clock(t)} {ITEM[key]}：主栏首见")
    diamond(g, 4, 72, 3.4, GEAR)
    text(g, 14, 76, "装备", 13, GEAR, 650)
    text(g, x0, 76, "　".join(f"{clock(t)} {ITEM[k]}" for t, k in items) or "无新增记录",
         13.5, INK if items else MUTED)


def panel(svg, idx, x, y, m, geo, view, obstacles):
    g = group(svg, x, y, data_match=m["id"], id=f'match-{m["id"]}')
    name = f'{HERO[m["hero"]]} · {TEAM[m["team"]]}'
    note(g, f'{name} / {m["player"]} / 对局平均 MMR {m["mmr"]} / {m["id"]}')
    rect(g, 0, 0, CARD_W, CARD_H, WHITE, stroke=RULE, rx=7)
    rect(g, PAD, 17, 31, 31, INK, rx=4)
    text(g, PAD+15.5, 39, "ABCD"[idx], 19, WHITE, 650, text_anchor="middle")
    text(g, 61, 41, name, 25, INK, 650)
    text(g, CARD_W-PAD, 38, f'MMR {m["mmr"]:,}   /   #{m["id"]}', 17, INK, 600, text_anchor="end")
    text(g, PAD, 64, m["player"], 15, MUTED)
    items = sorted((t, k) for k, t in m["firstHeld"].items())
    prior = " · ".join(ITEM[k] for t, k in items if t < 600) or "无记录"
    text(g, CARD_W-PAD, 64, "10:00 前主栏已见　"+prior, 13.5, MUTED, text_anchor="end")
    line(g, PAD, 76, CARD_W-PAD, 76, RULE)
    for j, (start, end) in enumerate(WINDOWS):
        color = (EARLY, LATE)[j]
        sub = group(g, PAD+j*(SMALL+INNER), 0, data_from=start, data_to=end)
        events = [e for e in m["events"] if start <= e[0] < end and e[2] == "body"]
        origins = Counter(e[1] for e in events)
        camps = {k: n for k, n in origins.items() if k in geo.camps}
        if any(k not in geo.camps and k not in ("?", "lane:top", "lane:mid", "lane:bot", "lane:?")
               for k in origins):
            raise ValueError(f"Unknown event origin in match {m['id']}")
        text(sub, 0, 101, f"{clock(start)}–{clock(end)}", 18, color, 650)
        lanes = [f"{name} {origins['lane:'+key]}" for key, name in (("top", "上"), ("mid", "中"), ("bot", "下"))]
        if origins["lane:?"]:
            lanes.append(f"未定 {origins['lane:?']}")
        text(sub, SMALL, 100, "兵线　"+"　".join(lanes), 13, MUTED, text_anchor="end")
        draw_walk(group(sub, 0, 110), m, geo, view, start, end, camps, color, obstacles)
        rug(group(sub, 0, 434), events, [(t, k) for t, k in items if start <= t < end], start, end)


def legend(g):
    for x, color in ((1, EARLY), (128, LATE)):
        line(g, x, 0, x+23, 0, color, 2.5)
        el(g, "path", d=f"M{x+25},0l-7,-4v8Z", fill=color)
    text(g, 33, 5, "10–15 分", 15, EARLY, 650)
    text(g, 160, 5, "15–20 分", 15, LATE, 650)
    el(g, "circle", cx=274, cy=0, r=5, fill=WHITE, stroke=INK, stroke_width=2)
    text(g, 286, 5, "首个存活点", 15)
    rect(g, 406, -4, 8, 8, INK)
    text(g, 423, 5, "末个存活点", 15)
    death_mark(g, 545, 0)
    text(g, 558, 5, "转为死亡", 15)
    diamond(g, 669, 0, 5, CAMP)
    text(g, 682, 5, "本段有正补的营地", 15)
    diamond(g, 866, 0, 5, FIELD, stroke="#83987f")
    text(g, 879, 5, "其他营地", 15)
    rect(g, 998, -4, 8, 8, TOWER)
    text(g, 1016, 5, "防御塔", 15)
    line(g, 1110, 0, 1136, 0, ROAD, 2, stroke_dasharray="5 3")
    text(g, 1144, 5, "同路塔位连线（非兵道）", 15)


def route_sheet(data, matches, geo):
    w = 2*MARGIN+2*CARD_W+GUTTER
    y0 = 158
    footer = y0+2*CARD_H+GUTTER
    svg = document(w, footer+124, "7.42｜10–20 分钟刷钱路线",
                   f"数据源：{SOURCE}。四组各取对局平均 MMR 最高的一场。"
                   "每格两个同向坐标图，仅画本体，时间范围为 [10:00,15:00)、[15:00,20:00)。"
                   "死亡、跳变和缺失处断开。下方为兵线、野怪正补时间及装备主栏首见。")
    view = View(SMALL, geo.extent)
    defs = el(svg, "defs")
    obstacles = field(el(defs, "g", id="field"), geo, view)
    text(svg, MARGIN, 29, "DOTA 2    /    7.42 录像研究", 15, MUTED, 650)
    text(svg, w-MARGIN, 29, "01  /  02     路线对照", 15, MUTED, 600, text_anchor="end")
    text(svg, MARGIN, 74, "10–20 分钟刷钱路线", 38, INK, 700)
    text(svg, w-MARGIN, 70, "↑ 北　→ 东　 /　左下天辉 · 右上夜魇", 17, INK, 600, text_anchor="end")
    text(svg, MARGIN, 102, f'{len(data["matches"])} 场中按英雄 × 阵营各取对局平均 MMR 最高的一场；'
         "同分取比赛 ID 较大者。每格从左向右，只画英雄本体。", 16, MUTED)
    text(svg, w-MARGIN, 101, "录像坐标示意，不是地形图", 16, MUTED, text_anchor="end")
    line(svg, MARGIN, 116, w-MARGIN, 116, RULE)
    legend(group(svg, MARGIN+6, 135))
    for i, m in enumerate(matches):
        panel(svg, i, MARGIN+(i%2)*(CARD_W+GUTTER), y0+(i//2)*(CARD_H+GUTTER), m, geo, view, obstacles)
    left, right = MARGIN, MARGIN+CARD_W+GUTTER
    text(svg, left, footer+30, "读路线", 17, INK, 650)
    text(svg, left+CARD_W, footer+30, "全图编号见 02 / 地标索引", 13, MUTED, text_anchor="end")
    text(svg, right, footer+30, "读正补与装备", 17, INK, 650)
    text(svg, w-MARGIN, footer+30, SOURCE, 13, MUTED, text_anchor="end")
    for i, msg in enumerate((
        "箭头指移动方向；分钟标签取整分后首个采样，死亡或缺样不标。",
        "仅连相邻存活坐标。死亡、传送等跳变、暂停与缺样处断线；不推断断点成因。",
        "G / E 对应 good_ / evil_ 营地。标签为本段野怪刀数前二；同数按 E、G 及编号升序。",
    )):
        text(svg, left, footer+54+i*23, msg, 14, MUTED)
    for i, msg in enumerate((
        "刻线为每个本体正补的时刻；上 / 中 / 下按兵线出生路，野按出生营地，不是击杀位置。",
        "未知来源仍计入线 / 野总数，不分配地标；不计幻象或攻击者归属不明的事件。",
        "紫色菱形为所跟踪装备在主栏 0–5 首见的时刻，不是购买或送达时间。时间向下取整到秒。",
    )):
        text(svg, right, footer+54+i*23, msg, 14, MUTED)
    return svg


def locator(geo):
    w, h, side, left, top = 770, 856, 636, 69, 117
    svg = document(w, h, "7.42｜营地与防御塔索引",
                   "录像坐标示意，北上东右，与四格路线图同范围。标出所有营地原始编号的缩写和防御塔级别。")
    text(svg, 26, 29, "DOTA 2    /    7.42 录像研究", 15, MUTED, 650)
    text(svg, w-26, 29, "02  /  02     地标索引", 15, MUTED, 600, text_anchor="end")
    text(svg, 26, 72, "营地与防御塔", 36, INK, 700)
    text(svg, w-26, 69, f"{len(geo.camps)} 处营地　/　{len(geo.towers)} 座塔", 18, INK, 600, text_anchor="end")
    text(svg, 26, 99, "与路线图同向、同坐标范围。灰线只串联同路塔位，不代表地形或实际兵道。", 15, MUTED)
    g = group(svg, left, top)
    view = View(side, geo.extent)
    occupied = field(g, geo, view, large=True)
    for k in sorted(geo.camps, key=camp_key):
        label(g, side, view.at(geo.camps[k]), short(k), CAMP, 15, occupied)
    for i in range(5):
        v = -geo.extent+i*geo.extent//2
        x, y = view.xy(v, v)
        line(g, x, side, x, side+5, MUTED)
        line(g, -5, y, 0, y, MUTED)
        text(g, x, side+20, f"{v:,}", 12, MUTED, text_anchor="middle")
        text(g, -9, y+4, f"{v:,}", 12, MUTED, text_anchor="end")
    text(svg, 14, top+26, "Y ↑ 北", 13, MUTED, 600)
    text(svg, w-12, top+side-6, "X → 东", 13, MUTED, 600, text_anchor="end")
    line(svg, 26, 788, w-26, 788, RULE)
    diamond(svg, 33, 811, 5.5, CAMP)
    text(svg, 47, 816, "营地编号　G2 = good_2　 /　E15 = evil_15", 15, INK, 600)
    rect(svg, 417, 804, 14, 14, TOWER, rx=1)
    text(svg, 424, 815, "1", 11, WHITE, 650, text_anchor="middle")
    text(svg, 439, 816, "塔内数字为塔级；实心天辉，空心夜魇。", 14)
    text(svg, 26, 842, "坐标源　"+SOURCE+"  ·  map.camps / map.towers", 13, MUTED)
    return svg


def main():
    cli = argparse.ArgumentParser(description=__doc__)
    cli.add_argument("--check", action="store_true", help="verify both existing SVGs without writing")
    args = cli.parse_args()
    data = json.loads((ROOT / SOURCE).read_bytes())
    if data["version"] != 1 or data["patch"]["version"] != "7.42":
        raise ValueError("Expected the v1 recording study for patch 7.42")
    picked = select(data["matches"])
    geo = Landmarks(data["map"], picked)
    for name, svg in (("farming-742-routes", route_sheet(data, picked, geo)),
                      ("farming-742-landmarks", locator(geo))):
        ET.indent(svg)
        raw = ET.tostring(svg, encoding="utf-8", xml_declaration=True) + b"\n"
        ET.fromstring(raw)
        path = OUT / (name+".svg")
        if args.check:
            if not path.exists() or path.read_bytes() != raw:
                raise SystemExit(f"Missing or stale: {path}")
        else:
            path.write_bytes(raw)
        print(f'{"Checked" if args.check else "Wrote"} {path.relative_to(ROOT)} ({len(raw):,} bytes)')
    for m in picked:
        print(f'{m["hero"]} / {m["team"]}: {m["id"]} ({m["mmr"]} MMR)')


if __name__ == "__main__":
    main()
