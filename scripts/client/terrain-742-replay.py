"""Read one pinned, *finished* build-6989 DEM. No Steam/game connection.

Only the selected real hero's position, motion flag, base-speed field and
game clock are retained. The speed used to score an ETA is NOT that base field.
"""
import argparse
import base64
import hashlib
import importlib.metadata
import json
from pathlib import Path
import struct
import zlib

from gem.parser import ReplayParser

ROOT = Path(__file__).resolve().parents[2]
NPC = {"luna": "CDOTA_Unit_Hero_Luna", "troll_warlord": "CDOTA_Unit_Hero_TrollWarlord"}
# dtick, dGameMs, dx32, dy32, dz32, base speed (NOT effective), state bits
RECORD = struct.Struct("<iiiiiHB")


def capture(match, dem):
    if importlib.metadata.version("gem-dota") != "0.14.0":
        raise ValueError("Expected gem-dota 0.14.0")
    with dem.open("rb") as f:
        if hashlib.file_digest(f, "sha256").hexdigest() != match["demSha256"]:
            raise ValueError("DEM hash differs from the 7.42 study")
    p = ReplayParser(str(dem))
    resource, last, hero_handle, samples = None, None, None, []

    def entity(e, _op):
        nonlocal resource
        if e.get_class_name() == "CDOTA_PlayerResource":
            resource = e

    def tick(_):
        nonlocal last, hero_handle
        if (resource is None or p.game_time_s is None or not 295 <= p.game_time_s <= 1202
                or last is not None and p.tick - last < 3):
            return
        if hero_handle is None:
            handles = [resource.get(f"m_vecPlayerTeamData.{i:04d}.m_hSelectedHero") for i in range(10)]
            active = [(h, p.entity_manager.find_by_handle(h)) for h in handles if h not in (None, 0xffffff)]
            matches = [h for h, e in active if e and e.get_class_name() == NPC[match["hero"]]
                       and e.get("m_iTeamNum") == (2 if match["team"] == "radiant" else 3)]
            if len(matches) != 1:
                raise ValueError("Expected exactly one player's selected hero, not an illusion")
            hero_handle = matches[0]
        e = p.entity_manager.find_by_handle(hero_handle)
        if not e or e.get_class_name() != NPC[match["hero"]]:
            raise ValueError("Lost selected hero")
        # 128-unit Source 2 cells, 1/32-unit recorded XYZ, axes not flipped.
        xyz = []
        for axis in "XYZ":
            cell, vec = (e.get("CBodyComponent." + field + axis) for field in ("m_cell", "m_vec"))
            value = (cell * 128 + vec - 16384) * 32
            if not value.is_integer():
                raise ValueError("Position precision changed")
            xyz.append(int(value))
        life, waiting, health, moving, base = (e.get(k) for k in
            ("m_lifeState", "m_bIsWaitingToSpawn", "m_iHealth", "m_bIsMoving", "m_iMoveSpeed"))
        if None in (life, waiting, health, moving, base):
            raise ValueError("Missing state; cannot certify continuity")
        state = int(life == 0 and not waiting and health > 0) | (int(moving) << 1)
        samples.append((p.tick, xyz, base, state))
        last = p.tick

    p.on_entity(entity)
    p.on_tick_start(tick)
    p.parse()
    if p.parse_error or not p.post_game_tick or p.match_id != match["id"] or p.game_build != 6989:
        raise ValueError("Incomplete/wrong DEM or build")
    previous, payload = [0] * 5, bytearray()
    for tickno, xyz, base, flags in samples:
        time = p.game_clock.game_time_at(tickno)  # final, pause-aware clock
        if 297 <= time <= 1201:
            values = [tickno, round(time * 1000), *xyz]
            payload += RECORD.pack(*(v - q for v, q in zip(values, previous)), base, flags)
            previous = values
    count = len(payload) // RECORD.size
    if count < 5000:
        raise ValueError("Incomplete 5–20 minute capture")
    return {"id": p.match_id, "hero": match["hero"], "team": match["team"], "build": p.game_build,
            "demSha256": match["demSha256"], "samples": count,
            "samplesSha256": hashlib.sha256(payload).hexdigest(),
            "samplesZlib": base64.b64encode(zlib.compress(payload, 9)).decode("ascii")}


if __name__ == "__main__":
    ap = argparse.ArgumentParser(description=__doc__)
    ap.add_argument("match_id", type=int)
    ap.add_argument("--dem-root", type=Path, default=ROOT / ".tmp/farm742")
    args = ap.parse_args()
    study = json.loads((ROOT / "data/farming-742-routes.json").read_text(encoding="utf-8"))
    m = next(m for m in study["matches"] if m["id"] == args.match_id and m["build"] == 6989)
    result = capture(m, args.dem_root / f"{args.match_id}.dem")
    dest = ROOT / ".tmp/farm742-terrain" / f"{args.match_id}.trace.json"
    dest.parent.mkdir(parents=True, exist_ok=True)
    dest.write_text(json.dumps(result, separators=(",", ":")) + "\n", encoding="utf-8")
    print(f"{dest.name}: {result['samples']} observed XYZ/state rows, {result['samplesSha256']}")
