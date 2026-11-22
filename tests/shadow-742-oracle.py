"""Independent stdlib-only check of both experiments from the compact *raw* replays.
No TypeScript imports, index, saved neighbors or model predictions are used as inputs.
Run: py -3 tests/shadow-742-oracle.py [fixed|expanded]
"""
from collections import Counter, defaultdict, namedtuple
from fractions import Fraction
from hashlib import sha256
from math import ceil, floor, isclose
from pathlib import Path
import json
import sys

ROOT = Path(__file__).resolve().parents[1] / "data"
ITEMS = set("power_treads phase_boots mask_of_madness yasha manta bfury maelstrom mjollnir sange_and_yasha black_king_bar".split())
A = namedtuple("Anchor", "mid account hero team t span xy items truth")


def read_anchors(m, known):
    trace, events = m["trace"], m["events"]
    terminal = m.get("endPoint", {}).get("t", 0)
    end = min(1200, m.get("duration") or terminal or float("inf"),
              max([terminal] + [p["t"] for p in m["points"]] + [p[0] for p in trace] + [e[0] for e in events]))
    for t in range(315, 1171, 30):
        span = "past" if t >= end else "short" if end < t + 30 else "full"
        p = next((p for p in reversed(trace) if p[0] <= t), None)
        xy = (p[1], p[2]) if p and p[0] >= t-3 and p[3] == 1 and max(map(abs, p[1:3])) <= 12000 else None
        own_items = {k for k, seen in m["firstHeld"].items() if k in ITEMS and 0 <= seen <= t}
        future = [e for e in events if t < e[0] <= t+30 and e[2] != "illusion"]
        sites = set()
        for at, site, kind, *_ in future:
            if at != future[0][0]:
                break
            if kind != "body" or site not in known:
                sites.add("@unresolved")
            elif site.startswith("lane:") or site.startswith("good_" if m["team"] == "radiant" else "evil_"):
                sites.add(site)
            else:
                sites.add("@opponent-camp")
        truth = next(iter(sites)) if len(sites) == 1 else "@unresolved" if sites else "@no-body-hit"
        yield A(str(m["id"]), str(m["accountId"]), m["hero"], m["team"], t, span, xy, own_items, truth if span == "full" else None)


def ranks(q, archive):
    if q.xy is None:
        return [], [], None, None
    potential, time_pool = [], defaultdict(list)
    for a in archive:
        if not a.xy or a.span != "full" or (a.hero, a.team) != (q.hero, q.team) or abs(a.t-q.t) > 90:
            continue
        time_pool[a.account].append(a.truth)
        ds = sum((x-y)**2 for x, y in zip(q.xy, a.xy))
        if ds <= 2400**2:
            # Integer multiple of the squared metric, avoiding distance-rounding order artifacts.
            potential.append((ds * 9 + (a.t-q.t)**2 * 1600, a.mid, a.t, a))
    baseline = Counter()
    if len(time_pool) >= 2:
        for observations in time_pool.values():
            for site, count in Counter(observations).items():
                if not site.startswith("@"):
                    baseline[site] += Fraction(count, len(observations))
    inventory = [p for p in potential if q.items <= p[-1].items]
    if len({p[-1].account for p in inventory}) >= 2:
        potential = inventory
    one_each = {}
    for _, _, _, a in sorted(potential, key=lambda p: p[:3]):
        one_each.setdefault(a.account, a.truth)
    sites = list(one_each.values())[:7]
    model = Counter(s for s in sites if not s.startswith("@")) if len(sites) >= 2 else Counter()
    def majority(mass, total):
        by_kind = Counter()
        for site, value in mass.items():
            by_kind[family(site)] += value
        return next((kind for kind, weight in by_kind.items() if 2*weight > total), None)
    return ([s for s, _ in model.most_common()], sorted(baseline, key=lambda s: (-baseline[s], s)),
            majority(model, len(sites)), majority(baseline, len(time_pool)))


def family(site):
    return None if site is None or site.startswith("@") else "lane" if site.startswith("lane:") else "own-camp"


