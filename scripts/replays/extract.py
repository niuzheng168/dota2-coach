"""Extract recorded facts, not gem's heuristic farming-route analysis.

Uses public ReplayParser/entity callbacks from gem-dota 0.14.0. Full snapshots
remain local; compact reports can be generated separately. Replay-wide hidden
information is never passed to the live coaching planner.
"""
from __future__ import annotations
import argparse
from collections import Counter
from dataclasses import asdict
import hashlib
import importlib.metadata
import json
from pathlib import Path
import time

from gem.parser import ReplayParser
from gem.state.entities import EntityOp

NULL_HANDLE = 0xFFFFFF
OFFSET = 16384
NEUTRAL = "CDOTA_BaseNPC_Creep_Neutral"
LANE = "CDOTA_BaseNPC_Creep_Lane"
TOWER = "CDOTA_BaseNPC_Tower"
SPAWNER = "CDOTA_NeutralSpawner"
HERO_CLASSES = {"luna": "CDOTA_Unit_Hero_Luna", "troll_warlord": "CDOTA_Unit_Hero_TrollWarlord"}


def position(entity):
    values = [entity.get(f"CBodyComponent.{name}") for name in ("m_cellX", "m_cellY", "m_vecX", "m_vecY")]
    if any(v is None for v in values):
        return None
    cx, cy, x, y = values
    return {"x": cx * 128 + x - OFFSET, "y": cy * 128 + y - OFFSET}


