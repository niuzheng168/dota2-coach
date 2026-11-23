"""Offline, one playing team's map only. Never export a hidden enemy's current position.

Same Source 2 NPC visibility words and completed-packet boundary as the pinned
gem 0.14.0 VisibilityExtractor. A missing word is *unknown*, not fog. Sampling is
once per game second, not continuously and not the player's camera or attention.
"""
import argparse
import hashlib
from importlib.metadata import version
import json
import math
from pathlib import Path
import re

from extract import HERO_CLASSES, LANE, NEUTRAL, TOWER, NULL_HANDLE, position
from spell_witness import own_items, visible_sheet, witness


def visibility(team_data, index, words):
    word = index >> 6
    if not 0 <= word < 256 or team_data is None or not team_data.active:
        return "unknown"
    if word not in words:
        words[word] = team_data.get_uint64(f"m_bNPCVisibleState.{word:04d}")
    n = words[word]
    return "unknown" if n is None else "visible" if n & (1 << (index & 63)) else "hidden"


def xy(e):
    p = position(e)
    return [round(p["x"]), round(p["y"])] if p and all(math.isfinite(v) for v in p.values()) else None


def pack(rows, origin, allies, towers):
    """Nearest *visible* group: not a guaranteed whole wave/camp, route or collectable gold."""
    if not rows or origin is None:
        return None
    distance = lambda a, b: round(math.dist(a, b))
    head = min(rows, key=lambda r: math.dist(origin, r[1]))
    group = [r for r in rows if (r[6] == head[6] if head[6] is not None else math.dist(head[1], r[1]) <= 600)]
    rolls = [(r[4], r[5]) for r in group]
    gross = [sum(a for a, _ in rolls), sum(b for _, b in rolls)] if all(
        type(a) is int and type(b) is int and 0 <= a <= b for a, b in rolls) else None
    return {"xy": head[1], "straightDistance": distance(origin, head[1]), "units": [[r[0], r[2], r[3]] for r in group],
            "listedGross": gross, "nearestAllyDistance": min((distance(head[1], a) for a in allies), default=None),
            "nearestAlliedTowerDistance": min((distance(head[1], t) for t in towers), default=None)}


def project_enemy(slot, npc, appearances, state_of):
    """Project *appearances*, never identify a real body among images, even when only an image is visible."""
    markers, unknown = [], False
    for e in appearances:
        state = state_of(e)
        unknown |= state == "unknown"
        if state == "visible":
            life = e.get("m_lifeState")
            markers.append({"xy": xy(e), "alive": (life == 0) if life is not None else None})
    # Neither ordering nor output fields disclose a selected handle, illusion flag, or hidden body position.
    markers.sort(key=lambda m: (m["xy"] or [], str(m["alive"])))
    return {"slot": slot, "hero": npc, "state": "visible" if markers else "unknown" if unknown else "hidden", "markers": markers}


def visible_hero_contact(e, team):
    # Source 2 can set is_hero for e.g. Lone Druid's Spirit Bear. Seeing the
    # bear exchange damage is NOT evidence its owner is fighting at that spot.
    return (str(e.log_type) in ("DAMAGE", "DEATH") and e.visible_to(team) is True
            and {e.attacker_team, e.target_team} == {2, 3}
            and all((e.attacker_is_hero_present, e.attacker_is_hero, e.target_is_hero_present, e.target_is_hero))
            and all(re.fullmatch(r"npc_dota_hero_[a-z0-9_]+", n or "") for n in (e.attacker_name, e.target_name))
            and (str(e.log_type) == "DEATH" or e.value > 0))


