"""Replay-only, packet-boundary entity sampling and recorded event transitions."""
from collections import Counter
import math
import re

from gem.parser import ReplayParser
from gem.extractors._snapshots import _player_id_from_entity
from gem.proto.dota_usermessages_pb2 import CHAT_MESSAGE_AEGIS, CHAT_MESSAGE_AEGIS_STOLEN, CHAT_MESSAGE_DENIED_AEGIS
from gem.state.entities import EntityOp

from verify import Trailer


SAMPLE_SECONDS = 5
MAX_EVENTS = 50_000
NULL_HANDLE = 0xffffff
WARD = {"CDOTA_NPC_Observer_Ward": "observer", "CDOTA_NPC_Observer_Ward_TrueSight": "sentry"}
TEAM_DATA = {"CDOTA_DataRadiant": 2, "CDOTADataRadiant": 2, "CDOTA_DataDire": 3, "CDOTADataDire": 3}
AEGIS = {CHAT_MESSAGE_AEGIS: "aegis-pickup", CHAT_MESSAGE_AEGIS_STOLEN: "aegis-stolen",
         CHAT_MESSAGE_DENIED_AEGIS: "aegis-denied"}
TOKEN = re.compile(r"[a-zA-Z0-9_]{1,100}\Z")
OPAQUE = re.compile(r"[a-fA-F0-9]{32}|[a-zA-Z0-9]{64}")
FARM_FIELDS = {"lastHits": "m_iLastHitCount", "netWorth": "m_iNetWorth",
               "totalEarnedGold": "m_iTotalEarnedGold", "creepKillGold": "m_iCreepKillGold",
               "denies": "m_iDenyCount"}


def token(value):
    return value if isinstance(value, str) and TOKEN.fullmatch(value) and not OPAQUE.search(value) else None


def player_id(value):
    return value if type(value) is int and 0 <= value < 10 else None


def team_id(value):
    return value if value in (2, 3) else None


def position(e):
    xy = []
    for axis in "XY":
        cell, offset = (e.get("CBodyComponent." + field + axis) for field in ("m_cell", "m_vec"))
        if cell is None or offset is None:
            return None, None
        value = cell * 128 + offset - 16384
        if not math.isfinite(value):
            return None, None
        xy.append(value)
    return tuple(xy)


def visibility(team_entity, idx):
    word = team_entity.get_uint64(f"m_bNPCVisibleState.{idx >> 6:04d}") if team_entity else None
    return None if word is None else bool(word & (1 << (idx & 63)))


def life(e):
    state, waiting, hp = (e.get(k) for k in ("m_lifeState", "m_bIsWaitingToSpawn", "m_iHealth"))
    if state in (1, 2) or waiting is True:
        return False
    return True if state == 0 and waiting is False and hp is not None and hp > 0 else None


def farm_sample(resource, index, team_entity, hero, pid, team):
    """Join this packet's selected handle -> resource row -> team slot -> independently tagged player.

    A missing or inconsistent join is no sample, never a zero or pid % 5 fallback.
    """
    if resource is None or index is None or team_entity is None or team not in (2, 3):
        return {}
    if (resource.get(f"m_vecPlayerData.{index:04d}.m_iPlayerTeam") != team
            or team_entity.get("m_iTeamNum") != team):
        return {}
    slot = resource.get(f"m_vecPlayerTeamData.{index:04d}.m_iTeamSlot")
    n = team_entity.get("m_vecDataTeam")
    if type(slot) is not int or type(n) is not int or not 0 <= slot < min(n, 5):
        return {}
    prefix = f"m_vecDataTeam.{slot:04d}."
    if player_id(team_entity.get(prefix + "m_nPlayerID")) != pid:
        return {}
    values = {key: team_entity.get(prefix + field) for key, field in FARM_FIELDS.items()}
    values["currentXP"] = hero.get("m_iCurrentXP")  # raw selected hero field, not inferred from level/XPM
    return {k: v for k, v in values.items() if type(v) is int and 0 <= v <= 0x7fffffff}


