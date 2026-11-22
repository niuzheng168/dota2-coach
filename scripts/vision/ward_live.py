"""Live check of the coach's ward guesses while spectating a public game (full spectator, both teams visible).
Evaluation only: full-spectator data never reaches the game machine.

    setup                      write a GSI config posting to this recorder (127.0.0.1:3297); restart Dota after
    record OUT_DIR SECONDS     log the spectating client's GSI: every 0.25 s the clock, each hero's position, life and
                               ward charges, and every minimap unit (with its vision radius)
    convert GSI PREFIX SCAN... WardEval input (PREFIX-N.wards.json), one per stretch of continuous game clock that
                               hero_scan.py --truth was reading (seeking with the spectator slider starts a new one)

    .tmp/vision-tools/Scripts/python.exe -I scripts/vision/ward_live.py record .tmp/ward-live/run1 5400
    .tmp/vision-tools/Scripts/python.exe -I scripts/vision/hero_scan.py scan --session NAME --truth
    .tmp/vision-tools/Scripts/python.exe -I scripts/vision/ward_live.py convert .tmp/ward-live/run1/gsi.jsonl.gz \\
        .tmp/ward-live/live .tmp/hero-scan/NAME/scan.jsonl
    dotnet run --project desktop/DotaCoach.TwoPc.Tests -c Release -- --ward-eval --list .tmp/ward-live/live-*.wards.json

The converted files hold the true positions, every unit with vision (its GSI radius; trees and cliffs are not
modelled), the wards that appeared on the full minimap (owner: the team's hero whose charges dropped, else the
nearest within 900) and the scanner's own HUD ward reads. WardEval plays each team as the friend team and uses a
read only if the friend team saw that hero then, as in Plus team view.
"""
from __future__ import annotations
import argparse
import bisect
import gzip
import http.server
import json
import math
import re
import secrets
import threading
import time
import zlib
from pathlib import Path

DOTA = Path("C:/Steam/steamapps/common/dota 2 beta")
CFG_NAME = "gamestate_integration_coach_ward_eval.cfg"
PORT = 3297
WARDS = {"npc_dota_observer_wards": "observer", "npc_dota_sentry_wards": "sentry"}


def config_path(dota: Path) -> Path:
    return dota / "game/dota/cfg/gamestate_integration" / CFG_NAME


def setup(dota: Path):
    path = config_path(dota)
    if path.exists():
        print("exists:", path)
        return
    data = "".join(f'  "{k}" "1"\n' for k in ("provider", "map", "player", "hero", "items", "minimap"))
    path.write_text(f'"coach_ward_eval"\n{{\n "uri" "http://127.0.0.1:{PORT}/"\n "timeout" "5.0"\n "buffer" "0.0"\n'
                    f' "throttle" "0.1"\n "heartbeat" "1.0"\n "auth" {{ "token" "{secrets.token_hex(16)}" }}\n'
                    f' "data"\n {{\n{data} }}\n}}\n', encoding="utf-8")
    print("written:", path, "(restart Dota; delete the file after testing)")


def compact(p: dict, now: float) -> dict:
    game = p.get("map") or {}
    heroes = []
    for team, players in (p.get("hero") or {}).items():
        for slot, h in (players or {}).items():
            if not isinstance(h, dict) or not h.get("name"):
                continue
            items = ((p.get("items") or {}).get(team) or {}).get(slot) or {}
            observer = sentry = 0
            for key, item in items.items():
                if not isinstance(item, dict) or not key.startswith("slot"):
                    continue
                name, charges = item.get("name"), item.get("charges") or 0
                if name == "item_ward_observer":
                    observer += charges
                elif name == "item_ward_sentry":
                    sentry += charges
                elif name == "item_ward_dispenser":
                    observer += charges  # GSI gives the dispenser's front count only
            heroes.append([h["name"], 2 if team == "team2" else 3, h.get("xpos"), h.get("ypos"), h.get("alive"), observer, sentry,
                           {k: [i.get("name"), i.get("charges")] for k, i in items.items() if isinstance(i, dict) and "ward" in str(i.get("name"))}])
    units = [[u.get("unitname"), u.get("team"), u.get("xpos"), u.get("ypos"), u.get("image"), u.get("visionrange")]
             for u in (p.get("minimap") or {}).values() if isinstance(u, dict)]
    return {"at": round(now, 3), "clock": game.get("clock_time"), "state": game.get("game_state"), "match": game.get("matchid"),
            "paused": game.get("paused"), "heroes": heroes, "units": units}


