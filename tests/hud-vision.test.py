"""Pure-logic tests for the HUD recognizer and hero scanner; run in the isolated .tmp/vision-tools venv:
.tmp/vision-tools/Scripts/python.exe -I tests/hud-vision.test.py (no GPU or game needed)."""
import collections
import sys
import unittest
from pathlib import Path

import numpy as np

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "scripts/vision"))
import hud_recognizer as hr  # noqa: E402
import hero_scan  # noqa: E402
import wgc  # noqa: E402

HEROES = {"npc_dota_hero_juggernaut": "主宰", "npc_dota_hero_pudge": "帕吉", "npc_dota_hero_abyssal_underlord": "孽主",
          "npc_dota_hero_phantom_assassin": "幻影刺客", "npc_dota_hero_phantom_lancer": "幻影长矛手"}
NAMES = hr.HeroNames({"heroes": {k: {"nameZh": v} for k, v in HEROES.items()}})


class Ratio(unittest.TestCase):
    def test_full_width_slash_keeps_the_maximum(self):
        # A dead hero: zero fill cannot tell 0/80 from 0/780, so the slash must be found.
        self.assertEqual(hr.ratio("0 ／780", 0.0), {"current": 0, "max": 780})

    def test_misread_slash_is_resolved_by_the_bar_fill(self):
        self.assertEqual(hr.ratio("3891780", 0.4987), {"current": 389, "max": 780})
        self.assertEqual(hr.ratio("650 l 962", 0.676), {"current": 650, "max": 962})

    def test_no_reading_fits_the_bar(self):
        self.assertIsNone(hr.ratio("500 / 1000", 0.9))

    def test_a_read_slash_keeps_its_grouping(self):
        # The bar lags a healed value; regrouping the digits (770 / 814) would fit it, wrongly.
        self.assertIsNone(hr.ratio("1770 / 1814", 0.946))
        self.assertEqual(hr.ratio("1770 / 1814", 0.976), {"current": 1770, "max": 1814})
        # ... but the slash read twice, as a digit beside it, is still undone.
        self.assertEqual(hr.ratio("2131/435", 0.4897), {"current": 213, "max": 435})
        self.assertEqual(hr.ratio(" 751 1/968 ", 0.7758), {"current": 751, "max": 968})


class Names(unittest.TestCase):
    def test_stray_marks_around_a_whole_name(self):
        self.assertEqual(NAMES.match("一、帕吉"), "npc_dota_hero_pudge")
        self.assertEqual(NAMES.match("幻影长矛手"), "npc_dota_hero_phantom_lancer")

    def test_one_misread_character_only_among_this_match(self):
        match = {"npc_dota_hero_juggernaut", "npc_dota_hero_pudge"}
        self.assertEqual(NAMES.match("一主率", match), "npc_dota_hero_juggernaut")
        self.assertEqual(NAMES.match("2妇吉", match), "npc_dota_hero_pudge")
        # Without the match's heroes a single shared character is not enough (主 is also in 孽主).
        self.assertIsNone(NAMES.match("主", None))
        self.assertIsNone(NAMES.match("主率", None))

    def test_tied_candidates_name_nobody(self):
        self.assertIsNone(NAMES.match("幻影", {"npc_dota_hero_phantom_assassin", "npc_dota_hero_phantom_lancer"}))


class Tint(unittest.TestCase):
    def test_colour_picks_within_a_grey_identical_group(self):
        refs = hr.ItemReferences.__new__(hr.ItemReferences)
        yellow, blue = np.full((40, 40, 3), (40, 200, 220), np.uint8), np.full((40, 40, 3), (220, 160, 40), np.uint8)
        refs.tint = {"item_ward_observer": hr.chroma(yellow), "item_ward_sentry": hr.chroma(blue), "item_internal": hr.chroma(blue)}
        refs.buyable = {"item_ward_observer", "item_ward_sentry"}
        seen = (blue * 0.4).astype(np.uint8)  # darker, as on cooldown
        self.assertEqual(refs.by_tint(["item_ward_observer", "item_ward_sentry"], seen)[0], "item_ward_sentry")
        # An item that cannot be bought never replaces the buyable label.
        self.assertEqual(refs.by_tint(["item_ward_observer", "item_internal"], seen)[0], "item_ward_observer")