def paired_check(delta, rows, clusters):
    units = defaultdict(lambda: [0, 0])
    for a, hit, baseline_hit in rows:
        cell = units[min(clusters[a])]
        cell[0] += int(hit) - int(baseline_hit)
        cell[1] += 1
    assert [delta["numerator"], delta["denominator"], delta["clusters"]] == [
        sum(c[0] for c in units.values()), sum(c[1] for c in units.values()), len(units)]
    if len(units) < 2:
        assert delta["ci95"] is None
    elif all(len(v) == 1 for v in clusters.values()):
        # Match/account clusters reduce to account IDs; independently verify percentile interval, not window-IID.
        source_cells = [units[a] for a in sorted(units)]
        ratios, state = [], 0x74251a
        for _ in range(2000):
            n = d = 0
            for _ in source_cells:
                state = (1664525 * state + 1013904223) % 2**32
                x, y = source_cells[(state * len(units)) // 2**32]
                n += x
                d += y
            ratios.append(n/d)
        ratios.sort()
        assert all(isclose(v, delta["ci95"][i], abs_tol=5.1e-7)
                   for i, v in enumerate([ratios[floor(.025*1999)], ratios[ceil(.975*1999)]]))


def audit(selector):
    source, report_name = ("routes", "fixed16") if selector == "fixed" else ("expanded", "expanded")
    b = (ROOT / f"farming-742-{source}.json").read_bytes()
    raw = json.loads(b)
    report = json.loads((ROOT / f"farming-742-shadow-{report_name}.json").read_bytes())
    assert report["source"]["sha256"] == sha256(b).hexdigest()
    known = {c["id"] for c in raw["map"]["camps"]} | {"lane:top", "lane:mid", "lane:bot"}
    games, rows = defaultdict(list), defaultdict(list)
    for m in raw["matches"]:
        if not m.get("trace") or not isinstance(m.get("events"), list):
            continue
        mid = str(m["id"])
        games[mid].append(m)
        rows[mid].extend(read_anchors(m, known))
    start = {i: ms[0]["start"] for i, ms in games.items()}
    end = {i: max(m["start"] + ceil(m.get("duration") or m.get("endPoint", {}).get("t") or 10**12) for m in ms)
           for i, ms in games.items()}
    by_game = {i: {str(p["accountId"]) for p in ms} for i, ms in games.items()}
    chronological = sorted(games, key=lambda i: (start[i], i))
    cutoff = start[chronological[-ceil(len(chronological)/4)]]
    held = [i for i in chronological if start[i] >= cutoff]

    for name, folds in [("frozenChronological", [held]), ("rollingOrigin", [[i] for i in chronological])]:
        section = report[name]
        scores, parts = [], {}
        for test in folds:
            # Shared games link bootstrap clusters even if they have different focal account IDs.
            for i in test:
                group = by_game[i]
                component = set.union(group, *(parts.get(a, {a}) for a in group))
                for a in component:
                    parts[a] = component
            forbidden = set.union(*(by_game[i] for i in test))
            train = [i for i in chronological if i not in test and end[i] < min(start[j] for j in test) and forbidden.isdisjoint(by_game[i])]
            if name == "frozenChronological":
                assert train == section["split"]["trainMatchIds"] and test == section["split"]["testMatchIds"]
                assert set(section["split"]["trainAccountIds"]) == {a for i in train for a in by_game[i]}
            else:
                s = next(s for s in section["splits"] if s["matchId"] == test[0])
                assert s["trainMatches"] == len(train) and s["latestTrainEndUnix"] == max((end[i] for i in train), default=None)
            history = [a for i in train for a in rows[i]]
            for i in test:
                for q in rows[i]:
                    outputs = ranks(q, history) if q.span == "full" else ([], [], None, None)
                    scores.append((q, *outputs))
        for h, stats in [(None, section["overall"]), *section["byHero"].items()]:
            group = [x for x in scores if h is None or x[0].hero == h]
            full = [x for x in group if x[0].span == "full"]
            assert [len(group), len(full), sum(x[0].span == "short" for x in group)] == [
                stats["gridWindows"], stats["fullWindows"], stats["rightCensored"]]
            assert stats["freshAliveWindows"] == sum(x[0].xy is not None for x in full)
            for j, method in [(1, "retrieval"), (2, "baseline")]:
                issued = [x for x in full if x[j]]
                scored = [(x[0].truth, x[j]) for x in issued if x[0].truth != "@unresolved"]
                for field, n, d in [("coverage", len(issued), len(full)), ("top1", sum(p[0] == y for y, p in scored), len(scored)),
                                    ("top3", sum(y in p[:3] for y, p in scored), len(scored))]:
                    rate = stats[method][field]
                    assert (rate["numerator"], rate["denominator"]) == (n, d), (selector, name, h, method, field, n, d)
                issued = [x for x in full if x[j+2] is not None]
                scored = [(family(x[0].truth), x[j+2]) for x in issued if x[0].truth != "@unresolved"]
                for field, n, d in [("coverage", len(issued), len(full)), ("top1", sum(p == y for y, p in scored), len(scored))]:
                    rate = stats["nextFamily"][method][field]
                    assert (rate["numerator"], rate["denominator"]) == (n, d), (selector, name, h, method, "broad", field, n, d)
            paired_check(stats["pairedTop1"]["difference"],
                         [(q.account, r[0] == q.truth, b[0] == q.truth)
                          for q, r, b, *_ in full if r and b and q.truth != "@unresolved"], parts)
            paired_check(stats["nextFamily"]["pairedTop1"]["difference"],
                         [(q.account, r == family(q.truth), b == family(q.truth))
                          for q, _, _, r, b in full if r and b and q.truth != "@unresolved"], parts)
            assert stats["visibleDisplay"]["displayed"] == 0 and stats["visibleDisplay"]["liveAccuracy"] is None
        print(f"{selector} {name}: independently matched {len(folds)} split(s), {len(scores)} grid windows, broad/exact predictions, baselines, coverage and paired uncertainty")


if __name__ == "__main__":
    name = sys.argv[1] if len(sys.argv) == 2 else "fixed"
    if name not in {"fixed", "expanded"}:
        raise SystemExit("expected fixed or expanded")
    audit(name)
