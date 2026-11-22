"""Mandatory release gate on one real DEM. Uses cached public metadata for independent comparisons."""
from collections import Counter
import hashlib
import json
import os
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile
import time
import unittest

HERE = Path(__file__).resolve().parent
GOLD = json.loads((HERE / "fixtures/9097559249.json").read_bytes())
MATCH = GOLD["matchId"]
CACHE = HERE.parents[3] / ".tmp/farm742"
DEM = CACHE / f"{MATCH}.dem"


def command():
    exe = os.environ.get("POSTGAME_TEST_EXE")
    return [str(Path(exe).resolve())] if exe else [sys.executable, "-IB", HERE.parent / "cli.py"]


def run_cli(cmd, cwd, timeout=930):
    run = subprocess.run(cmd, shell=False, cwd=cwd, capture_output=True, timeout=timeout)
    if run.stdout:
        raise AssertionError("Unexpected replay output on stdout")
    messages = [json.loads(line) for line in run.stderr.splitlines()]
    return run.returncode, messages


def sha(path):
    with path.open("rb") as f:
        return hashlib.file_digest(f, "sha256").hexdigest()


class RealReplay(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        if not DEM.is_file():
            raise AssertionError(f"Missing release-gate DEM: {DEM} (no automatic download)")
        if sha(DEM) != GOLD["demSha256"]:
            raise AssertionError("Local DEM differs from pinned fixture")

    def test_all_map_real_postgame(self):
        with tempfile.TemporaryDirectory(prefix="replay path with space ") as td:
            out = Path(td) / "timeline.json"
            rc, status = run_cli([*command(), "--match-id", MATCH, "--dem", DEM, "--output", out], td)
            self.assertEqual(rc, 0, status)
            self.assertEqual(status[-1]["type"], "success")
            self.assertFalse(list(Path(td).glob(".postgame-*")))
            self.assertEqual((out.stat().st_size, sha(out)), (GOLD["timelineBytes"], GOLD["timelineSha256"]))
            self.verify(json.loads(out.read_bytes()))

    def verify(self, result):
        md = json.loads((CACHE / f"{MATCH}.opendota.json").read_bytes())
        self.assertEqual({k: v for k, v in result.items() if k != "events"}, dict(
            schemaVersion=1, matchId=MATCH, replaySha256=GOLD["demSha256"], complete=True, source="postgame-replay"))
        ev = result["events"]
        self.assertTrue(all(set(e) == {"clock", "kind", "data"} and isinstance(e["data"], dict) for e in ev))
        self.assertEqual([e["clock"] for e in ev], sorted(e["clock"] for e in ev))
        self.assertEqual(Counter(e["kind"] for e in ev), GOLD["eventCounts"])
        self.assertEqual((ev[0]["clock"], ev[-1]["clock"]), (GOLD["firstClock"], GOLD["postGameClock"]))
        self.assertEqual(ev[-1]["kind"], "replay-end")
        end = ev[-1]["data"]
        for k in ("playbackFrames", "playbackTicks", "hornTick", "gameBuild"):
            self.assertEqual(end[k], GOLD[k])
        self.assertEqual(end["tick"], GOLD["postGameTick"])
        self.assertEqual(end["winnerTeam"], 2 if md["radiant_win"] else 3)
        self.assertLess(abs(md["duration"] - ev[-1]["clock"]), 1)
        self.assertEqual(end["pauseIntervals"], 1)
        heroes = [e for e in ev if e["kind"] == "replay-hero"]
        mid = {e["data"]["tick"]: e["clock"] for e in heroes if e["data"]["playerId"] == 0}
        for tick, clock in GOLD["beforeAndAfterPause"]:
            self.assertEqual(mid[tick], clock)
        (t1, _), (t2, _) = GOLD["beforeAndAfterPause"]
        p1, p2 = GOLD["pauseTicks"]
        self.assertAlmostEqual(mid[t2] - mid[t1], (t2 - t1 - (p2 - p1)) / 30, places=2)
        self.assertEqual([sum(e["data"]["alive"] is v for e in heroes) for v in (True, False, None)],
                         GOLD["aliveDeadUnknownRows"])
        self.assertEqual(sum(e["clock"] > 1200 for e in heroes), GOLD["samplesAfter20Minutes"])
        for pid in range(10):
            resolved = [e for e in heroes if e["data"]["playerId"] == pid and e["data"]["hero"]]
            self.assertLess(resolved[0]["clock"], 0)
            self.assertTrue(resolved[-1]["data"]["terminal"])
            self.assertEqual(resolved[-1]["clock"], ev[-1]["clock"])
            self.assertLessEqual(max(b["clock"] - a["clock"] for a, b in zip(resolved, resolved[1:])), 5.1)
            self.assertTrue(all(e["data"]["hp"] is not None and e["data"]["x"] is not None
                                and e["data"]["y"] is not None and e["data"]["level"] is not None
                                and len(e["data"]["items"]) == 25 for e in resolved))
            self.assertGreater(len({tuple(e["data"]["items"]) for e in resolved}), 1)
        term = [e["data"] for e in heroes if e["data"]["terminal"]]
        self.assertEqual(Counter(e["team"] for e in term), {2: 5, 3: 5})
        self.assertEqual(len({e["hero"] for e in term}), 10)
        self.assertEqual([e["level"] for e in term], [p["level"] for p in md["players"]])
        kills = Counter(e["data"]["target"] for e in ev if e["kind"] == "replay-kill")
        self.assertEqual([kills[e["hero"]] for e in term], [p["deaths"] for p in md["players"]])
        self.assertEqual(Counter(e["data"]["type"] for e in ev if e["kind"] == "replay-objective"), GOLD["objectives"])
        wards = [e for e in ev if e["kind"] == "replay-ward"]
        # Independent cached OpenDota parse: handles match for EVERY recorded placement/removal through the ancient.
        for action, logs in [("placed", ("obs_log", "sen_log")), ("removed", ("obs_left_log", "sen_left_log"))]:
            rows = [e["data"] for e in wards if e["data"]["action"] == action]
            handles = Counter()
            for row in rows:
                idx, serial, _ = map(int, row["wardId"].split(":"))
                handles[(idx | serial << 14, row["type"])] += 1
            self.assertEqual(handles, Counter((w["ehandle"], typ) for p in md["players"]
                for key, typ in zip(logs, ("observer", "sentry")) for w in p[key] if w["time"] <= ev[-1]["clock"]))
        fog = GOLD["foggedObserver"]
        placed, removed = [e for e in wards if e["data"]["wardId"] == fog["id"]]
        self.assertEqual([placed["clock"], removed["clock"]], [fog["placedClock"], fog["removedClock"]])
        for key in ("x", "y", "ownerPlayerId", "team", "visibleRadiant", "visibleDire"):
            self.assertEqual(placed["data"][key], fog[key])
        self.assertFalse(fog["radiantWordAtPlacement"] & (1 << fog["bit"]))
        self.assertTrue(fog["direWordAtPlacement"] & (1 << fog["bit"]))
        cov = end["coverage"]
        self.assertEqual(cov["hero"]["knownRows"]["itemSlotsKnown"], GOLD["knownItemSlots"])
        self.assertEqual(cov["ward"]["observed"]["placedHiddenToOpponent"], GOLD["wardPlacedHiddenToOpponent"])
        self.assertEqual(cov["ward"]["openAtEnd"], GOLD["wardOpenAtEnd"])

    def test_cached_download_and_real_decompression_same_bytes(self):
        with tempfile.TemporaryDirectory() as td:
            out = Path(td) / "download.json"
            exe = os.environ.get("POSTGAME_TEST_EXE")
            python = Path(exe).resolve().parent / "runtime/python.exe" if exe else sys.executable
            rc, status = run_cli([python, "-IB", HERE / "offline_download.py", "--match-id", MATCH,
                                  "--download", "--output", out], td)
            self.assertEqual(rc, 0, status)
            self.assertTrue({"download", "archive", "decompress", "verify", "serialize", "published"}
                            <= {s["stage"] for s in status})
            self.assertEqual(sha(out), GOLD["timelineSha256"])
            self.assertFalse(list(Path(td).glob(".postgame-*")))

    def test_local_magic_sniffed_archive_same_bytes(self):
        with tempfile.TemporaryDirectory() as td:
            root = Path(td)
            archive, out = root / "user choice.dem.bz2", root / "timeline.json"
            shutil.copyfile(CACHE / f"{MATCH}.archive", archive)  # real Valve .dem.bz2, actually Zstd
            rc, status = run_cli([*command(), "--match-id", MATCH, "--dem", archive, "--output", out], td)
            self.assertEqual(rc, 0, status)
            stages = {s["stage"] for s in status}
            self.assertIn("decompress", stages)
            self.assertTrue({"download", "archive"}.isdisjoint(stages))
            self.assertEqual(sha(out), GOLD["timelineSha256"])
            self.assertTrue(archive.exists())
            self.assertFalse(list(root.glob(".postgame-*")))

    def test_cancel_during_real_parse(self):
        with tempfile.TemporaryDirectory() as td:
            root = Path(td)
            out, marker, log = root / "cancelled.json", root / "cancel", root / "stderr"
            with log.open("wb") as stderr:
                proc = subprocess.Popen([*command(), "--match-id", MATCH, "--dem", DEM, "--output", out,
                                         "--cancel-file", marker], stdout=subprocess.PIPE, stderr=stderr, cwd=root)
                try:
                    deadline = time.monotonic() + 20
                    while b'"stage": "parse"' not in log.read_bytes():
                        self.assertIsNone(proc.poll(), log.read_bytes())
                        self.assertLess(time.monotonic(), deadline)
                        time.sleep(.05)
                    marker.touch()
                    self.assertEqual(proc.communicate(timeout=10)[0], b"")
                    self.assertEqual(proc.returncode, 130)
                finally:
                    if proc.poll() is None:
                        proc.kill()
                        proc.wait()
            self.assertEqual(json.loads(log.read_bytes().splitlines()[-1])["stage"], "cancelled")
            self.assertFalse(out.exists())
            self.assertTrue(marker.exists())  # caller owns marker
            self.assertFalse(list(root.glob(".postgame-*")))

    def test_corrupt_packet_after_post_game_rejected(self):
        with tempfile.TemporaryDirectory() as td:
            root = Path(td)
            dem, out = root / "corrupt.dem", root / "must-not-exist.json"
            shutil.copyfile(DEM, dem)
            with dem.open("r+b") as f:
                # Pinned DEM: last ordinary packet, tick 55464 (> POST_GAME 51116), size 471.
                # Preserve its length, stop, 64-bit ID, winner, 5v5 roster and the genuine trailer.
                f.seek(60759460)
                self.assertEqual(f.read(6), bytes.fromhex("07a8b103d703"))
                f.write(b"\x0f")  # invalid protobuf wire type
            rc, status = run_cli([*command(), "--match-id", MATCH, "--dem", dem, "--output", out], td)
            self.assertEqual(rc, 4, status)
            self.assertTrue(any(s["stage"] == "parse" and s.get("tick", 0) > GOLD["postGameTick"] for s in status))
            self.assertFalse(out.exists())
            self.assertFalse(list(root.glob(".postgame-*")))


if __name__ == "__main__":
    unittest.main()