class Leftover(unittest.TestCase):
    ROSTER = {f"npc_dota_hero_{i}" for i in range(10)}

    def test_last_unmatched_player_has_the_hero_left_over(self):
        players = {p: f"npc_dota_hero_{p}" for p in range(10) if p != 4}
        self.assertEqual(hero_scan.leftover(self.ROSTER, players, 4), "npc_dota_hero_4")

    def test_nothing_to_deduce(self):
        players = {p: f"npc_dota_hero_{p}" for p in range(10) if p != 4}
        self.assertIsNone(hero_scan.leftover(self.ROSTER, players, 3), "already matched")
        self.assertIsNone(hero_scan.leftover(self.ROSTER, players, None), "stream frame: no player selected")
        del players[5]
        self.assertIsNone(hero_scan.leftover(self.ROSTER, players, 4), "two players unmatched")
        nine = {p: f"npc_dota_hero_{p}" for p in range(10) if p != 4}
        self.assertIsNone(hero_scan.leftover(self.ROSTER - {"npc_dota_hero_4"}, nine, 4), "a hero not seen yet")


class Fits(unittest.TestCase):
    TEAMS = {"npc_dota_hero_undying": 2, "npc_dota_hero_invoker": 3, "npc_dota_hero_phantom_lancer": 3}

    def test_a_switch_teaches_only_a_hero_of_the_players_team(self):
        players = {4: "npc_dota_hero_undying", 8: "npc_dota_hero_phantom_lancer"}
        self.assertFalse(hero_scan.fits(9, "npc_dota_hero_undying", self.TEAMS, players), "the friend's hero shown while selecting a Dire player")
        self.assertFalse(hero_scan.fits(3, "npc_dota_hero_invoker", self.TEAMS, players), "a Dire hero for a Radiant hotkey")
        self.assertTrue(hero_scan.fits(9, "npc_dota_hero_invoker", self.TEAMS, players))

    def test_a_hero_matched_to_another_player_stays_there(self):
        players = {4: "npc_dota_hero_undying", 8: "npc_dota_hero_phantom_lancer"}
        self.assertFalse(hero_scan.fits(9, "npc_dota_hero_phantom_lancer", self.TEAMS, players))
        self.assertTrue(hero_scan.fits(8, "npc_dota_hero_phantom_lancer", self.TEAMS, players), "the same player again")
        self.assertTrue(hero_scan.fits(9, "npc_dota_hero_new", self.TEAMS, players), "team not known yet")


class Selection(unittest.TestCase):
    """Plus team view, friend team radiant (2): players 0-4 radiant, 5-9 dire by the spectator hotkeys."""
    TEAMS = {**{f"npc_dota_hero_r{i}": 2 for i in range(5)}, **{f"npc_dota_hero_d{i}": 3 for i in range(5)}}
    PLAYERS = {**{i: f"npc_dota_hero_r{i}" for i in range(5)}, **{5 + i: f"npc_dota_hero_d{i}" for i in range(5)}}

    def pick(self, players, visible=(), mode="team-view", allies=False, due=lambda p: True):
        return hero_scan.selectable(mode, 2, set(visible), self.TEAMS, players, due, allies)

    def test_enemies_in_sight_only(self):
        self.assertEqual(self.pick(self.PLAYERS, {"npc_dota_hero_d1", "npc_dota_hero_d4", "npc_dota_hero_r2"}), [6, 9])
        self.assertEqual(self.pick(self.PLAYERS, {"npc_dota_hero_d1"}, allies=True), [0, 1, 2, 3, 4, 6], "--allies adds the friend team")
        self.assertEqual(self.pick(self.PLAYERS, mode="full-spectator"), list(range(10)), "delayed spectating scans everyone")

    def test_unknown_players_outside_the_friend_half(self):
        players = {0: "npc_dota_hero_r0", 1: "npc_dota_hero_r1", 7: "npc_dota_hero_d2"}
        self.assertEqual(self.pick(players, {"npc_dota_hero_d2"}), [5, 6, 7, 8, 9],
                         "players 2-4 share the hotkey half of two allies; the other half may hold enemies")
        self.assertEqual(self.pick({}), list(range(10)), "nothing matched yet: everyone is tried")
        self.assertEqual(self.pick({}, due=lambda p: p in (3, 8)), [3, 8], "unknown players wait for their retry")
        mixed = {0: "npc_dota_hero_r0", 1: "npc_dota_hero_d0"}  # a custom lobby's order: no half is all allies
        self.assertEqual(self.pick(mixed), [2, 3, 4, 5, 6, 7, 8, 9])

    def test_friend_team_unknown_scans_as_before(self):
        out = hero_scan.selectable("team-view", None, {"npc_dota_hero_d0"}, self.TEAMS, self.PLAYERS, lambda p: True, False)
        self.assertEqual(out, [5])


