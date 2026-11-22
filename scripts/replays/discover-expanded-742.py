"""Validate/rebuild the frozen D2PT ranked carry roster. Does not download or invent replays.

Capture once with --capture in an empty destination with --previous pointing to the
earlier snapshot. The default --check recomputes membership from both stored pages.
"""
import argparse
from datetime import datetime, timezone
import hashlib
import json
from pathlib import Path
from urllib.request import Request, urlopen


FIELDS = ("match_id hero_id account_id name mmr is_radiant won patch position "
          "lobby_type last_hits duration activate_time is_pro").split()
SELECTION = ("All hero-match observations across frozen same-day D2PT listings: 7.42, ranked pos 1, "
             "numeric account and match-average MMR >=7000; no win, duration, item or unique-account filter.")


def rows_of(raw):
    records = json.loads(raw)
    if not isinstance(records, list):
        raise ValueError("Not a D2PT Matches list")
    return [json.loads(e["data"]) if isinstance(e["data"], str) else e["data"] for e in records]


def digest(raw):
    return hashlib.sha256(raw).hexdigest()


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    mode = parser.add_mutually_exclusive_group()
    mode.add_argument("--capture", action="store_true")
    mode.add_argument("--check", action="store_true")
    parser.add_argument("--root", type=Path, default=Path(".tmp/farm742-more"))
    parser.add_argument("--previous", type=Path, default=Path(".tmp/farm742"))
    args = parser.parse_args()
    root = args.root
    root.mkdir(parents=True, exist_ok=True)
    target = root / "eligible.json"
    previous_index = None if args.capture else json.loads(target.read_bytes())
    if args.capture and any((root / name).exists() for name in ["eligible.json", "catalog-48.json", "catalog-95.json"]):
        raise FileExistsError("Select a new empty destination to avoid overwriting a frozen catalog")
    sources, selected = [], []
    for hero in (48, 95):
        name = f"catalog-{hero}.json"
        old = (args.previous / name).read_bytes()
        url = f"https://dota2protracker.com/api/hero-matches?hero_id={hero}&position=pos+1&sort_by=mmr"
        if args.capture:
            stamp = datetime.now(timezone.utc).isoformat()
            req = Request(url, headers={"User-Agent": "Mozilla/5.0 (public farming route study)"})
            with urlopen(req, timeout=30) as response:
                raw = response.read(8 * 1024**2 + 1)
            if len(raw) > 8 * 1024**2:
                raise ValueError("Oversized catalog")
            rows_of(raw)
            with (root / name).open("xb") as out:
                out.write(raw)
        else:
            stamp = next(s["checkedAt"] for s in previous_index["sources"] if s["heroId"] == hero)
            raw = (root / name).read_bytes()
        a, b = rows_of(old), rows_of(raw)
        union = {r["match_id"]: r for r in [*a, *b]}
        pubs = [r for r in union.values() if r["hero_id"] == hero and r["position"] == "pos 1" and
                r["patch"] == "7.42" and r["lobby_type"] == 7 and isinstance(r["mmr"], int) and r["account_id"] > 0]
        selected.extend({k: r[k] for k in FIELDS} for r in pubs if r["mmr"] >= 7000)
        sources.append(dict(heroId=hero, url=url, checkedAt=stamp, refreshError=None, freshFile=name,
                            sha256=digest(raw), previousSha256=digest(old), freshRows=len(b), previousRows=len(a),
                            unionPatchRankedRows=len(pubs)))
    selected.sort(key=lambda r: (-r["mmr"], -r["match_id"], r["hero_id"]))
    result = dict(version=1, patch="7.42", minimumMatchMmr=7000, selection=SELECTION, sources=sources, rows=selected)
    if len({r["match_id"] for r in selected}) != len(selected):
        raise ValueError("Both focal heroes in one match; adapt replay storage to (match, hero) before collecting")
    if args.capture:
        with target.open("x", encoding="utf8") as out:
            json.dump(result, out, ensure_ascii=False, indent=2)
            out.write("\n")
    elif result != previous_index:
        raise ValueError("Frozen D2PT roster differs")
    print(f"Verified both catalogs and {len(selected)} distinct 7.42 ranked carries")


if __name__ == "__main__":
    main()
