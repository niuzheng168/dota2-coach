import bz2
import hashlib
import json
from pathlib import Path
import struct
import subprocess
import sys
import tempfile
import threading
import unittest
from unittest import mock

HERE = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(HERE))
import cli
from gem.proto.demo_pb2 import CDemoFileHeader, CDemoFileInfo
from timeline import Capture, MAX_EVENTS, farm_sample, life, position, visibility
from verify import inspect_dem
from worker import write_result
from decompress import decompress


def var(n):
    data = bytearray()
    while n >= 128:
        data.append(0x80 | (n & 127))
        n >>= 7
    return data + bytes([n])


def frame(kind, tick, data=b""):
    return var(kind) + var(tick) + var(len(data)) + data


def envelope(*, packets=1, winner=2, client="SourceTV Demo", stop=True):
    """Complete *framing*, deliberately no genuine match/POST_GAME; must never publish."""
    h = CDemoFileHeader(demo_file_stamp="PBDEMS2\0", client_name=client)
    fi = CDemoFileInfo(playback_ticks=30, playback_time=1., playback_frames=packets)
    fi.game_info.dota.match_id, fi.game_info.dota.game_winner = 9097559249, winner
    for i in range(10):
        fi.game_info.dota.player_info.add(game_team=2 + i // 5, hero_name=f"npc_dota_hero_{i}")
    prefix = frame(1, 0xffffffff, h.SerializeToString()) + frame(8, 0) + frame(4, 0) + frame(5, 0) + frame(7, 30)
    if stop:
        prefix += frame(0, 30)
    spawn = frame(15, 30)
    return b"PBDEMS2\0" + struct.pack("<II", 16 + len(prefix + spawn), 16 + len(prefix)) + prefix + spawn + frame(2, 30, fi.SerializeToString())


class Fields(dict):
    get_uint64 = dict.get


class Contract(unittest.TestCase):
    def test_farm_join_uses_resource_team_slot_not_pid_or_resource_index(self):
        # Resource index 12, player 8, Dire team slot 1: none of these indexes are interchangeable.
        res = {"m_vecPlayerData.0012.m_iPlayerTeam": 3, "m_vecPlayerTeamData.0012.m_iTeamSlot": 1}
        pre = "m_vecDataTeam.0001."
        team = {"m_iTeamNum": 3, "m_vecDataTeam": 5, pre + "m_nPlayerID": 8, pre + "m_iLastHitCount": 0,
                pre + "m_iNetWorth": 710, pre + "m_iDenyCount": -1, pre + "m_iTotalEarnedGold": True,
                pre + "m_iCreepKillGold": 0xffffffff}
        hero = {"m_iCurrentXP": 35}
        def read(r, t, index=12):
            return farm_sample(r, index, t, hero, 8, 3)
        self.assertEqual(read(res, team), {"lastHits": 0, "netWorth": 710, "currentXP": 35})
        del team[pre + "m_iNetWorth"]  # no back/forward fill when it is no longer reported
        self.assertNotIn("netWorth", read(res, team))
        for r, t, i in [(res, team, None), ({}, team, 12), (res, None, 12),
                        (res | {"m_vecPlayerData.0012.m_iPlayerTeam": 2}, team, 12),
                        (res, team | {pre + "m_nPlayerID": 7}, 12), (res, team | {"m_iTeamNum": 2}, 12),
                        (res, team | {"m_vecDataTeam": 1}, 12)]:
            self.assertEqual(read(r, t, i), {})

    def test_wrong_internal_64bit_id_not_low_word(self):
        with tempfile.TemporaryDirectory() as td:
            dem = Path(td) / "507624657.dem"
            dem.write_bytes(envelope())
            with self.assertRaisesRegex(ValueError, "Internal FileInfo"):
                inspect_dem(dem, 507624657)
            self.assertEqual(inspect_dem(dem, 9097559249).ticks, 30)

    def test_trailer_index_packet_totals_winner_server_and_eof(self):
        wire = envelope()
        for bad in [wire[:-1], wire[:struct.unpack_from("<I", wire, 8)[0]], wire + frame(3, 30), envelope(stop=False),
                    envelope(packets=2), envelope(winner=0), envelope(client="Player POV"), wire[:8] + bytes(8) + wire[16:]]:
            with self.subTest(length=len(bad)), tempfile.TemporaryDirectory() as td:
                path = Path(td) / "x.dem"
                path.write_bytes(bad)
                with self.assertRaises(ValueError):
                    inspect_dem(path, 9097559249)

    def test_framing_without_game_end_not_published(self):
        with tempfile.TemporaryDirectory() as td:
            dem, out = Path(td) / "x.dem", Path(td) / "x.json"
            dem.write_bytes(envelope())
            run = subprocess.run([sys.executable, "-IB", HERE / "cli.py", "--dem", dem, "--match-id", "9097559249",
                                  "--output", out], capture_output=True, timeout=20)
            self.assertEqual(run.returncode, 4, run.stderr)
            self.assertEqual(run.stdout, b"")
            self.assertFalse(out.exists())
            self.assertFalse(list(Path(td).glob(".postgame-*")))

    def test_real_bzip2_decoder_and_archive_hash_gate(self):
        with tempfile.TemporaryDirectory() as td:
            root = Path(td)
            archive = bz2.compress(envelope())
            (root / "9097559249.archive").write_bytes(archive)
            manifest = root / "9097559249.acquisition.json"
            manifest.write_text('{"archiveSha256":"' + '0' * 64 + '"}', encoding="utf8")
            with self.assertRaisesRegex(ValueError, "hash mismatch"):
                decompress(root, 9097559249)
            manifest.write_text(json.dumps({"archiveSha256": hashlib.sha256(archive).hexdigest()}), encoding="utf8")
            evidence = decompress(root, 9097559249)
            self.assertEqual(evidence["demSha256"], hashlib.sha256(envelope()).hexdigest())
            self.assertEqual((root / "9097559249.dem").read_bytes(), envelope())

    def test_no_implicit_download_argument_injection_or_overwrite(self):
        root = Path(tempfile.gettempdir())
        with tempfile.TemporaryDirectory(dir=root) as td:
            output = Path(td) / "taken.json"
            for args in [[], ["--match-id", "1"], ["--match-id", "01", "--download"],
                         ["--match-id", "1e9", "--download"], ["--match-id", "9007199254740992", "--download"],
                         ["--match-id", "1", "--download", "--dem", "game.dem"],
                         ["--match-id", "1", "--dem", "https://example.org/game.dem"],
                         ["--match-id", "1", "--dem", r"\\server\share\game.dem"]]:
                with self.subTest(args=args), mock.patch.object(cli, "run_child") as runner:
                    with self.assertRaises(ValueError):
                        cli.main([*args, "--output", str(output)])
                    runner.assert_not_called()
            output.write_bytes(b"original")
            with self.assertRaisesRegex(ValueError, "new .json"):
                cli.main(["--match-id", "1", "--download", "--output", str(output)])
            part = Path(td) / "staged"
            part.write_bytes(b"don't overwrite")
            with self.assertRaises(FileExistsError):
                cli.publish(part, output)
            self.assertEqual(output.read_bytes(), b"original")

    def test_unknown_fields_are_not_zero_or_empty(self):
        self.assertEqual(position(Fields()), (None, None))
        self.assertEqual(position(Fields({"CBodyComponent.m_cellX": 128, "CBodyComponent.m_cellY": 128,
                                          "CBodyComponent.m_vecX": 0, "CBodyComponent.m_vecY": 0})), (0, 0))
        self.assertIsNone(life(Fields(m_lifeState=0, m_iHealth=100)))
        self.assertIs(life(Fields(m_lifeState=2)), False)
        self.assertIsNone(visibility(Fields(other=1), 669))
        self.assertIs(visibility(Fields({"m_bNPCVisibleState.0010": 0}), 669), False)
        self.assertIs(visibility(Fields({"m_bNPCVisibleState.0010": 1 << 29}), 669), True)
        cap = object.__new__(Capture)
        cap.resolve = lambda _: None
        self.assertIsNone(cap.inventory(Fields()))
        self.assertEqual(cap.inventory(Fields({"m_hItems": 3, "m_hItems.0000": 0xffffff, "m_hItems.0001": 42})),
                         ["empty", None, None])
        with self.assertRaises(ValueError):
            cap.inventory(Fields(m_hItems=33))

    def test_abort_instead_of_truncating_events_or_json(self):
        cap = object.__new__(Capture)
        cap.events = [None] * MAX_EVENTS
        with self.assertRaisesRegex(ValueError, "event budget"):
            cap.emit(10, "replay-kill")
        with tempfile.TemporaryDirectory() as td, mock.patch("worker.MAX_OUTPUT", 45):
            for i, value in enumerate([{"data": "x" * 50}, {"clock": float("nan") }]):
                with self.assertRaises(ValueError):
                    write_result({"events": [value]}, Path(td) / str(i))

    def test_child_cancellation_and_deadline(self):
        with tempfile.TemporaryDirectory() as td:
            marker = Path(td) / "cancel"
            sleeper = [sys.executable, "-I", "-c", "import time;time.sleep(100)"]
            with self.assertRaises(TimeoutError):
                cli.run_child(sleeper, td, "test", 0.2, None, 4)
            t = threading.Timer(0.25, marker.touch)
            t.start()
            try:
                with self.assertRaises(KeyboardInterrupt):
                    cli.run_child(sleeper, td, "test", 5, marker, 4)
            finally:
                t.join()


if __name__ == "__main__":
    unittest.main()
