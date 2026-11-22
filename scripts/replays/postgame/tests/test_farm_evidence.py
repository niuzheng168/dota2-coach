"""Offline comparison to independent evidence for the pinned completed match (not a production input).

Usage: python -IB test_farm_evidence.py [v2 timeline.json]
"""
from bisect import bisect_left
from collections import Counter, defaultdict
import hashlib
import json
from pathlib import Path
import sys
import unittest

from gem.parser import ReplayParser

HERE = Path(__file__).resolve().parent
ROOT = HERE.parents[3]
GOLD = json.loads((HERE / "fixtures/9097559249.json").read_bytes())
MATCH = GOLD["matchId"]
CACHE = ROOT / ".tmp/farm742"
OUTPUT = ROOT / f".tmp/postgame-qa/{MATCH}.farm-v2.json"
RAW = dict(lastHits="m_iLastHitCount", netWorth="m_iNetWorth", totalEarnedGold="m_iTotalEarnedGold",
           creepKillGold="m_iCreepKillGold")


class FarmEvidence(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        wire = OUTPUT.read_bytes()
        if hashlib.sha256(wire).hexdigest() != GOLD["timelineSha256"]:
            raise AssertionError("Pass the actual pinned v2 output")
        result = json.loads(wire)
        cls.rows = [e for e in result["events"] if e["kind"] == "replay-hero"]
        cls.by_player = defaultdict(list)
        for ev in cls.rows:
            if "economy" in ev["data"]:
                cls.by_player[ev["data"]["playerId"]].append(ev)

    def test_deleting_only_new_fields_reproduces_prior_v1_bytes(self):
        result = json.loads(OUTPUT.read_bytes())
        for ev in result["events"]:
            if ev["kind"] == "replay-hero":
                ev["data"].pop("economy", None)
        original = (json.dumps(result, ensure_ascii=True, allow_nan=False, separators=(",", ":")) + "\n").encode("ascii")
        self.assertEqual(len(original), GOLD["priorV1TimelineBytes"])
        self.assertEqual(hashlib.sha256(original).hexdigest(), GOLD["priorV1TimelineSha256"])

    def test_ten_rosters_against_original_extract_and_cached_opendota(self):
        legacy = json.loads((CACHE / f"{MATCH}.facts.json").read_bytes())
        self.assertTrue(legacy["complete"])
        self.assertEqual(legacy["demSha256"], GOLD["demSha256"])
        frames = legacy["frames"]  # independent extract.py: pre-delta, every rounded second, post-delta terminal
        ticks = [frame["tick"] for frame in frames]
        od = {p["player_slot"]: p for p in json.loads((CACHE / f"{MATCH}.opendota.json").read_bytes())["players"]}
        counts, bracket, exact = Counter(), Counter(), Counter()
        unresolved = [ev for ev in self.rows if "economy" not in ev["data"]]
        self.assertEqual(len(unresolved), GOLD["economyAbsentPreselection"])
        self.assertTrue(all(ev["data"]["hero"] is None and ev["clock"] < 0 for ev in unresolved))
        for pid in range(10):
            seq = self.by_player[pid]
            times = [ev["clock"] for ev in seq]
            last = seq[-1]["data"]
            terminal = next(h for h in frames[-1]["heroes"] if h["playerIdField"] == pid)
            self.assertEqual((terminal["name"], terminal["team"]), (last["hero"], last["team"]))
            od_slot = (128 if terminal["team"] == 3 else 0) + terminal["teamSlot"]
            p = od[od_slot]
            stats = last["economy"]
            for k, field in RAW.items():
                self.assertEqual(stats[k], terminal["economy"][field])
            self.assertEqual((stats["lastHits"], stats["netWorth"], stats["denies"], stats["creepKillGold"]),
                             (p["last_hits"], p["net_worth"], p["denies"], p["gold_reasons"]["13"]))
            self.assertEqual(stats["totalEarnedGold"], GOLD["terminalTotalEarnedGold"][pid])
            self.assertEqual(stats["currentXP"], GOLD["terminalCurrentXP"][pid])
            self.assertNotEqual(stats["totalEarnedGold"], p["gold"])  # not spendable gold
            for ev in seq:
                h = ev["data"]
                counts.update(h["economy"].keys())
                j = bisect_left(ticks, h["tick"])
                if j >= len(ticks) or ticks[j] != h["tick"]:
                    continue
                left, right = [next(x for x in frames[k]["heroes"] if x["playerIdField"] == pid)
                               for k in (j, min(j + 1, len(frames) - 1))]
                self.assertEqual((left["team"], left["teamSlot"], left["name"]), (terminal["team"], terminal["teamSlot"], h["hero"]))
                for k, field in RAW.items():
                    lo, hi = sorted((left["economy"][field], right["economy"][field]))
                    if lo <= h["economy"][k] <= hi:
                        bracket[k] += 1
                    if left["economy"][field] == h["economy"][k]:
                        exact[k] += 1
            for i, minute in enumerate(p["times"]):
                j = bisect_left(times, minute)
                self.assertTrue(0 < j < len(seq))
                a, b = seq[j-1]["data"]["economy"], seq[j]["data"]["economy"]
                for k, series in (("lastHits", "lh_t"), ("denies", "dn_t"), ("totalEarnedGold", "gold_t"), ("currentXP", "xp_t")):
                    self.assertTrue(a[k] <= p[series][i] <= b[k], (pid, minute, k))
        self.assertEqual(counts, GOLD["economyKnownRows"])
        self.assertEqual(exact, dict(lastHits=2971, netWorth=2751, totalEarnedGold=2754, creepKillGold=2975))
        self.assertEqual(bracket, dict(lastHits=2990, netWorth=2981, totalEarnedGold=2990, creepKillGold=2990))

    def test_independent_team_vector_scan_at_the_same_packet_boundary(self):
        # Different join/direction and callback: enumerate both team arrays directly, use each row's own
        # player ID, compare BEFORE next packet deltas. Even short-lived net-worth changes must agree.
        parser = ReplayParser(str(CACHE / f"{MATCH}.dem"))
        expected = defaultdict(dict)
        for ev in self.rows:
            h = ev["data"]
            if "economy" in h:
                expected[h["tick"]][h["playerId"]] = h
        prev = None
        checked = Counter()
        def before_next_packet(_):
            nonlocal prev
            if prev != parser.tick and prev in expected:
                for classname, side in (("CDOTA_DataRadiant", 2), ("CDOTA_DataDire", 3)):
                    entity = parser.entity_manager.find_by_class_name(classname)
                    for slot in range(5):
                        prefix = f"m_vecDataTeam.{slot:04d}."
                        ref = expected[prev][entity.get(prefix + "m_nPlayerID")]
                        self.assertEqual(ref["team"], side)
                        for k, field in (RAW | {"denies": "m_iDenyCount", "currentXP": "m_iTotalEarnedXP"}).items():
                            self.assertEqual(ref["economy"][k], entity.get(prefix + field), (prev, side, slot, k))
                            checked[k] += 1
            prev = parser.tick
        parser.on_tick_start(before_next_packet)
        parser.parse()
        self.assertIsNone(parser.parse_error)
        self.assertEqual(parser.match_id, int(MATCH))
        self.assertEqual(checked, GOLD["economyKnownRows"])


if __name__ == "__main__":
    if len(sys.argv) == 2 and not sys.argv[1].startswith("-"):
        OUTPUT = Path(sys.argv.pop())
    unittest.main(verbosity=2)
