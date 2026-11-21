"""Pure-logic tests for the HUD recognizer and hero scanner; run in the isolated .tmp/vision-tools venv:
.tmp/vision-tools/Scripts/python.exe -I tests/hud-vision.test.py (no GPU or game needed)."""
import sys
import unittest
from pathlib import Path

import numpy as np

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "scripts/vision"))
import hud_recognizer as hr  # noqa: E402
import hero_scan  # noqa: E402

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


class Items(unittest.TestCase):
    MANIFEST = {"item_blink": {"icon": "items/blink.png"}, "item_bfury": {"icon": "items/bfury.png"},
                "item_boots": {"icon": "items/boots.png"}, "item_tango": {"consumable": True, "icon": "items/tango.png"},
                "item_recipe_bfury": {"recipe": True}, "item_pogo_stick": {"icon": "items/pogo.png"}}

    @staticmethod
    def read(*names, neutral=None):
        return {**{f"slot{i}": n for i, n in enumerate(names)}, "neutral0": neutral, "teleport0": "item_tpscroll"}

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

    def test_ward_reads_are_kept_from_complete_reads(self):
        slots, hero = hero_scan.ItemSlots(self.MANIFEST), "npc_dota_hero_lion"
        wards = lambda o, clock: {"wards": {"observer": o, "sentry": 0, "unknown": 0}, "clock": clock}
        slots.read(hero, self.read("item_boots") | wards(2, 600))
        slots.read(hero, self.read("item_boots") | wards(2, 600))
        slots.read(hero, self.read("item_boots", "?") | wards(1, 603))
        slots.read(hero, self.read("item_boots") | {"wards": None, "clock": 606})
        self.assertEqual(list(slots.wards[hero]), [{"clock": 600, "observer": 2, "sentry": 0, "unknown": 0}],
                         "one entry per read clock; reads with an unknown slot or unread counts are left out")
        backpack_unknown = self.read("item_boots") | {"slot7": "?"} | wards(1, 609)
        slots.read(hero, backpack_unknown)
        self.assertEqual(len(slots.wards[hero]), 1, "an unknown backpack slot could hide a ward")
        for clock in range(612, 640, 3):
            slots.read(hero, self.read("item_boots") | wards(1, clock))
        self.assertEqual([r["clock"] for r in slots.wards[hero]], [630, 633, 636, 639], "the last few reads only")

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
            scanner.item_slots.read("npc_dota_hero_pudge", self.read("item_boots") | {"wards": {"observer": 1, "sentry": 0, "unknown": 0}, "clock": 590})
            scanner.item_slots.read("npc_dota_hero_juggernaut", self.read("?"))
            hero = lambda team, status: {"team": team, "status": status, "items": None, "seen": None}
            snap = {"mode": "team-view", "matchId": "9001", "ownTeam": 2, "clock": 600, "selecting": True,
                    "players": {"7": "npc_dota_hero_pudge", "1": "npc_dota_hero_lion"},
                    "heroes": {"npc_dota_hero_lion": hero(2, {"level": 9, "age": 0.2}),
                               "npc_dota_hero_pudge": hero(3, {"level": 11, "age": 4.5}),
                               "npc_dota_hero_juggernaut": hero(3, None)}}
            scanner.write_live(snap)
            live = json.loads(scanner.args.live.read_text(encoding="utf-8"))
            self.assertEqual(live["version"], 2)
            self.assertEqual(live["enemies"], [
                {"hero": "npc_dota_hero_pudge", "level": 11, "age": 4.5, "items": ["item_boots", None, None, None, None, None],
                 "neutral": None, "wards": [{"clock": 590, "observer": 1, "sentry": 0, "unknown": 0}]},
                {"hero": "npc_dota_hero_juggernaut", "level": None, "age": None, "items": None, "neutral": None}],
                "allies left out; matched players first; a never-read enemy has nulls")
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
            self.assertEqual(json.loads(scanner.args.live.read_text(encoding="utf-8"))["mode"], "team-view")


if __name__ == "__main__":
    unittest.main()