def objective(target):
    if target in ("npc_dota_roshan", "npc_dota_miniboss", "npc_dota_courier"):
        return {"npc_dota_roshan": "roshan", "npc_dota_miniboss": "tormentor", "npc_dota_courier": "courier"}[target]
    for side in ("goodguys", "badguys"):
        prefix = "npc_dota_" + side + "_"
        if target.startswith(prefix + "tower"):
            return "tower"
        if target.startswith((prefix + "melee_rax", prefix + "range_rax")):
            return "barracks"
        if target == prefix + "fort":
            return "ancient"
    return None


class Capture:
    def __init__(self, parser, trailer, progress):
        self.p, self.file, self.progress = parser, trailer, progress
        self.events = []
        self.resource = None
        self.teams = {}
        self.active_wards = {}
        self.pending_wards = []
        self.known = Counter()
        self.ward_stats = Counter()
        self.kill_stats = Counter()
        self.frames = 0
        self.bucket = None
        self.terminal = None
        self.next_progress = 0
        self.heroes = {name for _, name in trailer.roster}
        parser._on_entity_filtered(self.entity, class_names=("CDOTA_PlayerResource", *TEAM_DATA, *WARD))
        # Pinned gem private hook: team bitsets and entity fields AFTER deltas, even at POST_GAME.
        parser._on_packet_end(self.packet)
        parser.on_game_end(self.finish_packet)
        parser.on_combat_log_entry(self.combat)
        parser.on_chat_event(self.chat_event)

    def emit(self, tick, kind, **data):
        if len(self.events) >= MAX_EVENTS:
            raise ValueError("Replay exceeds event budget; not emitting partial output")
        self.events.append((tick, kind, {"tick": tick, **data}))

    def resolve(self, handle):
        if handle is None or handle == NULL_HANDLE:
            return None
        entity = self.p.entity_manager.find_by_handle(handle)
        return entity if entity is not None and entity.active else None

    def name(self, e):
        table = self.p.string_tables.get_by_name("EntityNames")
        if table is not None:
            for field in ("m_pEntity.m_nameStringTableIndex", "m_pEntity.m_nameStringableIndex"):
                row = table.items.get(e.get(field))
                if row and token(row[0]):
                    return row[0]
        return None

    def inventory(self, hero):
        n = hero.get("m_hItems")
        if n is None:
            return None
        if type(n) is not int or not 0 <= n <= 32:
            raise ValueError("Unsupported inventory vector length")
        slots = []
        for i in range(n):
            handle = hero.get(f"m_hItems.{i:04d}")
            item = self.resolve(handle)
            name = self.name(item) if item else None
            slots.append("empty" if handle == NULL_HANDLE else name if name and name.startswith("item_") else None)
        return slots

    def selected(self):
        result = {}
        if self.resource is not None:
            n = self.resource.get("m_vecPlayerTeamData")
            if n is None:
                return result
            if not 0 <= n <= 64:
                raise ValueError("Unsupported PlayerResource vector length")
            for i in range(n):
                e = self.resolve(self.resource.get(f"m_vecPlayerTeamData.{i:04d}.m_hSelectedHero"))
                if e is None or not e.get_class_name().startswith("CDOTA_Unit_Hero_"):
                    continue
                pid = player_id(_player_id_from_entity(e))
                if pid is not None:
                    if pid in result:
                        if result[pid][0] is not e:
                            raise ValueError("Ambiguous selected hero for one player")
                        result[pid] = (e, None)  # same hero but no unique resource row for economy
                    else:
                        result[pid] = (e, i)
        return result

    def sample(self, tick, terminal=False):
        selected = self.selected()
        roster = Counter()
        for pid in range(10):
            e, resource_index = selected.get(pid, (None, None))
            # Emit nulls, rather than letting an importer carry a stale slot forward across a gap.
            name, team = (self.name(e), team_id(e.get("m_iTeamNum"))) if e else (None, None)
            name = name if name in self.heroes else None
            x, y = position(e) if e else (None, None)
            data = dict(hero=name, team=team, x=x, y=y, level=e.get("m_iCurrentLevel") if e else None,
                        hp=e.get("m_iHealth") if e else None, maxHp=e.get("m_iMaxHealth") if e else None,
                        alive=life(e) if e else None, items=self.inventory(e) if e else None)
            if e:
                roster[team, name] += 1
                self.known["selected"] += 1
            for k, value in data.items():
                if value is not None:
                    self.known[k] += 1
            if data["items"] is not None:
                self.known["itemSlotsRead"] += len(data["items"])
                self.known["itemSlotsKnown"] += sum(v is not None for v in data["items"])
            if name and (economy := farm_sample(self.resource, resource_index, self.teams.get(team), e, pid, team)):
                data["economy"] = economy
            self.emit(tick, "replay-hero", playerId=pid, **data, terminal=terminal)
        self.frames += 1
        if terminal and roster != self.file.roster:
            raise ValueError("Terminal selected heroes do not match the 5v5 FileInfo roster")

    def entity(self, e, op):
        if self.terminal is not None:
            return
        cls = e.get_class_name()
        gone = op.has(EntityOp.LEFT | EntityOp.DELETED)
        if cls == "CDOTA_PlayerResource":
            self.resource = None if gone else e
        elif cls in TEAM_DATA:
            self.teams[TEAM_DATA[cls]] = None if gone else e
        else:
            idx, serial = e.get_index(), e.get_serial()
            key = (idx, serial)
            active = self.active_wards.get(key)
            state = e.get("m_lifeState")
            if gone and state not in (1, 2):
                # Leaving network scope is NOT proof of a ward kill/expiry. Keep identity on LEFT.
                if op.has(EntityOp.DELETED) and active:
                    self.queue_ward(e, active, "removed", "entity-deleted")
                    del self.active_wards[key]
            elif state in (1, 2) and active:
                self.queue_ward(e, active, "removed", "life-state")
                del self.active_wards[key]
            elif not gone and state == 0 and not active:
                ident = f"{idx}:{serial}:{self.p.tick}"
                self.active_wards[key] = ident
                self.queue_ward(e, ident, "placed", "first-observed-alive")

    def queue_ward(self, e, ident, action, evidence):
        owner = self.resolve(e.get("m_hOwnerEntity"))
        pid = player_id(_player_id_from_entity(owner, allow_owner=True))
        x, y = position(e)
        self.pending_wards.append((e, dict(wardId=ident, action=action, evidence=evidence,
            type=WARD[e.get_class_name()], team=team_id(e.get("m_iTeamNum")), ownerPlayerId=pid, x=x, y=y)))
        if len(self.pending_wards) > 2048:
            raise ValueError("Too many ward transitions in one packet")

    def packet(self, tick):
        if tick >= self.next_progress:
            self.progress("parse", tick=tick, totalTicks=self.file.ticks)
            self.next_progress = tick + max(1800, self.file.ticks // 20)
        if self.terminal is not None:
            return
        for ward, row in self.pending_wards:
            idx = ward.get_index()
            same = ward.active and self.p.entity_manager.find(idx) is ward
            rad, dire = (visibility(self.teams.get(team), idx) if same else None for team in (2, 3))
            self.emit(tick, "replay-ward", **row, visibleRadiant=rad, visibleDire=dire)
            self.ward_stats[row["action"]] += 1
            if row["action"] == "placed" and ((row["team"] == 2 and dire is False) or
                                              (row["team"] == 3 and rad is False)):
                self.ward_stats["placedHiddenToOpponent"] += 1
        self.pending_wards.clear()
        raw = self.p.raw_game_time_s
        if raw is not None and raw // SAMPLE_SECONDS != self.bucket and self.p.post_game_tick is None:
            self.bucket = raw // SAMPLE_SECONDS
            self.sample(tick)

    def finish_packet(self, tick):
        self.sample(tick, terminal=True)
        self.terminal = tick

    def combat(self, entry):
        if self.terminal is not None or entry.log_type != "DEATH":
            return
        target = token(entry.target_name)
        if target is None:
            return
        data = dict(target=target, attacker=token(entry.attacker_name), damageSource=token(entry.damage_source_name),
                    attackerTeam=entry.attacker_team, targetTeam=entry.target_team)
        if target in self.heroes:
            if entry.target_is_illusion or entry.will_reincarnate:
                self.kill_stats["excludedIllusionOrReincarnation"] += 1
            elif not (entry.target_is_hero_present and entry.target_is_hero and entry.target_is_illusion_present):
                self.kill_stats["unconfirmedTargetFlags"] += 1
            else:
                self.emit(entry.tick, "replay-kill", **data)
        elif (kind := objective(target)) is not None:
            self.emit(entry.tick, "replay-objective", type=kind, **data)

    def chat_event(self, msg, tick):
        # Typed game notifications only. Never subscribe to or export player chat.
        if self.terminal is None and msg.HasField("type") and msg.type in AEGIS:
            pid = player_id(msg.playerid_1) if msg.HasField("playerid_1") else None
            self.emit(tick, "replay-objective", type=AEGIS[msg.type], playerId=pid)


def extract(dem, trailer: Trailer, progress):
    parser = ReplayParser(str(dem))
    capture = Capture(parser, trailer, progress)
    parser.parse()  # Do NOT stop at the postgame callback: consume through indexed FileInfo/EOF.
    if (parser.parse_error is not None or parser.truncated_at_tick is not None or parser.tick != trailer.ticks
            or parser.match_id != trailer.match_id or parser.post_game_tick is None
            or capture.terminal != parser.post_game_tick or capture.frames <= 1):
        raise ValueError("Incomplete DEM parse or missing POST_GAME / internal match ID")
    details = parser.match_details
    if details is not None and details.HasField("match_id") and details.match_id != trailer.match_id:
        raise ValueError("Embedded postgame match ID disagrees with FileInfo")
    clock = parser.game_clock
    if (clock.game_start_tick is None or clock.game_start_time_s is None
            or not 0 < clock.game_start_tick < capture.terminal <= trailer.ticks):
        raise ValueError("No verified horn/postgame clock")
    counts = Counter(kind for _, kind, _ in capture.events)
    coverage = {
        "hero": {"sampleSeconds": SAMPLE_SECONDS, "frames": capture.frames, "knownRows": dict(capture.known),
                 "basis": "PlayerResource selected handles; rounded raw engine clock buckets + terminal packet; null=unknown"},
        "ward": {"observed": dict(capture.ward_stats), "openAtEnd": len(capture.active_wards),
                 "basis": "observer/sentry lifecycle; packet-end team visibility bits; removal cause not inferred"},
        "kill": {"excluded": dict(capture.kill_stats), "basis": "confirmed non-illusion roster hero DEATH, no reincarnation"},
        "objective": {"basis": "recorded DEATH (tower/barracks/ancient/Roshan/Tormentor/courier) or typed Aegis notification"},
        "observedEvents": dict(counts),
    }
    capture.emit(capture.terminal, "replay-end", winnerTeam=trailer.winner, gameBuild=parser.game_build or None,
                 parser="gem-dota/0.14.0", hornTick=clock.game_start_tick, playbackTicks=trailer.ticks,
                 playbackFrames=trailer.packets, pauseIntervals=len(clock.pauses),
                 clockBasis="horn-relative game seconds; freezes during recorded pauses", coverage=coverage)
    result, last = [], -math.inf
    first_half, second_half = set(), set()
    end_seconds = clock.game_time_at(capture.terminal)
    if end_seconds is None or not math.isfinite(end_seconds) or end_seconds <= 0:
        raise ValueError("Invalid end clock")
    for tick, kind, data in sorted(capture.events, key=lambda r: r[0]):  # stable for ties, end last
        seconds = clock.game_time_at(tick)
        if seconds is None or not math.isfinite(seconds) or seconds < last:
            raise ValueError("Unknown/non-monotone replay clock")
        last = seconds
        result.append({"clock": round(seconds, 3), "kind": kind, "data": data})
        if kind == "replay-hero" and data["hero"] and seconds >= 0 and not data["terminal"]:
            (first_half if seconds < end_seconds / 2 else second_half).add(data["playerId"])
    if result[0]["clock"] >= 0 or first_half != set(range(10)) or second_half != first_half:
        raise ValueError("Missing pregame or ten-player sampling across both halves of the match")
    return {"schemaVersion": 1, "matchId": str(trailer.match_id), "complete": True,
            "replaySha256": trailer.sha256, "source": "postgame-replay", "events": result}
