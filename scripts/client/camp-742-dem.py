"""Offline-only: keep neutral damage, entity HP/death transitions and selected-body fields.

The stock facts retain deaths but not DAMAGE. Do not infer contact from the first
kill or use hero proximity. Run with the same pinned gem-dota as extract.py.
"""
import argparse
import hashlib
from importlib.metadata import version
import json
from pathlib import Path

from gem.parser import ReplayParser


HERO_FIELDS = ("m_iDamageMin", "m_iDamageMax", "m_iDamageBonus", "m_flAgilityTotal",
               "m_flBaseAttackTime", "m_iAttackCapabilities", "m_iCurrentLevel")
NEUTRAL_CLASS = "CDOTA_BaseNPC_Creep_Neutral"


def extract(match_id, root, out):
    if version("gem-dota") != "0.14.0":
        raise ValueError("Expected gem-dota 0.14.0")
    facts = json.loads((root / f"{match_id}.facts.json").read_bytes())
    if not facts["complete"] or facts["schemaVersion"] != 4 or facts["matchId"] != match_id:
        raise ValueError("Need complete, matching schema-4 facts")
    body = facts["heroPath"][0]
    body_id, hero, team = body["id"], body["unitName"], body["team"]
    build, expected_hash, end_tick = facts["gameBuild"], facts["demSha256"], facts["postGameTick"]
    last_tick = next(f["tick"] for f in facts["frames"] if f["gameTime"] > 1201)
    del facts  # the replay, not saved snapshots, supplies per-tick damage evidence
    dem = root / f"{match_id}.dem"
    with dem.open("rb") as stream:
        if hashlib.file_digest(stream, "sha256").hexdigest() != expected_hash:
            raise ValueError("DEM/facts content mismatch")
    parser = ReplayParser(str(dem))
    last, transitions, body_stats, combat, gold, census, enhancements = {}, [], [], [], [], [], []
    last_census_s = None

    def census_tick(_):
        # Across the entire match, well away from upgrade/minute boundaries.
        # These fields are the entity's gross roll limits, NOT a gold transfer.
        nonlocal last_census_s
        sec = parser.game_time_s
        if sec is None or sec < 65 or sec % 30 != 5 or sec == last_census_s or parser.post_game_tick is not None:
            return
        last_census_s = sec
        table = parser.string_tables.get_by_name("UnitNames")
        for e in parser.entity_manager.all_active():
            if (e.get_class_name() != NEUTRAL_CLASS or e.get("m_bIsWaitingToSpawn") is not False or
                    e.get("m_lifeState") != 0 or e.get("m_iTeamNum") != 4):
                continue
            name_index = e.get("m_iUnitNameStringTableIndex")
            handle = e.get("m_hNeutralSpawner")
            if table and name_index in table.items and handle not in (None, 0xffffff):
                census.append([parser.tick, f"{e.get_index()}:{e.get_serial()}", table.items[name_index][0],
                               handle, e.get("m_iMaxHealth"), e.get("m_iGoldBountyMin"), e.get("m_iGoldBountyMax")])

    def entity(e, _):
        if parser.tick > last_tick:
            return
        cls = e.get_class_name()
        if cls != NEUTRAL_CLASS and cls != body["class"]:
            return
        uid = f"{e.get_index()}:{e.get_serial()}"
        if cls != NEUTRAL_CLASS:
            if uid == body_id:
                fields = [e.get(k) for k in HERO_FIELDS]
                if not body_stats or body_stats[-1][1:] != fields:
                    body_stats.append([parser.tick, *fields])
                # The full facts cap inventory at slot 16; the enhancement is a separate slot (17).
                handle = e.get("m_hItems.0017")
                ench = parser.entity_manager.find_by_handle(handle) if handle not in (None, 0xffffff) else None
                name_index = ench.get("m_pEntity.m_nameStringTableIndex") if ench else None
                table = parser.string_tables.get_by_name("EntityNames")
                item = (table.items[name_index][0] if table and name_index in table.items else
                        None if handle == 0xffffff else "unknown")
                fields = [item, ench.get("m_iLevel") if ench else None]
                if not enhancements or enhancements[-1][1:] != fields:
                    enhancements.append([parser.tick, *fields])
            return
        hp, max_hp, life = (e.get(k) for k in ("m_iHealth", "m_iMaxHealth", "m_lifeState"))
        hit = e.get("m_flLastDamageTime")
        previous = last.get(uid)
        if previous and hp is not None and previous[0] is not None and previous[2] == 0:
            if hp < previous[0] or (hit and hit != previous[3]) or life not in (None, 0):
                transitions.append([parser.tick, uid, previous[0], hp, max_hp, life,
                                    e.get("m_iGoldBountyMin"), e.get("m_iGoldBountyMax")])
        last[uid] = (hp, max_hp, life, hit)

    def log(e):
        if e.tick > last_tick:
            return
        if e.log_type in ("DAMAGE", "DEATH") and (e.target_name.startswith("npc_dota_neutral_") or
                                                   (e.log_type == "DEATH" and e.target_team == 4)):
            combat.append([e.tick, str(e.log_type), e.target_name, e.attacker_name,
                           e.attacker_team, e.attacker_is_illusion if e.attacker_is_illusion_present else None,
                           e.inflictor_name, e.value, e.damage_type, e.target_team])
        if e.log_type == "GOLD" and e.target_name == hero and e.gold_reason == 14:
            gold.append([e.tick, e.value])

    parser.on_entity(entity)
    parser.on_tick_start(census_tick)
    parser.on_combat_log_entry(log)
    parser.parse()
    if (parser.parse_error or parser.match_id != match_id or parser.game_build != build or
            parser.post_game_tick is None or parser.post_game_tick != end_tick):
        raise ValueError("Incomplete/mismatched DEM parse")
    output = dict(matchId=match_id, build=build, demSha256=expected_hash, endTick=end_tick,
                  lastTick=last_tick, bodyId=body_id, hero=hero, team=team,
                  heroFields=HERO_FIELDS, bodyStats=body_stats, transitions=transitions, combat=combat, gold=gold,
                  census=census, enhancements=enhancements)
    out.mkdir(parents=True, exist_ok=True)
    (out / f"{match_id}.damage.json").write_text(json.dumps(output, separators=(",", ":")), encoding="utf8")
    print(f"{match_id}: {len(transitions)} neutral transitions, {len(combat)} damage/death entries")


if __name__ == "__main__":
    ap = argparse.ArgumentParser(description=__doc__)
    ap.add_argument("match_id", type=int)
    ap.add_argument("--root", type=Path, default=Path(".tmp/farm742"))
    ap.add_argument("--out", type=Path, default=Path(".tmp/farm742-camps"))
    args = ap.parse_args()
    extract(args.match_id, args.root, args.out)