class WardCounts(unittest.TestCase):
    @staticmethod
    def digit(d: int) -> np.ndarray:
        """A crude bright glyph of `d` dots on dark ground, enough to tell templates apart."""
        g = np.zeros(hr.DIGIT_PATCH, np.float32)
        for i in range(d + 1):
            g[2 + i, 2 + (i * 3) % 7] = 255
        return g

    def digits(self):
        return hr.Digits({str(d): (self.digit(d), np.ones(hr.DIGIT_PATCH, np.float32)) for d in range(1, 10)})

    def test_one_digit_is_found_where_counts_are_drawn(self):
        reader, value = self.digits(), np.full((56, 74), 40, np.float32)
        rows, cols = hr.DIGIT_PATCH
        (x0, _), (y0, _) = hr.COUNT_SEARCH["main"]
        xr, yb = 40 + x0 + 2, 32 + y0 + 1
        value[yb - rows + 1:yb + 1, xr - cols + 1:xr + 1] = self.digit(3)
        self.assertEqual(reader.read(value, 40, 32, hr.COUNT_SEARCH["main"]), 3)
        self.assertIsNone(reader.read(np.full((56, 74), 40, np.float32), 40, 32, hr.COUNT_SEARCH["main"]), "flat ground reads as nothing")

    def test_ward_summary(self):
        hud = object.__new__(hr.HudRecognizer)
        frame = np.zeros((1080, 1920, 3), np.uint8)
        reads = {}

        class FakeDigits:
            def read(self, value, cx, cy, search, small=False):
                return reads.get("behind" if small else "count")
        hud.digits, tint = FakeDigits(), {}
        hud.ward_tint = lambda f, cx, cy: tint.get("front")

        def summary(name, slot="slot0", **values):
            reads.clear(); reads.update(values)
            return hud.wards(frame, {slot: {"name": name}, "slot1": {"name": "item_tango"}}, 0, 0)
        tint["front"] = "observer"
        self.assertEqual(summary("item_ward_observer", count=2), {"observer": 2, "sentry": 0, "unknown": 0})
        tint["front"] = None
        self.assertEqual(summary("item_ward_sentry", count=3), {"observer": 0, "sentry": 0, "unknown": 3},
                         "a grey icon (cooldown) does not tell the kind")
        tint["front"] = "sentry"
        self.assertEqual(summary("item_ward_dispenser", count=2, behind=1), {"observer": 1, "sentry": 2, "unknown": 0},
                         "a dispenser: the front kind's count bottom right, the other's bottom left")
        self.assertEqual(summary("item_ward_dispenser", slot="slot7", count=1, behind=2), {"observer": 0, "sentry": 0, "unknown": 3},
                         "in the backpack only the total is known")
        self.assertIsNone(summary("item_ward_observer", count=None), "an unread count makes the read useless for counting")
        self.assertIsNone(summary("item_ward_dispenser", count=1, behind=None))
        self.assertEqual(summary("item_blink"), {"observer": 0, "sentry": 0, "unknown": 0})
        hud.digits = None
        self.assertIsNone(summary("item_ward_observer", count=1), "without digit templates no counts are claimed")


class ClosedWindow(unittest.TestCase):
    def test_a_closed_window_is_a_window_change_not_silence(self):
        # Capture of a closed window just stops delivering frames. On 2026-11-28 Dota restarted after the scan began and
        # the scanner, never told, watched the dead window (and sent hero keys to it) for a whole match: no enemy panel.
        from types import SimpleNamespace
        from unittest.mock import patch
        cap = object.__new__(wgc.WindowCapture)
        cap.pool, cap.window, cap.checked = 1, 42, 0.0
        alive = {42}
        now = [1.0]
        with patch.object(wgc, "call"), patch.object(wgc.time, "perf_counter", side_effect=lambda: now[0]), \
                patch.object(wgc, "user32", SimpleNamespace(IsWindow=lambda h: h in alive)):
            self.assertIsNone(cap.latest(), "no new frame from an open window")
            alive.clear()
            now[0] = 1.5
            self.assertIsNone(cap.latest(), "existence is checked at most once per WINDOW_CHECK")
            now[0] = 2.0
            with self.assertRaises(wgc.WindowChanged):
                cap.latest()


