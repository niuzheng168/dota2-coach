"""Fail-closed framing checks before feeding a finalized 5v5 SourceTV DEM to gem.

gem reads to EOF, but EOF at a frame boundary is not proof of a complete DEM.
Check the header-indexed trailer *and* its independent packet/tick totals.
"""
from collections import Counter
from dataclasses import dataclass
import hashlib
import math
import mmap
from pathlib import Path
import re
import struct

import snappy
from gem.proto.demo_pb2 import CDemoFileHeader, CDemoFileInfo


MAX_DEM = 2 * 1024**3
MAX_TICK = 6 * 3600 * 30
MAX_FRAME = 16 * 1024**2
MAX_EXPANDED = 4 * 1024**3
MAX_FRAMES = 750_000
NPC = re.compile(r"npc_dota_hero_[a-z0-9_]{1,80}\Z")


@dataclass(frozen=True)
class Trailer:
    sha256: str
    match_id: int
    ticks: int
    packets: int
    winner: int
    roster: Counter


def varint(buf, pos, end):
    n = 0
    for shift in range(0, 35, 7):
        if pos >= end:
            break
        b = buf[pos]
        pos += 1
        n |= (b & 127) << shift
        if b < 128 and n <= 0xffffffff:
            return n, pos
    raise ValueError("Invalid DEM frame length/varint")


def inspect_dem(path: Path, expected_id: int) -> Trailer:
    size = path.stat().st_size
    if not 16 < size <= MAX_DEM:
        raise ValueError("Invalid DEM size (limit 2 GiB)")
    with path.open("rb") as f, mmap.mmap(f.fileno(), 0, access=mmap.ACCESS_READ) as buf:
        if buf[:8] != b"PBDEMS2\0":
            raise ValueError("Not a Source 2 DEM")
        info_at, spawn_at = struct.unpack_from("<II", buf, 8)
        if not 16 < spawn_at < info_at < size:
            raise ValueError("Missing or truncated final DEM index")
        cursor, frames, expanded, last_tick = 16, 0, 0, 0
        counts = Counter()
        info = stop = None
        while cursor < size:
            start = cursor
            cmd, cursor = varint(buf, cursor, size)
            raw_tick, cursor = varint(buf, cursor, size)
            length, cursor = varint(buf, cursor, size)
            end = cursor + length
            kind = cmd & ~64
            tick = 0 if raw_tick == 0xffffffff else raw_tick
            if not 0 <= kind <= 18 or not last_tick <= tick <= MAX_TICK:
                raise ValueError("Unsupported DEM command/tick range")
            if end > size or length > MAX_FRAME:
                raise ValueError("Truncated/oversized outer frame")
            decoded = varint(buf, cursor, end)[0] if cmd & 64 else length
            frames += 1
            expanded += decoded
            if frames > MAX_FRAMES or expanded > MAX_EXPANDED or decoded > MAX_FRAME:
                raise ValueError("DEM frame/expansion budget exceeded")
            if kind in (1, 2):
                if decoded > 1024**2:
                    raise ValueError("Oversized DEM metadata")
                payload = buf[cursor:end]
                if cmd & 64:
                    payload = snappy.decompress(payload)
                if kind == 1:
                    h = CDemoFileHeader.FromString(payload)
                    if start != 16 or h.client_name != "SourceTV Demo" or h.demo_file_stamp != "PBDEMS2\0":
                        raise ValueError("Expected a server-recorded SourceTV DEM header")
                else:
                    if start != info_at or end != size:
                        raise ValueError("FileInfo is not the indexed final frame")
                    info = CDemoFileInfo.FromString(payload)
            if kind == 0:
                stop = tick
            if (start == spawn_at and kind != 15) or (kind == 15 and (start != spawn_at or stop != tick)):
                raise ValueError("Invalid spawn-groups trailer index")
            if stop is not None and kind not in (0, 15, 2):
                raise ValueError("Unexpected data after DEM_Stop")
            counts[kind] += 1
            cursor, last_tick = end, tick
        if any(counts[k] != 1 for k in (0, 1, 2, 15)) or not counts[4] or not counts[5] or not counts[8]:
            raise ValueError("Missing required signon/schema/stop/trailer frames")
        if (info is None or stop != last_tick or not last_tick or info.playback_ticks != last_tick
                or not counts[7] or info.playback_frames != counts[7]):
            raise ValueError("Incomplete DEM: playback frame/tick totals disagree")
        if not math.isclose(info.playback_time, last_tick / 30, abs_tol=0.1):
            raise ValueError("Unsupported replay tick rate")
        dota = info.game_info.dota
        if dota.match_id != expected_id:
            raise ValueError(f"Internal FileInfo match ID {dota.match_id} differs from {expected_id}")
        if dota.game_winner not in (2, 3):
            raise ValueError("FileInfo has no completed result")
        roster = Counter((v.game_team, v.hero_name) for v in dota.player_info)
        if (len(roster) != 10 or any(n != 1 or not NPC.fullmatch(hero) for (_, hero), n in roster.items())
                or Counter(team for team, _ in roster) != {2: 5, 3: 5}):
            raise ValueError("Expected a 5v5 FileInfo hero roster")
        return Trailer(hashlib.sha256(buf).hexdigest(), dota.match_id, last_tick,
                       counts[7], dota.game_winner, roster)
