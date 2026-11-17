"""Attribute ground truth for captured HUD frames, read from the replays themselves.

GSI already labels level, health, mana and items at the paused moment, but not
strength/agility/intelligence. This finds the replay tick whose ten-hero state equals
the frame's GSI snapshot and reads the selected hero's attribute totals there.
Run with the gem-dota venv (.tmp/replay-tools), not the vision venv.
"""
from __future__ import annotations
import argparse
from collections import defaultdict
import json
from pathlib import Path

from gem.parser import ReplayParser

NULL_HANDLE = 0xFFFFFF
WINDOW = 240  # ticks after the seek target; capture plays ~2.5 s (75 ticks) before pausing


def gsi_state(sample: dict) -> dict[int, tuple]:
    state = {}
    for team in (sample["gsi"].get("hero") or {}).values():
        for slot, hero in team.items():
            state[int(slot.removeprefix("player"))] = (hero.get("level"), hero.get("health"), hero.get("mana"))
    return state


def main():
    ap = argparse.ArgumentParser(description=__doc__)
    ap.add_argument("dataset", type=Path, help="folder with captures.jsonl")
    ap.add_argument("--replays", type=Path, default=Path(".tmp/replays"))
    args = ap.parse_args()
    samples = [json.loads(line) for line in open(args.dataset / "captures.jsonl", encoding="utf-8")]
    by_replay = defaultdict(list)
    for s in samples:
        by_replay[s["replay"]].append(s)
    out = open(args.dataset / "truth.jsonl", "w", encoding="utf-8")
    for replay, rows in by_replay.items():
        p = ReplayParser(str(args.replays / replay))
        best: dict[str, tuple] = {}
        windows = sorted((s["seekTick"], s["seekTick"] + WINDOW, s) for s in rows)

        def heroes():
            res = p.entity_manager.find_by_class_name("CDOTA_PlayerResource") if p.entity_manager else None
            found = {}
            for i in range(10) if res else ():
                handle = res.get(f"m_vecPlayerTeamData.{i:04d}.m_hSelectedHero")
                e = p.entity_manager.find_by_handle(handle) if handle not in (None, NULL_HANDLE) else None
                if e is not None:
                    found[i] = e
            return found

        def tick(_):
            active = [w for w in windows if w[0] <= p.tick <= w[1]]
            if not active:
                return
            current = heroes()
            for _, _, s in active:
                want = gsi_state(s)
                miss = 0
                for pid, (level, health, mana) in want.items():
                    e = current.get(pid)
                    if e is None:
                        miss += 100
                        continue
                    m = e.get("m_flMana") or 0.0
                    miss += (e.get("m_iCurrentLevel") != level) * 10 + abs((e.get("m_iHealth") or 0) - (health or 0)) \
                        + min(abs(int(m) - (mana or 0)), abs(round(m) - (mana or 0)))
                if s["image"] not in best or miss < best[s["image"]][0]:
                    e = current.get(s["selected"]["playerId"])
                    attrs = None if e is None else {k: e.get(f) for k, f in (
                        ("strength", "m_flStrengthTotal"), ("agility", "m_flAgilityTotal"), ("intelligence", "m_flIntellectTotal"))}
                    best[s["image"]] = (miss, p.tick, attrs)

        p.on_tick_start(tick)
        p.stop_after_tick(max(w[1] for w in windows) + 1)
        p.parse()
        for s in rows:
            miss, at, attrs = best.get(s["image"], (None, None, None))
            out.write(json.dumps({"image": s["image"], "tick": at, "mismatch": miss, "attributes": attrs}) + "\n")
        out.flush()
        print(replay, "matched", sum(1 for s in rows if best.get(s["image"], (1,))[0] == 0), "/", len(rows), flush=True)


if __name__ == "__main__":
    main()