def extract(dem: Path, output: Path, expected_id: int, hero_key: str = "luna", route_core: bool = False):
    if importlib.metadata.version("gem-dota") != "0.14.0":
        raise ValueError("Parser version must match the audited pin")
    if output.exists():
        raise FileExistsError("Existing extracted evidence will not be overwritten")
    hero_class = HERO_CLASSES[hero_key]
    hero_name = "npc_dota_hero_" + hero_key
    p = ReplayParser(str(dem))
    creatures, towers, spawners, camps, frames, hero_path, logs = {}, {}, {}, {}, [], [], []
    farm_damage = []
    observed_classes = Counter()
    resource = None
    team_entities = {}
    last_second = None
    last_path_tick = None

    def name(e):
        table = p.string_tables.get_by_name("EntityNames")
        # m_iUnitNameStringTableIndex belongs to a DIFFERENT table. Mixing it
        # with EntityNames silently yields camp/ability names for heroes.
        for field in ("m_pEntity.m_nameStringTableIndex", "m_pEntity.m_nameStringableIndex"):
            index = e.get(field)
            if table and index is not None and index in table.items:
                value = table.items[index][0]
                if value:
                    return value
        return None

    def identity(e):
        return f"{e.get_index()}:{e.get_serial()}"

    def unit_name(e):
        table = p.string_tables.get_by_name("UnitNames")
        index = e.get("m_iUnitNameStringTableIndex")
        return table.items[index][0] if table and index in table.items else None

    def unit(e):
        pos = position(e)
        return {
            "id": identity(e), "class": e.get_class_name(), "name": name(e), "unitName": unit_name(e), "position": pos,
            "team": e.get("m_iTeamNum"), "health": e.get("m_iHealth"),
            "maxHealth": e.get("m_iMaxHealth"), "lifeState": e.get("m_lifeState"),
            "waitingToSpawn": e.get("m_bIsWaitingToSpawn"), "moving": e.get("m_bIsMoving"),
            "baseMoveSpeedField": e.get("m_iMoveSpeed"), "attackRangeField": e.get("m_iAttackRange"),
            "spawnerHandle": e.get("m_hNeutralSpawner"),
        }

    def entity(e, op):
        nonlocal resource
        cls = e.get_class_name()
        if op & EntityOp.CREATED:
            observed_classes[cls] += 1
        if cls == "CDOTA_PlayerResource":
            resource = e
        if cls in ("CDOTA_DataRadiant", "CDOTA_DataDire"):
            team_entities[e.get("m_iTeamNum")] = e
        if cls == "CDOTAGamerulesProxy" and not camps:
            for i in range(64):
                prefix = f"m_pGameRules.m_NeutralSpawnBoxes.{i:04d}."
                camp_id = e.get(prefix + "strCampName")
                if camp_id:
                    camps[camp_id] = {
                        "id": camp_id, "spawnerIndex": e.get(prefix + "iSpawnerEntIndex"),
                        "type": e.get(prefix + "nCampType"),
                        "min": e.get(prefix + "m_vMinBounds"), "max": e.get(prefix + "m_vMaxBounds"),
                        "origin": e.get(prefix + "vSpawnBoxOrigin"),
                    }
        if cls == SPAWNER and not op & EntityOp.DELETED:
            row = spawners.setdefault(e.get_index(), {"index": e.get_index(), "position": position(e), "types": []})
            if row["position"] is None:
                row["position"] = position(e)
            value = e.get("m_Type")
            if value is not None and (not row["types"] or row["types"][-1]["type"] != value):
                row["types"].append({"tick": p.tick, "type": value})
        if cls not in (NEUTRAL, LANE, TOWER):
            return
        rows = towers if cls == TOWER else creatures
        key = identity(e)
        row = rows.get(key)
        if row is None:
            row = rows[key] = {**unit(e), "createdTick": p.tick, "spawnedTick": None, "diedTick": None}
        if not row["name"]:
            row["name"] = name(e)
        if not row["unitName"]:
            row["unitName"] = unit_name(e)
        if row["position"] is None:
            row["position"] = position(e)
        if row["spawnedTick"] is None and e.get("m_bIsWaitingToSpawn") is False:
            row["spawnedTick"] = p.tick
            row["spawnPosition"] = position(e)
        if row["diedTick"] is None and e.get("m_lifeState") not in (None, 0):
            row["diedTick"] = p.tick
            row["deathPosition"] = position(e)
        if op & EntityOp.DELETED:
            row["deletedTick"] = p.tick

    def selected_heroes():
        if resource is None:
            return []
        heroes = []
        for i in range(30):
            prefix = f"m_vecPlayerTeamData.{i:04d}."
            handle = resource.get(prefix + "m_hSelectedHero")
            e = p.entity_manager.find_by_handle(handle) if handle not in (None, NULL_HANDLE) else None
            if e is None or not e.get_class_name().startswith("CDOTA_Unit_Hero_"):
                continue
            slot = resource.get(prefix + "m_iTeamSlot")
            heroes.append((e, slot))
        return heroes

    def inventory(e, prefix, count):
        result = []
        for i in range(count):
            handle = e.get(f"{prefix}.{i:04d}")
            item = p.entity_manager.find_by_handle(handle) if handle not in (None, NULL_HANDLE) else None
            if item is not None:
                result.append({"slot": i, "class": item.get_class_name(), "name": name(item),
                               "level": item.get("m_iLevel"), "cooldown": item.get("m_fCooldown"),
                               "toggleState": item.get("m_bToggleState")})
        return result

    def hero(e, team_slot, details):
        row = {**unit(e), "level": e.get("m_iCurrentLevel"), "mana": e.get("m_flMana"),
               "playerIdField": e.get("m_iPlayerID"), "teamSlot": team_slot}
        team = team_entities.get(row["team"])
        fields = ("m_iLastHitCount", "m_iNetWorth", "m_iTotalEarnedGold", "m_iCreepKillGold",
                  "m_iNeutralKillGold", "m_iHeroKillGold", "m_iBuildingGold", "m_iIncomeGold",
                  "m_iCampsStacked")
        row["economy"] = {k: team.get(f"m_vecDataTeam.{team_slot:04d}.{k}") for k in fields} if team is not None and isinstance(team_slot, int) else None
        if details:
            row["items"] = inventory(e, "m_hItems", 17)
            row["abilities"] = inventory(e, "m_vecAbilities", 32)
        return row

    def capture_frame(terminal=False):
        selected = selected_heroes()
        if route_core:
            # Same economy/inventory counters, geometry, kill attribution and
            # game-clock samples as full extraction. Don't materialize every
            # unrelated unit every second; the postgame parity frame needs all 10.
            return {"tick": p.tick, "roundedGameTime": p.game_time_s,
                    "heroes": [hero(e, slot, e.get_class_name() == hero_class) for e, slot in selected
                               if terminal or e.get_class_name() == hero_class]}
        units = [unit(e) for e in p.entity_manager.all_active() if e.get_class_name() in (NEUTRAL, LANE, TOWER)
                 and e.get("m_lifeState") == 0 and e.get("m_bIsWaitingToSpawn") is False]
        return {"tick": p.tick, "roundedGameTime": p.game_time_s, "units": units,
                "heroes": [hero(e, slot, True) for e, slot in selected]}

    def tick(_):
        nonlocal last_second, last_path_tick
        seconds = p.game_time_s
        if seconds is None or seconds < 0 or p.post_game_tick is not None:
            return
        if route_core and seconds > 1202:
            # No spatial events or canonical economic boundaries after 20:00
            # are published. Keep walking the replay and sampling loadouts for
            # firstHeld, but not an unused 0.13s body history late in the match.
            if seconds != last_second:
                last_second = seconds
                frames.append(capture_frame())
            return
        if last_path_tick is None or p.tick - last_path_tick >= 3:
            last_path_tick = p.tick
            for e, slot in selected_heroes():
                if e.get_class_name() == hero_class:
                    hero_path.append({"tick": p.tick, **hero(e, slot, False)})
        if seconds == last_second:
            return
        last_second = seconds
        # All-map observed replay snapshot, NOT information available to the player.
        frames.append(capture_frame())

    def combat(log):
        # Raw recorded hero events. Do not infer camp/kill credit by nearest hero.
        if (route_core and log.log_type == "DAMAGE" and log.attacker_name == hero_name
                and str(log.target_name).startswith("npc_dota_neutral_") and log.target_team == 4):
            farm_damage.append([log.tick, log.target_name, log.value, log.attacker_team,
                                log.attacker_is_illusion if log.attacker_is_illusion_present else None])
        if any(hero_key in str(getattr(log, k, "")) for k in ("attacker_name", "target_name", "inflictor_name")):
            if str(log.log_type) in ("DEATH", "GOLD", "ABILITY", "ITEM", "PURCHASE"):
                logs.append(asdict(log))

    p.on_entity(entity)
    p.on_tick_start(tick)
    p.on_combat_log_entry(combat)
    # Flush after the terminal packet's entity deltas, not the preceding 1s
    # sample. Otherwise final last-hit totals can be low by one.
    p.on_game_end(lambda _: frames.append(capture_frame(terminal=True)))
    started = time.monotonic()
    p.parse()
    if p.parse_error is not None or p.post_game_tick is None or p.match_id != expected_id:
        raise ValueError(f"Incomplete/mismatched replay: id={p.match_id}, postgame={p.post_game_tick}, error={p.parse_error}")
    frames = [f for f in frames if f["tick"] <= p.post_game_tick]
    hero_path = [h for h in hero_path if h["tick"] <= p.post_game_tick]
    if not hero_path or any(h["name"] != hero_name for h in hero_path):
        raise ValueError("Selected hero/name table mismatch")
    clock = p.game_clock
    for frame in frames:
        frame["gameTime"] = clock.game_time_at(frame["tick"])
    for row in hero_path:
        row["gameTime"] = clock.game_time_at(row["tick"])
    for row in [*creatures.values(), *towers.values()]:
        for event in ("created", "spawned", "died", "deleted"):
            value = row.get(event + "Tick")
            if value is not None:
                row[event + "GameTime"] = clock.game_time_at(value)
    for row in spawners.values():
        for change in row["types"]:
            change["gameTime"] = clock.game_time_at(change["tick"])
    with dem.open("rb") as f:
        dem_hash = hashlib.file_digest(f, "sha256").hexdigest()
    result = {
        "schemaVersion": 4, "parser": {"name": "gem-dota", "version": "0.14.0"},
        "matchId": p.match_id, "gameBuild": p.game_build, "postGameTick": p.post_game_tick,
        "complete": True, "durationSeconds": clock.game_time_at(p.post_game_tick),
        "demSha256": dem_hash,
        "coordinateTransform": {"cellSize": 128, "offsetSubtract": OFFSET, "method": "cell*128+vec-16384; checked against named replay camp boxes and static towers"},
        "clock": asdict(clock), "classes": dict(observed_classes),
        "camps": list(camps.values()), "spawners": list(spawners.values()), "towers": list(towers.values()),
        "creatures": list(creatures.values()), "heroPath": hero_path, "frames": frames,
        # Keep historical Luna facts byte-compatible; never label Troll events as Luna's.
        ("lunaCombatLog" if hero_key == "luna" else "trollCombatLog"): logs,
        "limitations": ["Replay all-map state is audit-only, not player-visible live context.",
                        "m_iMoveSpeed is a recorded base field, NOT necessarily effective speed with modifiers.",
                        "No heuristic camp-route classification, kill-credit proximity, or strategy selection.",
                        "Trees/river/collision and exact subpatch require separate evidence."],
    }
    if route_core:
        result["extractionScope"] = "route-core"
        result["farmDamage"] = farm_damage
        result["farmDamageFields"] = ["tick", "neutralName", "damage", "attackerTeam", "attackerIllusion"]
    output.parent.mkdir(parents=True, exist_ok=True)
    target = output.with_suffix(output.suffix + ".partial") if route_core else output
    with target.open("x", encoding="utf-8") as f:
        json.dump(result, f, separators=(",", ":"), ensure_ascii=False)
    if route_core:
        target.rename(output)
    print(json.dumps({"matchId": p.match_id, "build": p.game_build, "elapsedSeconds": round(time.monotonic()-started, 2),
                      "frames": len(frames), "hero": hero_key, "heroSamples": len(hero_path), "camps": len(camps),
                      "spawners": len(spawners), "towers": len(towers), "creatures": len(creatures)}, indent=2))


if __name__ == "__main__":
    ap = argparse.ArgumentParser()
    ap.add_argument("match_id", type=int)
    ap.add_argument("--root", type=Path, default=Path(".tmp/replays"))
    ap.add_argument("--hero", choices=HERO_CLASSES, default="luna")
    ap.add_argument("--route-core", action="store_true",
                    help="Focus per-second facts on the selected hero; retain terminal parity and spatial evidence.")
    args = ap.parse_args()
    extract(args.root / f"{args.match_id}.dem", args.root / f"{args.match_id}.facts.json", args.match_id,
            args.hero, args.route_core)
