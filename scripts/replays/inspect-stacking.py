"""Extra high-frequency raw evidence for selected stack windows, never route rules."""
import argparse
from dataclasses import asdict
import json
from pathlib import Path
import sys
sys.dont_write_bytecode = True
from replay_storage import replay_area, set_replay_temp

ap = argparse.ArgumentParser()
ap.add_argument("match_id", type=int)
ap.add_argument("--from-time", type=float, default=600)
ap.add_argument("--to-time", type=float, default=900)
ap.add_argument("--root", type=Path)
args = ap.parse_args()
root = args.root or replay_area()
set_replay_temp(root)
from gem.parser import ReplayParser
from extract import position
p = ReplayParser(str(root / f"{args.match_id}.dem"))
frames, logs, field_samples = [], [], {}
last_tick = -10


def tick(_):
    global last_tick
    t = p.game_time_s
    if t is None or not args.from_time - 10 <= t <= args.to_time + 10 or p.tick - last_tick < 3:
        return
    last_tick = p.tick
    units = []
    for e in p.entity_manager.all_active():
        cls = e.get_class_name()
        if cls not in ("CDOTA_BaseNPC_Creep_Neutral", "CDOTA_Unit_Hero_Luna"):
            continue
        if cls not in field_samples:
            field_samples[cls] = {k: v for k, v in e.to_map().items()
                                 if any(word in k.lower() for word in ("aggro", "attack", "visib", "angle", "owner", "illusion"))}
        units.append({"id": f"{e.get_index()}:{e.get_serial()}", "class": cls,
                      "position": position(e), "health": e.get("m_iHealth"), "lifeState": e.get("m_lifeState"),
                      "spawnerHandle": e.get("m_hNeutralSpawner"), "waiting": e.get("m_bIsWaitingToSpawn"),
                      "playerId": e.get("m_iPlayerID")})
    frames.append({"tick": p.tick, "units": units})


def combat(log):
    if log.log_type in ("DAMAGE", "DEATH") and "luna" in str(log.attacker_name):
        logs.append(asdict(log))


p.on_tick_start(tick)
p.on_combat_log_entry(combat)
p.parse()
if p.parse_error or p.post_game_tick is None or p.match_id != args.match_id:
    raise ValueError("Incomplete replay")
for f in frames:
    f["time"] = p.game_clock.game_time_at(f["tick"])
logs = [{**e, "time": p.game_clock.game_time_at(e["tick"])} for e in logs]
logs = [e for e in logs if args.from_time - 10 <= e["time"] <= args.to_time + 10]
output = root / "study" / f"{args.match_id}.stacking-detail.json"
output.parent.mkdir(parents=True, exist_ok=True)
with output.open("x", encoding="utf8") as f:
    json.dump({"matchId": p.match_id, "build": p.game_build, "complete": True,
               "scope": "omniscient-offline-replay-only", "clock": asdict(p.game_clock),
               "fieldSamples": field_samples, "frames": frames, "combatLog": logs}, f, separators=(",", ":"))
print(json.dumps({"matchId": p.match_id, "frames": len(frames), "combatEntries": len(logs),
                  "fieldSamples": field_samples}, indent=2))