class LiveScope(unittest.TestCase):
    """Exercise the real GSI parser and snapshot builder; no window, server, keys, images or model."""
    def setUp(self):
        import threading
        from types import SimpleNamespace
        from unittest.mock import patch
        self.feed = f = object.__new__(hero_scan.GsiFeed)
        f.clock = f.state = f.match_id = f.own_team = f.ended_match = None
        f.received, f.epoch, f.mode, f.packets, f.keep_truth, f.live_ready = 0, 0, "none", 0, False, False
        f.team_view_only = True
        f.lock, f.ticks, f.views, f.truth = threading.Lock(), collections.deque(), collections.deque(), collections.deque()
        f.seen, f.visible, f.teams = {}, set(), {}
        self.scanner = sc = object.__new__(hero_scan.Scanner)
        sc.feed, sc.lock, sc.epoch, sc.selecting = f, threading.Lock(), -1, True
        sc.run_id = "test"
        sc.hud, sc.args = SimpleNamespace(manifest={"items": {}}), SimpleNamespace(select="keys", truth=False, team_view_only=True)
        self.now = 100.1
        clock = patch.object(hero_scan.time, "perf_counter", side_effect=lambda: self.now)
        clock.start(); self.addCleanup(clock.stop)
        self.hero = "npc_dota_hero_lion"

    def packet(self, team=2, match="9001", **extra):
        return {"map": {"matchid": match, "clock_time": 600, "game_state": "DOTA_GAMERULES_STATE_GAME_IN_PROGRESS"},
                "player": {"team_name": "spectator"}, "buildings": {"radiant" if team == 2 else "dire": {}},
                "minimap": {"enemy": {"unitname": self.hero, "team": 3, "xpos": 400, "ypos": 50, "visionrange": 1800}},
                **extra}

    def test_same_match_full_view_never_becomes_enemy_inventory(self):
        f, sc = self.feed, self.scanner
        f.update(self.packet(abilities={"team2": {}}), 99.5)
        self.assertEqual(f.mode, "full-spectator")
        self.assertFalse(f.seen or f.teams or f.truth or f.match_id, "packaged live scanner doesn't retain omniscient data")
        sc.snapshot()
        # Model old, queued OCR with a result from an omniscient HUD for the very same hero/match.
        sc.heroes[self.hero] = {"status": {"at": 99.6, "clock": 600, "time": 600.1, "level": 29, "alive": True, "health": None, "mana": None}}
        sc.item_slots.read(self.hero, Items.read("item_blink") | {"time": 600.1})
        self.assertIn(self.hero, sc.item_slots.shown)
        f.update(self.packet(), 100.0)
        self.assertIsNone(f.view_at(99.7), "queued frame from the previous epoch cannot be attributed to team view")
        snap = sc.snapshot()
        self.assertTrue(snap["liveReady"])
        self.assertEqual(snap["enemies"], [{"hero": self.hero, "level": None, "age": None, "items": None, "neutral": None}])
        self.assertEqual(sc.heroes, {})
        self.assertIsNone(f.view_at(103.1), "a stale team packet cannot date a new HUD read")
        self.now = 104
        self.assertEqual(sc.snapshot()["enemies"], [])
        for bad in [self.packet(match=0), self.packet(match="000"), self.packet(buildings={}), self.packet(player={}),
                    self.packet(buildings={"dire": {}, "radiant": {}}), self.packet(player=["spectator"]), self.packet(abilities="?")]:
            f.update(bad, self.now)
            self.assertFalse(sc.snapshot()["liveReady"], "missing ID/side/positive spectator identity fails closed")

    def test_silence_or_pause_fences_hud_and_charge_baselines(self):
        f, sc = self.feed, self.scanner
        f.update(self.packet(), 100)
        run = sc.snapshot()["run"]
        charges = {"wards": {"observer": 2, "sentry": 0, "unknown": 0}, "time": 600.1}
        sc.item_slots.read(self.hero, Items.read("item_ward_observer") | charges)
        self.now = 103  # Exact freshness boundary, not the ten-second process heartbeat.
        self.assertFalse(sc.snapshot()["liveReady"])
        self.assertEqual(sc.snapshot()["enemies"], [])
        f.update(self.packet(), self.now)
        snap = sc.snapshot()
        self.assertNotEqual(run, snap["run"], "the coach cannot compare charges across an outage in the same process")
        self.assertIsNone(snap["enemies"][0]["items"])
        self.assertNotIn("wards", snap["enemies"][0])
        self.assertIsNone(f.view_at(102.9))
        paused = self.packet(); paused["map"]["paused"] = True
        f.update(paused, 103.1)
        f.update(self.packet(), 103.2)
        self.now = 103.25
        self.assertNotEqual(sc.snapshot()["run"], snap["run"])
        self.assertIsNone(f.view_at(103.15), "a frame captured during pause cannot be read after resume")

    def test_recognition_uses_capture_time_and_drops_inflight_postgame(self):
        import io
        import queue
        f, sc = self.feed, self.scanner
        self.hero = "npc_dota_hero_invoker"
        hidden = self.packet(minimap={})
        hidden["map"]["clock_time"] = 599
        f.update(hidden, 99.8); f.update(self.packet(), 100)
        self.now = 100.3
        sc.work, sc.stats, sc.log = queue.Queue(), collections.Counter(), io.StringIO()
        sc.read_ms, sc.refresh_s, sc.started, sc.width1080 = [], [], 99, 32
        sc.canvas = np.zeros((1080, sc.width1080, 3), np.uint8)
        band = np.zeros((1080 - hero_scan.BAND_TOP, sc.width1080, 4), np.uint8)
        def read(at, during_ocr=lambda: None):
            def hud(frame, roster, full):
                during_ocr()
                return {"hud": True, "hero": self.hero, "level": 16, "alive": True, "health": None, "mana": None,
                        "items": dict.fromkeys(hr.SLOTS) | {"slot0": {"name": "item_ward_observer"}},
                        "wards": {"observer": 2, "sentry": 0, "unknown": 0}, "stats": None, "attributes": None}
            sc.hud.read = hud
            sc.work.put((9, True, True, at, band)); sc.work.put(None)
            sc.recognize()  # actual worker, fixture pixels and recognizer; no window or GPU
        read(99.95)
        self.assertIsNone(sc.snapshot()["enemies"][0]["items"], "later visibility cannot authorize an earlier HUD")
        read(100.2)
        snap = sc.snapshot()
        self.assertEqual(snap["enemies"][0]["wards"], [{"first": 600.2, "clock": 600.2, "observer": 2, "sentry": 0, "unknown": 0}])
        self.assertEqual((snap["scanClock"], snap["enemies"][0]["level"], sc.player_hero.get(9)), (600.3, 16, self.hero))
        ended = self.packet(); ended["map"]["game_state"] = "DOTA_GAMERULES_STATE_POST_GAME"
        read(100.25, lambda: f.update(ended, self.now))
        self.assertEqual(sc.stats["readings"], 1, "OCR that finishes across the end fence is discarded")
        self.assertEqual(sc.snapshot()["enemies"], [])
        self.now = 100.5
        f.update(self.packet(), 100.4)
        self.assertFalse(sc.snapshot()["liveReady"], "a late same-match playing packet after the terminal cannot backfill")
        f.update(self.packet(match="9002"), self.now)
        new = sc.snapshot()
        self.assertTrue(new["liveReady"])
        self.assertIsNone(new["enemies"][0]["level"])
        self.assertNotIn("wards", new["enemies"][0])

    def test_capture_time_visibility_not_later_reappearance(self):
        f = self.feed
        f.update(self.packet(minimap={}), 99)
        f.update(self.packet(), 100)
        self.assertNotIn(self.hero, f.view_at(99.7)[2])
        self.assertIn(self.hero, f.view_at(100.1)[2])
        hidden = self.packet()
        hidden["minimap"]["enemy"].update(xpos=0, ypos=0)
        f.update(hidden, 100.2)
        self.assertNotIn(self.hero, f.visible, "a placeholder at the origin is not a new sighting")
        self.now = 100.25
        self.assertTrue(self.scanner.snapshot()["liveReady"])
        self.scanner.args.truth = True
        self.assertFalse(self.scanner.snapshot()["liveReady"], "offline truth runs never contribute live rows")

    def test_full_spectator_truth_remains_offline_only(self):
        f, sc = self.feed, self.scanner
        f.team_view_only = sc.args.team_view_only = False
        f.keep_truth = sc.args.truth = True
        f.update(self.packet(hero={"team3": {"player9": {"name": self.hero, "level": 29}}},
                             items={"team3": {"player9": {"slot0": {"name": "item_blink"}}}}), 100)
        snap = sc.snapshot()
        self.assertEqual(snap["mode"], "full-spectator")
        self.assertEqual(snap["heroes"][self.hero]["truth"]["items"]["slot0"], "item_blink")
        self.assertFalse(snap["liveReady"] or snap["enemies"])
        f.update(self.packet(), self.now)
        self.assertFalse(f.truth, "even a same-match switch clears offline truth")
        self.assertFalse(sc.snapshot()["enemies"])

    def test_packaged_scan_never_writes_a_parallel_session(self):
        import tempfile
        with tempfile.TemporaryDirectory() as work:
            sc = self.scanner
            sc.args.out = Path(work)
            self.feed.update(self.packet(), 100)
            snap = sc.snapshot()
            sc.record(snap); sc.write_state(snap)
            self.assertEqual(list(sc.args.out.iterdir()), [], "only the matched coach archives live readings")


