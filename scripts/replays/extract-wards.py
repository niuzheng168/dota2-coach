"""Ward evaluation facts from a replay: every hero's position and ward counts, each team's vision sources, and the
wards actually placed. Replay all-map data for offline evaluation of the coach's ward guesses only (never live input).

    .tmp/replay-tools/Scripts/python.exe -I scripts/replays/extract-wards.py REPLAY.dem OUT.json

Uses public ReplayParser/entity callbacks from gem-dota 0.14.0. Positions are cell*128+vec-16384, as in extract.py.
"""
from __future__ import annotations
import argparse
import importlib.metadata
import json
from pathlib import Path

from gem.parser import ReplayParser
from gem.state.entities import EntityOp

NULL_HANDLE = 0xFFFFFF
OFFSET = 16384
STEP = 8  # ticks between hero samples (30 a second); creeps and towers once a second
WARDS = {"CDOTA_NPC_Observer_Ward": "observer", "CDOTA_NPC_Observer_Ward_TrueSight": "sentry"}
SOURCES = ("CDOTA_BaseNPC_Creep_Lane", "CDOTA_BaseNPC_Creep_Siege", "CDOTA_BaseNPC_Tower")


def position(e):
    values = [e.get(f"CBodyComponent.{n}") for n in ("m_cellX", "m_cellY", "m_vecX", "m_vecY")]
    if any(v is None for v in values):
        return None
    cx, cy, x, y = values
    return [round(cx * 128 + x - OFFSET, 1), round(cy * 128 + y - OFFSET, 1)]


def extract(dem: Path, out: Path):
    if importlib.metadata.version("gem-dota") != "0.14.0":
        raise ValueError("Parser version must match the audited pin")
    p = ReplayParser(str(dem))
    wards: dict[str, dict] = {}
    samples, sources = [], []
    state = {"resource": None, "next": 0, "nextSource": 0}

    def name(e):
        table = p.string_tables.get_by_name("EntityNames")
        index = e.get("m_pEntity.m_nameStringTableIndex")
        return table.items[index][0] if table and index in table.items else None

    def heroes():
        resource = state["resource"]
        out = []
        for i in range(30) if resource is not None else ():
            handle = resource.get(f"m_vecPlayerTeamData.{i:04d}.m_hSelectedHero")
            e = p.entity_manager.find_by_handle(handle) if handle not in (None, NULL_HANDLE) else None
            if e is not None and e.get_class_name().startswith("CDOTA_Unit_Hero_"):
                out.append(e)
        return out

    def ward_counts(e):
        observer = sentry = 0
        for i in range(9):  # main slots and backpack
            handle = e.get(f"m_hItems.{i:04d}")
            item = p.entity_manager.find_by_handle(handle) if handle not in (None, NULL_HANDLE) else None
            if item is None:
                continue
            cls, charges = item.get_class_name(), item.get("m_iCurrentCharges") or 0
            if cls == "CDOTA_Item_ObserverWard":
                observer += charges
            elif cls == "CDOTA_Item_SentryWard":
                sentry += charges
            elif cls == "CDOTA_Item_Ward_Dispenser":
                observer += charges
                sentry += item.get("m_iSecondaryCharges") or 0
        return observer, sentry

    def entity(e, op):
        cls = e.get_class_name()
        if cls == "CDOTA_PlayerResource":
            state["resource"] = e
        if cls in WARDS:
            key = f"{e.get_index()}:{e.get_serial()}"
            row = wards.get(key)
            if row is None and op & EntityOp.CREATED:
                row = wards[key] = {"kind": WARDS[cls], "team": e.get("m_iTeamNum"), "owner": None, "player": None,
                                    "placed": p.tick, "position": None, "gone": None}
            if row is None:
                return
            # Owner and position may arrive after the creation delta.
            row["owner"] = row["owner"] if row["owner"] not in (None, NULL_HANDLE) else e.get("m_hOwnerEntity")
            row["player"] = row["player"] if row["player"] not in (None, -1) else e.get("m_nPlayerOwnerID")
            row["position"] = row["position"] or position(e)
            if row["gone"] is None and (op & EntityOp.DELETED or (e.get("m_lifeState") or 0) != 0):
                row["gone"] = p.tick

    def tick(_):
        t = p.game_time_s
        if t is None or t < -60 or p.post_game_tick is not None or p.tick < state["next"]:
            return
        state["next"] = p.tick + STEP
        rows = []
        for e in heroes():
            pos = position(e)
            if pos is None:
                continue
            observer, sentry = ward_counts(e)
            rows.append({"hero": name(e), "team": e.get("m_iTeamNum"), "owner": e.get("m_hOwnerEntity"),
                         "player": e.get("m_iPlayerID"), "x": pos[0], "y": pos[1],
                         "alive": e.get("m_lifeState") == 0, "observer": observer, "sentry": sentry})
        samples.append({"tick": p.tick, "heroes": rows})
        if p.tick >= state["nextSource"]:
            state["nextSource"] = p.tick + 30
            units = [(e.get("m_iTeamNum"), position(e), e.get_class_name()) for e in p.entity_manager.all_active()
                     if e.get_class_name() in SOURCES and e.get("m_lifeState") == 0]
            sources.append({"tick": p.tick, "units": [[team, pos[0], pos[1], "tower" if "Tower" in cls else "creep"]
                                                       for team, pos, cls in units if pos is not None]})

    p.on_entity(entity)
    p.on_tick_start(tick)
    p.parse()
    if p.parse_error is not None:
        raise ValueError(f"Parse error: {p.parse_error}")
    clock = p.game_clock
    at = lambda tick: None if tick is None else round(clock.game_time_at(tick), 2)
    owners, players = {}, {}
    for s in samples:
        s["t"] = at(s.pop("tick"))
        for h in s["heroes"]:
            owners[h["owner"]] = players[h.pop("player")] = h["hero"]
    for s in sources:
        s["t"] = at(s.pop("tick"))
    placed = [{**w, "hero": owners.get(w["owner"]) or players.get(w["player"]), "owner": None, "player": None,
               "placed": at(w["placed"]), "gone": at(w["gone"])} for w in wards.values() if w["position"] is not None]
    out.write_text(json.dumps({"matchId": p.match_id, "build": p.game_build, "step": STEP, "samples": samples,
                               "sources": sources, "wards": placed}, separators=(",", ":")), encoding="utf-8")
    print(json.dumps({"matchId": p.match_id, "build": p.game_build, "samples": len(samples), "wards": len(placed),
                      "withHero": sum(1 for w in placed if w["hero"])}))


if __name__ == "__main__":
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("replay", type=Path)
    ap.add_argument("out", type=Path)
    args = ap.parse_args()
    extract(args.replay, args.out)
