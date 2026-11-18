"""Score hud_recognizer.py against the labels captured with each frame.

Level, health, mana and items come from the client's GSI at the paused moment
(captures.jsonl); attributes from the replay itself (truth.jsonl, when present).
Frames from replays used for calibration should be left out with --exclude.
"""
from __future__ import annotations
import argparse
from collections import Counter, defaultdict
import json
import math
from pathlib import Path
import sys
import time

import cv2

sys.path.insert(0, str(Path(__file__).resolve().parent))  # python -I leaves the script folder off the path
from hud_recognizer import HudRecognizer, SLOTS


def item_ok(truth: str, got: dict | None) -> bool:
    if truth == "empty":
        return got is None
    if got is None or got["name"] is None:
        return False
    if got["name"] == "item_recipe":
        return truth.startswith("item_recipe_")
    return truth in [got["name"], *got.get("sameIcon", [])]


def main():
    ap = argparse.ArgumentParser(description=__doc__)
    ap.add_argument("datasets", nargs="+", type=Path)
    ap.add_argument("--exclude", nargs="*", default=[], help="replay files used for calibration")
    ap.add_argument("--assets", type=Path, default=Path(".tmp/vision-assets"))
    ap.add_argument("--calibration", type=Path, default=Path(".tmp/vision-assets/hud-1080.npz"))
    ap.add_argument("--report", type=Path)
    ap.add_argument("--any-hero", action="store_true", help="identify the hero among all heroes, not the match's ten")
    args = ap.parse_args()
    sys.stdout.reconfigure(encoding="utf-8")
    hud = HudRecognizer(args.assets, args.calibration)
    score, errors, started = defaultdict(Counter), [], time.monotonic()
    frames = 0
    for d in args.datasets:
        truth = {}
        if (d / "truth.jsonl").exists():
            truth = {r["image"]: r for r in map(json.loads, open(d / "truth.jsonl", encoding="utf-8"))}
        for row in map(json.loads, open(d / "captures.jsonl", encoding="utf-8")):
            if row["replay"] in args.exclude:
                continue
            frames += 1
            # The match's heroes compete for the HUD's identity, as the scanner passes them from GSI.
            match = None if args.any_hero else {h["name"] for players in row["gsi"]["hero"].values()
                                                for h in players.values() if isinstance(h, dict) and h.get("name")}
            got = hud.read(cv2.imread(str(d / row["image"])), match)
            hero = row["selected"]["hero"]
            items = row["gsi"]["items"][row["selected"]["team"]][f"player{row['selected']['playerId']}"]

            def check(field, expected, actual, detail=None):
                ok = expected == actual
                score[field]["ok" if ok else ("missing" if actual is None else "wrong")] += 1
                if not ok:
                    errors.append({"image": row["image"], "field": field, "expected": expected, "got": actual,
                                   **({"detail": detail} if detail else {})})

            if "lone_druid" in hero["name"]:
                continue  # the HUD can show the Spirit Bear while GSI flags the hero as selected
            # The victory screen hides the HUD.
            check("hud", row["gsi"]["map"].get("game_state") != "DOTA_GAMERULES_STATE_POST_GAME", got["hud"])
            if not got["hud"]:
                continue
            check("hero", hero["name"], got["hero"], got["heroMatch"])
            # The name label alone; a dead hero's HUD shows the death summary there instead.
            check("hero.name", hero["name"] if hero["alive"] else None, got["heroMatch"]["named"], got["ocr"]["name"])
            check("level", hero["level"], got["level"], got["ocr"]["level"])
            for bar, cur, mx in (("health", "health", "max_health"), ("mana", "mana", "max_mana")):
                check(bar, {"current": hero[cur], "max": hero[mx]}, got[bar], got["ocr"][bar])
            check("alive", hero["alive"], got["alive"])
            attrs = (truth.get(row["image"]) or {}).get("attributes")
            if attrs and truth[row["image"]]["mismatch"] == 0:
                for k, v in attrs.items():
                    # The HUD shows whole numbers and hides them while the hero is dead. Totals near
                    # .5 went either way (37.5 -> 37, 45.525 -> 45, 34.6 -> 35), so the shown value
                    # counts if it is within 0.55 of the replay's float.
                    shown = got["attributes"][k]
                    expected = None if not hero["alive"] else shown if shown is not None and abs(shown - v) <= 0.55 \
                        else math.floor(v + 0.5)
                    check("attributes", expected, shown, [k, v, got["ocr"].get(k)])
            for slot in SLOTS:
                want = (items.get(slot) or {}).get("name", "empty")
                g = got["items"][slot]
                field = "items." + ("main" if slot < "slot6" and slot.startswith("slot") else slot.rstrip("0123456789")
                                    if not slot.startswith("slot") else "backpack")
                ok = item_ok(want, g)
                cd = (items.get(slot) or {}).get("cooldown", 0) > 0
                score[field]["ok" if ok else "wrong"] += 1
                score[field + (".cooldown" if cd else ".ready")]["ok" if ok else "wrong"] += 1
                if g and len(g.get("sameIcon", [])) and want in [g["name"], *g["sameIcon"]]:
                    # Icons alike in grey (observer and sentry wards): did the first name pick the item?
                    score["items.sameIcon.first"]["ok" if g["name"] == want else "wrong"] += 1
                if not ok:
                    errors.append({"image": row["image"], "field": slot, "expected": want, "cooldown": cd,
                                   "got": g and (g["name"] or f"?{g['best']}"), "score": g and g["score"]})
    print(f"{frames} frames in {time.monotonic() - started:.0f}s")
    for field, c in sorted(score.items()):
        total = sum(c.values())
        print(f"{field:28s} {c['ok'] / total:6.1%}  ({c['ok']}/{total})", dict(c))
    if args.report:
        args.report.write_text(json.dumps({"score": score, "errors": errors}, ensure_ascii=False, indent=1), encoding="utf-8")


if __name__ == "__main__":
    main()