class Items(unittest.TestCase):
    MANIFEST = {"item_blink": {"icon": "items/blink.png"}, "item_bfury": {"icon": "items/bfury.png"},
                "item_boots": {"icon": "items/boots.png"}, "item_tango": {"consumable": True, "icon": "items/tango.png"},
                "item_recipe_bfury": {"recipe": True}, "item_pogo_stick": {"icon": "items/pogo.png"}}

    @staticmethod
    def read(*names, neutral=None):
        return dict.fromkeys(hr.SLOTS) | {**{f"slot{i}": n for i, n in enumerate(names)}, "neutral0": neutral, "teleport0": "item_tpscroll"}

    def test_the_latest_clean_read_is_shown(self):
        slots, hero = hero_scan.ItemSlots(self.MANIFEST), "npc_dota_hero_juggernaut"
        slots.read(hero, self.read("item_boots"))
        self.assertEqual(slots.shown[hero], ("item_boots", None, None, None, None, None, None))
        slots.read(hero, self.read("item_boots", "item_bfury", neutral="item_pogo_stick"))
        self.assertEqual(slots.shown[hero], ("item_boots", "item_bfury", None, None, None, None, "item_pogo_stick"))
        slots.read(hero, self.read("item_boots", "?"))
        self.assertEqual(slots.shown[hero][1], "item_bfury", "a read with an unknown slot keeps the last one")

    def test_recipes_are_empty_and_unknown_slots_skip_the_read(self):
        slots, hero = hero_scan.ItemSlots(self.MANIFEST), "npc_dota_hero_juggernaut"
        slots.read(hero, self.read("?", "item_boots"))
        self.assertNotIn(hero, slots.shown, "a read with an unknown slot is skipped")
        slots.read(hero, self.read("item_tango", "item_recipe_bfury", "item_recipe", "item_boots"))
        self.assertEqual(slots.shown[hero][:4], ("item_tango", None, None, "item_boots"), "consumables stay, recipes do not")

    def test_partial_or_older_item_read_does_not_replace_a_dated_inventory(self):
        slots, hero = hero_scan.ItemSlots(self.MANIFEST), "npc_dota_hero_lion"
        complete = self.read("item_boots") | {"time": 600.35, "wards": {"observer": 0, "sentry": 0, "unknown": 0}}
        partial = dict(complete); del partial["slot1"]
        slots.read(hero, partial)
        self.assertNotIn(hero, slots.shown, "an absent slot is not evidence that the slot is empty")
        self.assertNotIn(hero, slots.wards)
        slots.read(hero, complete)
        slots.read(hero, self.read("item_blink") | {"time": 600.2})
        self.assertEqual((slots.shown[hero][0], slots.shown_time[hero]), ("item_boots", 600.35))

    def test_ward_reads_are_kept_from_complete_reads(self):
        slots, hero = hero_scan.ItemSlots(self.MANIFEST), "npc_dota_hero_lion"
        wards = lambda o, time: {"wards": {"observer": o, "sentry": 0, "unknown": 0}, "time": time}
        slots.read(hero, self.read("item_boots") | wards(2, 600.15))
        slots.read(hero, self.read("item_boots") | wards(2, 600.45))
        slots.read(hero, self.read("item_boots") | wards(2, 600.3))
        slots.read(hero, self.read("item_boots", "?") | wards(1, 603.2))
        slots.read(hero, self.read("item_boots") | {"wards": None, "time": 606.2})
        self.assertEqual(list(slots.wards[hero]), [{"first": 600.15, "clock": 600.45, "observer": 2, "sentry": 0, "unknown": 0}],
                         "one entry per change of the counts, first and latest read; reads out of order, with an unknown slot "
                         "or unread counts are left out")
        self.assertTrue(slots.carrying(hero) and not slots.carrying("npc_dota_hero_pudge"))
        backpack_unknown = self.read("item_boots") | {"slot7": "?"} | wards(1, 609)
        slots.read(hero, backpack_unknown)
        self.assertEqual(len(slots.wards[hero]), 1, "an unknown backpack slot could hide a ward")
        slots.read(hero, self.read("item_boots") | wards(1, 612.6))
        self.assertEqual(slots.wards[hero][-1], {"first": 612.6, "clock": 612.6, "observer": 1, "sentry": 0, "unknown": 0},
                         "a ward went down between 600.45 and 612.6")
        for i, n in enumerate([0, 2, 1, 0, 3]):
            slots.read(hero, self.read("item_boots") | wards(n, 620 + i))
        self.assertEqual([(r["first"], r["observer"]) for r in slots.wards[hero]], [(621, 2), (622, 1), (623, 0), (624, 3)],
                         "the last few changes only")
        self.assertTrue(slots.carrying(hero))
        slots.read(hero, self.read("item_boots") | wards(0, 625))
        self.assertFalse(slots.carrying(hero), "the newest read had none")

    def test_reads_are_timed_within_their_second(self):
        feed = object.__new__(hero_scan.GsiFeed)
        feed.ticks = collections.deque([(100.0, 599), (100.93, 600), (101.95, 601)])
        self.assertEqual(feed.game_time(101.5), 600.57, "the arrival of second 600 starts it")
        self.assertEqual(feed.game_time(102.0), 601.05)
        self.assertEqual(feed.game_time(103.4), 601.99, "never past the next second (paused, or a packet late)")
        self.assertIsNone(feed.game_time(99.0), "before the first packet")

    def test_carriers_are_due_for_items_sooner(self):
        import threading
        from types import SimpleNamespace
        scanner = object.__new__(hero_scan.Scanner)
        heroes = {"npc_dota_hero_lion": 2, "npc_dota_hero_pudge": 3, "npc_dota_hero_axe": 3}
        scanner.feed = SimpleNamespace(lock=threading.Lock(), mode="team-view", own_team=2, visible={"npc_dota_hero_pudge", "npc_dota_hero_axe"},
                                       teams=heroes, epoch=1, live_ready=True, received=99)
        scanner.lock, scanner.epoch, scanner.items_queued = threading.Lock(), 1, {}
        scanner.tried = {p: 100.0 for p in (7, 8, 9)}  # unknown players tried just now: not due again yet
        scanner.player_hero = {0: "npc_dota_hero_lion", 5: "npc_dota_hero_pudge", 6: "npc_dota_hero_axe"}
        scanner.args = SimpleNamespace(allies=False, team_view_only=True, item_visits=1, items_every=3.0, ward_items_every=0.4)
        scanner.item_slots = hero_scan.ItemSlots(self.MANIFEST)
        scanner.item_slots.read("npc_dota_hero_pudge", self.read("item_boots") | {"wards": {"observer": 1, "sentry": 0, "unknown": 0}, "time": 99.0})
        scanner.heroes = {"npc_dota_hero_pudge": {"items": {"at": 99.5}}, "npc_dota_hero_axe": {"items": {"at": 98.0}}}
        self.assertEqual(scanner.plan(100.0), [(5, True), (6, False)], "Pudge carries a ward: due 0.4 s after his last item read")
        self.assertEqual(scanner.plan(101.5), [(5, False), (6, True)], "when both are due the longer-waiting one goes first")

    def test_live_file_lists_enemies_in_player_order_with_their_icons(self):
        import json
        import tempfile
        import threading
        from types import SimpleNamespace
        with tempfile.TemporaryDirectory() as root:
            root = Path(root)
            for icon in ["items/boots.png", "heroes/pudge.png", "heroes/jugg.png"]:
                (root / "assets" / icon).parent.mkdir(parents=True, exist_ok=True)
                (root / "assets" / icon).write_bytes(b"icon " + icon.encode())
            scanner = object.__new__(hero_scan.Scanner)
            scanner.args = SimpleNamespace(live=root / "live" / "live.json", assets=root / "assets")
            scanner.hud = SimpleNamespace(manifest={"items": self.MANIFEST, "heroes": {
                "npc_dota_hero_pudge": {"icon": "heroes/pudge.png"}, "npc_dota_hero_juggernaut": {"icon": "heroes/jugg.png"}}})
            scanner.lock, scanner.exported, scanner.run_id = threading.Lock(), set(), "ab12"
            scanner.item_slots = hero_scan.ItemSlots(self.MANIFEST)
            scanner.item_slots.read("npc_dota_hero_pudge", self.read("item_boots") | {"wards": {"observer": 1, "sentry": 0, "unknown": 0}, "time": 590.4})
            scanner.item_slots.read("npc_dota_hero_juggernaut", self.read("?"))
            hero = lambda team, status: {"team": team, "status": status, "items": None, "seen": None}
            snap = {"mode": "team-view", "matchId": "9001", "ownTeam": 2, "clock": 600, "scanClock": 600.0,
                    "selecting": True, "liveReady": True, "run": "ab12-1", "sampledUnix": 1800000000.123, "gsiAge": 0.125,
                    "players": {"7": "npc_dota_hero_pudge", "1": "npc_dota_hero_lion"},
                    "heroes": {"npc_dota_hero_lion": hero(2, {"level": 9, "age": 0.2}),
                               "npc_dota_hero_pudge": hero(3, {"level": 11, "age": 4.5, "time": 595.5}),
                               "npc_dota_hero_juggernaut": hero(3, None)}}
            snap["enemies"] = scanner.enemies(snap, scanner.item_slots.shown, {"npc_dota_hero_pudge": 9.6},
                                              {h: list(v) for h, v in scanner.item_slots.wards.items()})
            scanner.write_live(snap)
            live = json.loads(scanner.args.live.read_text(encoding="utf-8"))
            self.assertEqual(live["version"], 2)
            self.assertEqual(live["enemies"], [
                {"hero": "npc_dota_hero_pudge", "level": 11, "age": 9.6, "items": ["item_boots", None, None, None, None, None],
                 "neutral": None, "wards": [{"first": 590.4, "clock": 590.4, "observer": 1, "sentry": 0, "unknown": 0}]},
                {"hero": "npc_dota_hero_juggernaut", "level": None, "age": None, "items": None, "neutral": None}],
                "allies left out; matched players first; a never-read enemy has nulls; a level read never refreshes old gear")
            snap["heroes"]["npc_dota_hero_juggernaut"]["status"] = {"level": None, "time": 599.9}
            unread = scanner.enemies(snap, scanner.item_slots.shown, {"npc_dota_hero_pudge": 9.6}, {})[1]
            self.assertIsNone(unread["age"], "a HUD with neither readable level nor equipment has no last-read age")
            icons = sorted(p.name for p in (root / "live" / "icons").iterdir())
            self.assertEqual(icons, ["item_boots.png", "npc_dota_hero_juggernaut.png", "npc_dota_hero_pudge.png"])
            scanner.write_live(snap | {"mode": "full-spectator"})
            self.assertEqual(json.loads(scanner.args.live.read_text(encoding="utf-8"))["enemies"], [],
                             "full spectating never fills the panel")
            # A reader holding the file open without sharing it for delete (as the coach client did) blocks replacing
            # it on Windows: the scanner skips that write instead of stopping, and the next one goes through.
            with open(scanner.args.live, "rb"):
                scanner.write_live(snap)
            self.assertEqual(json.loads(scanner.args.live.read_text(encoding="utf-8"))["mode"], "full-spectator")
            scanner.write_live(snap)
            self.assertEqual(json.loads(scanner.args.live.read_text(encoding="utf-8")), live, "writing the same snapshot doesn't restamp it")
            scanner.write_live(snap, stopped=True)
            stopped = json.loads(scanner.args.live.read_text(encoding="utf-8"))
            self.assertTrue(stopped["stopped"])
            self.assertEqual(stopped["enemies"], [])
            for invalid in [{"gsiAge": 3.0}, {"gsiAge": None}, {"liveReady": False}]:
                scanner.write_live(snap | invalid)
                self.assertEqual(json.loads(scanner.args.live.read_text(encoding="utf-8"))["enemies"], [])


if __name__ == "__main__":
    unittest.main()