def extract_view(dem, output, match, key, pid, spells_only=False):
    from gem.parser import ReplayParser
    from gem.state.entities import EntityOp
    if version("gem-dota") != "0.14.0" or not 0 <= pid < 10 or output.exists():
        raise ValueError("Wrong parser/slot or view target already exists")
    team = 2 if pid < 5 else 3
    other = 5 - team
    p = ReplayParser(str(dem))
    resource = data = None
    last_second = None
    frames, fights, npcs = [], [], {}
    opponent_labels, ally_labels = set(), set()

    def entity(e, op):
        nonlocal resource, data
        cls = e.get_class_name()
        if cls == "CDOTA_PlayerResource":
            resource = e
        elif cls in (("CDOTADataRadiant", "CDOTA_DataRadiant") if team == 2 else ("CDOTADataDire", "CDOTA_DataDire")):
            data = e
        elif (not spells_only and cls in (LANE, NEUTRAL, TOWER)) or cls.startswith("CDOTA_Unit_Hero_"):
            identity = (e.get_index(), e.get_serial())
            if op & (EntityOp.LEFT | EntityOp.DELETED):
                npcs.pop(identity, None)
            elif op & (EntityOp.CREATED | EntityOp.ENTERED):
                npcs[identity] = e

    def heroes():
        if resource is None or not resource.active:
            return {}
        found = {}
        for i in range(30):
            handle = resource.get(f"m_vecPlayerTeamData.{i:04d}.m_hSelectedHero")
            e = p.entity_manager.find_by_handle(handle) if handle not in (None, NULL_HANDLE) else None
            if e is not None and e.active and e.get_class_name().startswith("CDOTA_Unit_Hero_"):
                slot = e.get("m_iPlayerID")
                if type(slot) is int and 0 <= slot < 10 and e.get("m_iTeamNum") == (2 if slot < 5 else 3):
                    found[slot] = e
        return found

    def snapshot(tick):
        nonlocal last_second
        sec = p.game_time_s
        if sec is None or sec < 0 or sec == last_second:
            return
        last_second = sec
        hs = heroes()
        me = hs.get(pid)
        if me is None or me.get_class_name() != HERO_CLASSES[key]:
            return
        table = p.string_tables.get_by_name("UnitNames")

        def unit_name(e):
            i = e.get("m_iUnitNameStringTableIndex")
            return table.items[i][0] if table and i in table.items else None

        words, allies, enemies, towers, lanes, neutrals = {}, [], [], [], [], []
        origin = xy(me) if me.get("m_lifeState") == 0 else None
        enemy_appearances = {}
        for unit in npcs.values():
            cls = unit.get_class_name()
            if unit.active and cls.startswith("CDOTA_Unit_Hero_") and unit.get("m_iTeamNum") == other:
                enemy_appearances.setdefault(cls, {})[(unit.get_index(), unit.get_serial())] = unit
        for slot, e in sorted(hs.items()):
            if e.get("m_iTeamNum") == other:
                looks = enemy_appearances.get(e.get_class_name(), {}).copy()
                looks[(e.get_index(), e.get_serial())] = e
                state_of = lambda u: visibility(data, u.get_index(), words)
                if spells_only:
                    sheet = visible_sheet(looks.values(), state_of)
                    npc = unit_name(e)
                    if npc:
                        opponent_labels.add(npc)
                    if sheet is not None and npc:
                        enemies.append([npc, *sheet])
                else:
                    enemies.append(project_enemy(slot, unit_name(e), looks.values(), state_of))
            elif spells_only:
                ally_labels.add(unit_name(e))
            elif slot != pid and e.get("m_lifeState") == 0 and xy(e) is not None:
                allies.append({"hero": unit_name(e), "xy": xy(e)})
        if spells_only:
            frames.append({"tick": tick, "publicSheets": enemies, "ownItems": own_items(p, me)})
            return
        unknown_units = False
        for e in npcs.values():
            if not e.active:
                continue
            cls, side = e.get_class_name(), e.get("m_iTeamNum")
            if cls == TOWER:
                if side == team and e.get("m_lifeState") == 0 and xy(e) is not None:
                    towers.append(xy(e))
                continue
            if not (cls == LANE and side == other or cls == NEUTRAL and side == 4):
                continue
            state = visibility(data, e.get_index(), words)
            if state != "visible":
                unknown_units |= state == "unknown"
                continue
            if e.get("m_lifeState") != 0 or e.get("m_bIsWaitingToSpawn") is not False:
                continue
            pos, name, hp = xy(e), unit_name(e), e.get("m_iHealth")
            if pos is None or not name or type(hp) is not int or hp <= 0:
                continue
            handle = e.get("m_hNeutralSpawner") if cls == NEUTRAL else None
            row = [name, pos, hp, e.get("m_iMaxHealth"), e.get("m_iGoldBountyMin"), e.get("m_iGoldBountyMax"),
                   handle if handle not in (None, NULL_HANDLE) else None]
            (lanes if cls == LANE else neutrals).append(row)
        ally_xy = [a["xy"] for a in allies]
        frames.append({"tick": tick, "bodyXY": origin, "allies": allies, "enemies": enemies,
                       "farmCoverage": "incomplete" if unknown_units or not words else "sampled-visible-only",
                       "lane": pack(lanes, origin, ally_xy, towers), "neutral": pack(neutrals, origin, ally_xy, towers)})

    def combat(e):
        # Visible event != both participants standing at its location. Spatial witnesses are joined separately.
        if spells_only:
            row = witness(e, team, "npc_dota_hero_" + key, opponent_labels - ally_labels)
            if row is not None:
                fights.append(row)
        elif visible_hero_contact(e, team):
            fights.append([e.tick, e.attacker_name, e.target_name, str(e.log_type)])

    p.on_entity(entity)
    p._on_packet_end(snapshot)  # Same boundary/API as gem.extractors.visibility in the audited pin.
    p.on_combat_log_entry(combat)
    p.parse()
    if p.parse_error or p.match_id != match or p.post_game_tick is None or not frames:
        raise ValueError("Incomplete or wrong DEM/focal hero")
    with dem.open("rb") as f:
        digest = hashlib.file_digest(f, "sha256").hexdigest()
    view = {"format": "team-spells-v2" if spells_only else "team-map-v2", "matchId": match, "build": p.game_build, "postGameTick": p.post_game_tick,
            "demSha256": digest, "hero": key, "team": team, "pid": pid,
            "frames": [f for f in frames if f["tick"] <= p.post_game_tick],
            "combat": [e for e in fights if e[1 if spells_only else 0] <= p.post_game_tick]}
    output.parent.mkdir(parents=True, exist_ok=True)
    with output.open("x", encoding="utf-8") as f:
        json.dump(view, f, separators=(",", ":"), allow_nan=False)
    print(json.dumps({"matchId": match, "hero": key, "team": team, "samples": len(view["frames"]), "visibleHeroEvents": len(view["combat"])}))


if __name__ == "__main__":
    from replay_storage import set_replay_temp
    ap = argparse.ArgumentParser()
    ap.add_argument("match", type=int)
    ap.add_argument("--root", required=True, type=Path)
    ap.add_argument("--hero", required=True, choices=HERO_CLASSES)
    ap.add_argument("--pid", required=True, type=int)
    ap.add_argument("--output", required=True, type=Path)
    ap.add_argument("--spells-only", action="store_true")
    args = ap.parse_args()
    set_replay_temp(args.output.parent)
    extract_view(args.root / f"{args.match}.dem", args.output, args.match, args.hero, args.pid, args.spells_only)