def record(out: Path, seconds: float, dota: Path):
    token = re.search(r'"token"\s+"([^"]+)"', config_path(dota).read_text(encoding="utf-8")).group(1)
    out.mkdir(parents=True, exist_ok=True)
    log = gzip.open(out / "gsi.jsonl.gz", "at", encoding="utf-8")
    state, lock = {"last": 0.0, "packets": 0, "rows": 0}, threading.Lock()

    class Handler(http.server.BaseHTTPRequestHandler):
        def do_POST(self):
            body = self.rfile.read(min(int(self.headers.get("Content-Length", 0)), 16 << 20))
            self.send_response(200)
            self.end_headers()
            try:
                p = json.loads(body)
            except ValueError:
                return
            if not isinstance(p, dict) or (p.get("auth") or {}).get("token") != token:
                return
            now = time.time()
            with lock:
                state["packets"] += 1
                if now - state["last"] >= 0.25:
                    state["last"] = now
                    log.write(json.dumps(compact(p, now), separators=(",", ":")) + "\n")
                    state["rows"] += 1

        def log_message(self, *_):
            pass

    server = http.server.ThreadingHTTPServer(("127.0.0.1", PORT), Handler)
    threading.Thread(target=server.serve_forever, daemon=True).start()
    end = time.time() + seconds
    try:
        while time.time() < end:
            time.sleep(5)
            with lock:
                log.flush()
                (out / "status.json").write_text(json.dumps(state), encoding="utf-8")
    finally:
        server.shutdown()
        with lock:
            log.close()
    print(json.dumps(state))


def rows(path: Path) -> list[dict]:
    """Rows of the GSI log, also while it is still being written (stops at the truncated end)."""
    out = []
    try:
        with gzip.open(path, "rt", encoding="utf-8") as f:
            for line in f:
                try:
                    out.append(json.loads(line))
                except ValueError:
                    break
    except (EOFError, zlib.error, OSError):
        pass
    return out


def stretches(rs: list[dict]) -> list[list[dict]]:
    """Runs of game time without a seek (the clock going back, or forward by more than 5 s)."""
    out, cur, last = [], [], None
    for r in rs:
        c = r.get("clock")
        if c is None or r.get("state") != "DOTA_GAMERULES_STATE_GAME_IN_PROGRESS":
            continue
        if last is not None and (c < last or c > last + 5):
            out.append(cur)
            cur = []
        cur.append(r)
        last = c
    out.append(cur)
    return [s for s in out if len(s) > 40]


def timed(stretch: list[dict]) -> list[dict]:
    """Sub-second game time: the whole-second clock plus the arrival time within that second."""
    base_clock = base_at = None
    for r in stretch:
        if r["clock"] != base_clock or r.get("paused"):
            base_clock, base_at = r["clock"], r["at"]
        r["t"] = r["clock"] + min(0.99, r["at"] - base_at)
    return stretch


def aligned(reads: list[dict], rs: list[dict]) -> list[dict]:
    """Wall time of each scanner read (scan.jsonl keeps seconds since the scan started): the offset that puts the most
    reads on GSI rows of their own clock. Clocks repeat after seeking back, so the clock alone cannot place them."""
    clocks = [(r["at"], r["clock"]) for r in rs if r.get("clock") is not None]
    walls = [a for a, _ in clocks]

    def clock_at(wall):
        return clocks[min(bisect.bisect_left(walls, wall), len(clocks) - 1)][1]

    first = reads[0]
    offset = max({round(a - first["t"], 1) for a, c in clocks if c == first["clock"]},
                 key=lambda off: sum(abs(clock_at(r["t"] + off) - r["clock"]) <= 1 for r in reads[::25]))
    for r in reads:
        r["wall"] = r["t"] + offset
    return reads


def covered(stretch: list[dict], reads: list[dict]) -> list[list[dict]]:
    """Parts of a stretch the scanner was reading during (a read within 10 s), at least 30 s long."""
    walls = sorted(r["wall"] for r in reads)
    runs, cur = [], []
    for r in stretch:
        i = bisect.bisect_left(walls, r["at"] - 10)
        if i < len(walls) and walls[i] <= r["at"] + 10:
            cur.append(r)
        elif cur:
            runs.append(cur)
            cur = []
    runs.append(cur)
    return [c for c in runs if len(c) > 120]


