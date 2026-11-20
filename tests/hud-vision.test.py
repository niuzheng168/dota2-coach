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
            scanner.item_slots.read("npc_dota_hero_pudge", self.read("item_boots"))
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
                 "neutral": None},
                {"hero": "npc_dota_hero_juggernaut", "level": None, "age": None, "items": None, "neutral": None}],
                "allies left out; matched players first; a never-read enemy has nulls")
            icons = sorted(p.name for p in (root / "live" / "icons").iterdir())
            self.assertEqual(icons, ["item_boots.png", "npc_dota_hero_juggernaut.png", "npc_dota_hero_pudge.png"])
            scanner.write_live(snap | {"mode": "full-spectator"})
            self.assertEqual(json.loads(scanner.args.live.read_text(encoding="utf-8"))["enemies"], [],
                             "full spectating never fills the panel")


if __name__ == "__main__":
    unittest.main()
