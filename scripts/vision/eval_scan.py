"""Score hero_scan.py readings against the replay's full-spectator GSI logged with them (--truth).

Each reading is compared with the GSI packet nearest its capture time. While a replay plays, values
move between the frame and that packet, so health and mana also get a tolerant score (current within
--tolerance of max). The hero a reading names is the truth it is compared with, so a wrong identity
shows up as wrong fields.
"""
from __future__ import annotations
import argparse
from collections import Counter, defaultdict
import json
from pathlib import Path
import sys

sys.path.insert(0, str(Path(__file__).resolve().parent))  # python -I leaves the script folder off the path
from eval_hud import item_ok  # noqa: E402


def main():
    ap = argparse.ArgumentParser(description=__doc__)
    ap.add_argument("log", type=Path, help="scan.jsonl")
    ap.add_argument("--tolerance", type=float, default=0.03)
    args = ap.parse_args()
    sys.stdout.reconfigure(encoding="utf-8")
    score, errors, readings = defaultdict(Counter), Counter(), 0
    rows = [json.loads(line) for line in open(args.log, encoding="utf-8")]
    for row in rows:
        truth = row.get("truth")
        if not row.get("hero") or not truth:
            continue
        readings += 1

        def check(field, ok, detail=None):
            score[field]["ok" if ok else "wrong"] += 1
            if not ok:
                errors[(field, json.dumps(detail, ensure_ascii=False))] += 1

        check("level", row["level"] == truth["level"], [row["hero"], row["level"], truth["level"]])
        check("alive", row["alive"] == truth["alive"], [row["hero"], row["alive"], truth["alive"]])
        for bar in ("health", "mana"):
            got, (cur, mx) = row[bar], truth[bar]
            check(bar, got == {"current": cur, "max": mx}, [row["hero"], bar, got, [cur, mx]])
            score[bar + ".tolerant"]["ok" if got and got["max"] == mx and abs(got["current"] - cur) <= args.tolerance * mx
                                     else "wrong"] += 1
        items = dict(row.get("items") or {})  # only visits that waited for the item fade
        alt = items.pop("alt", {})
        for slot, got in items.items():
            want = truth["items"].get(slot) or "empty"
            if want.startswith("item_recipe_"):
                continue  # recipes are not tracked: every recipe is the same scroll
            kind = "main" if slot < "slot6" and slot.startswith("slot") else "backpack" if slot.startswith("slot") else slot.rstrip("0123456789")
            check("items." + kind, item_ok(want, got and {"name": None if got == "?" else got, "sameIcon": alt.get(slot, [])}),
                  [row["hero"], slot, got, want])
    identified = sum(1 for r in rows if r.get("hero"))
    print(f"{len(rows)} frames read, {identified} identified, {readings} with truth")
    for field, c in sorted(score.items()):
        total = sum(c.values())
        print(f"{field:18s} {c['ok'] / total:6.1%}  ({c['ok']}/{total})")
    for (field, detail), n in errors.most_common(15):
        print(f"  {n:4d} x {field} {detail}")


if __name__ == "__main__":
    main()