def wards_of(stretch: list[dict]) -> list[dict]:
    start, end = stretch[0]["t"], stretch[-1]["t"]
    seen = {}
    for r in stretch:
        for u in r["units"]:
            if u[0] in WARDS and u[1] in (2, 3) and (u[2], u[3]) != (0, 0):  # (0, 0): not a placed ward
                seen.setdefault((WARDS[u[0]], u[1], u[2], u[3]), [r["t"], r["t"]])[1] = r["t"]
    out = []
    for (kind, team, x, y), (first, last) in seen.items():
        if first < start + 2:
            continue  # standing before this stretch began
        owner = None
        before = [r for r in stretch if first - 4 <= r["t"] < first]
        after = [r for r in stretch if first <= r["t"] <= first + 3]
        if before and after:
            index = 5 if kind == "observer" else 6
            was = {h[0]: h[index] for h in before[0]["heroes"] if h[1] == team}
            now = {h[0]: h[index] for h in after[-1]["heroes"] if h[1] == team}
            at = {h[0]: (h[2], h[3]) for h in before[-1]["heroes"]}
            near = lambda n: math.hypot(at[n][0] - x, at[n][1] - y) if n in at else 1e9
            dropped = [n for n in was if n in now and now[n] < was[n]]
            if dropped:
                owner = min(dropped, key=near)
            elif was and near(min(was, key=near)) < 900:
                owner = min(was, key=near)
        out.append({"kind": kind, "team": team, "hero": owner, "placed": round(first, 2), "position": [x, y],
                    "gone": round(last + 0.25, 2) if last < end - 1 else None})
    return out


def convert(gsi: Path, prefix: str, scans: list[Path]):
    rs = rows(gsi)
    match = next(r["match"] for r in rs if r.get("match"))
    reads = []
    for scan in scans:
        mine = []
        for line in open(scan, encoding="utf-8"):
            try:
                e = json.loads(line)
            except ValueError:
                continue
            if e.get("hero") and (e.get("items") or {}).get("wards") and e.get("clock") is not None:
                mine.append(e)
        reads += aligned(mine, rs) if mine else []
    parts = [run for st in stretches(rs) for run in covered(timed(st), reads)]
    for i, s in enumerate(parts):
        samples = [{"t": round(r["t"], 2), "heroes": [{"hero": h[0], "team": h[1], "x": h[2], "y": h[3], "alive": bool(h[4]),
                                                        "observer": h[5], "sentry": h[6]} for h in r["heroes"]]} for r in s]
        sources = [{"t": round(r["t"], 2), "units": [[u[1], u[2], u[3], u[5]] for u in r["units"]
                                                     if u[1] in (2, 3) and (u[5] or 0) > 0 and u[2] is not None]} for r in s]
        picked = [{"hero": r["hero"], "clock": r["clock"], **r["items"]["wards"]} for r in reads if s[0]["at"] <= r["wall"] <= s[-1]["at"]]
        if not picked:
            continue
        wards = wards_of(s)
        path = Path(f"{prefix}-{i}.wards.json")
        path.write_text(json.dumps({"matchId": int(match), "live": True, "samples": samples, "sources": sources,
                                    "wards": wards, "reads": picked}, separators=(",", ":")), encoding="utf-8")
        print(path.name, f"clock {s[0]['clock']}-{s[-1]['clock']}", f"wards {len(wards)}", f"reads {len(picked)}")


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--dota", type=Path, default=DOTA)
    sub = ap.add_subparsers(dest="command", required=True)
    sub.add_parser("setup")
    r = sub.add_parser("record")
    r.add_argument("out", type=Path)
    r.add_argument("seconds", type=float)
    c = sub.add_parser("convert")
    c.add_argument("gsi", type=Path)
    c.add_argument("prefix")
    c.add_argument("scans", nargs="+", type=Path)
    args = ap.parse_args()
    if args.command == "setup":
        setup(args.dota)
    elif args.command == "record":
        record(args.out, args.seconds, args.dota)
    else:
        convert(args.gsi, args.prefix, args.scans)


if __name__ == "__main__":
    main()
