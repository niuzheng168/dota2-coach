"""Offline QA only: separate per-team, causal minimap-like inputs from a full-map scoring oracle.
The live binaries do not ship/read either file. Missing NPC visibility bits are NOT filled from truth.
Usage: python -B scripts/replays/lane-wave-visibility.py 9099209663
"""
import argparse
import json
import math
from importlib.metadata import version
from pathlib import Path

from extract import LANE, position
from replay_storage import replay_area, set_replay_temp
from team_view import visibility


def run(match, root, out):
    from gem.parser import ReplayParser
    from gem.state.entities import EntityOp
    assert version("gem-dota") == "0.14.0"
    dem = root / f"{match}.dem"
    mapdata = json.loads((Path(__file__).resolve().parents[2] / "data/minute-farm-742-client.json").read_text(encoding="utf8"))
    paths = {side: {l["id"].split("_")[-1]: l["points"][0] for l in mapdata["lanes"] if key in l["id"]}
             for side, key in [(2, "_good_"), (3, "_bad_")]}
    out.mkdir(parents=True, exist_ok=True)
    set_replay_temp(out)
    p = ReplayParser(str(dem))
    teams, active, births = {}, {}, {}
    last, samples = -1, 0
    def entity(e, op):
        cls = e.get_class_name()
        if cls in ("CDOTADataRadiant", "CDOTA_DataRadiant", "CDOTADataDire", "CDOTA_DataDire"):
            teams[2 if "Radiant" in cls else 3] = e
        if cls not in (LANE, "CDOTA_BaseNPC_Creep_Siege"): return
        k = (e.get_index(), e.get_serial())
        if op & (EntityOp.LEFT | EntityOp.DELETED): active.pop(k, None)
        elif op & (EntityOp.CREATED | EntityOp.ENTERED): active[k] = e
    # A partial/incorrect build extraction cannot be mistaken for a validated run.
    with (out / f"{match}.views.partial").open("w", encoding="utf8") as views, (out / f"{match}.oracle.partial").open("w", encoding="utf8") as oracle:
        def sample(_):
            nonlocal last, samples
            t = p.game_time_s
            if t is None or t < 0 or int(t) <= last or p.post_game_tick is not None: return
            last = int(t)
            if not all(k in teams and teams[k].active for k in (2, 3)): return
            words = {2: {}, 3: {}}
            seen = {2: [], 3: []}
            truth = []
            names = p.string_tables.get_by_name("UnitNames")
            for (idx, serial), e in active.items():
                if not e.active or e.get("m_lifeState") != 0 or e.get("m_bIsWaitingToSpawn") is not False: continue
                side = e.get("m_iTeamNum")
                pos = position(e)
                ni = e.get("m_iUnitNameStringTableIndex")
                if side not in paths or pos is None or names is None or ni not in names.items: continue
                name = names.items[ni][0]
                x, y = round(pos["x"]), round(pos["y"])
                identity = (idx, serial)
                if identity not in births:
                    d = sorted((math.hypot(x-a, y-b), lane) for lane, (a,b) in paths[side].items())
                    births[identity] = d[0][1] if d[0][0] < 900 and d[1][0]-d[0][0] > 512 else "?"
                vs = [visibility(teams[team], idx, words[team]) for team in (2,3)]
                for i, team in enumerate((2,3)):
                    if side == team or vs[i] == "visible":
                        seen[team].append([name, side, x, y])
                truth.append([f"{idx}:{serial}", side, births[identity], x, y, *vs])
            def line(f, value): f.write(json.dumps(value, separators=(",", ":")) + "\n")
            line(views, [last, seen[2], seen[3]]); line(oracle, [last, truth]); samples += 1
        p.on_entity(entity)
        p._on_packet_end(sample)
        p.parse()
    if p.parse_error or p.match_id != match or p.game_build != 6989 or p.post_game_tick is None or samples < 300:
        raise ValueError("Requires a complete matching build 6989 replay")
    for name in ("views", "oracle"):
        (out / f"{match}.{name}.partial").replace(out / f"{match}.{name}.jsonl")
    print(f"{match}: {samples} game seconds; separate team-view inputs and offline oracle: {out}")


if __name__ == "__main__":
    ap = argparse.ArgumentParser()
    ap.add_argument("match", type=int)
    ap.add_argument("--root", type=Path, default=replay_area("farm742"))
    ap.add_argument("--out", type=Path, default=replay_area("farm742") / "tmp" / "wave-visibility-6989")
    a = ap.parse_args()
    run(a.match, a.root, a.out)
