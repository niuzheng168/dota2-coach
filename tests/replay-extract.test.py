"""Optional parser/decompression tests; run in the isolated replay venv."""
import bz2
import hashlib
import importlib.util
import json
import os
from pathlib import Path
import subprocess
import sys
import tempfile
from types import SimpleNamespace
import unittest
import zstandard


def load(name, path):
    spec = importlib.util.spec_from_file_location(name, path)
    mod = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(mod)
    return mod


sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "scripts/replays"))
extract = load("coach_extract", "scripts/replays/extract.py")
decompress = load("coach_decompress", "scripts/replays/decompress.py")
fog = load("coach_team_view", "scripts/replays/team_view.py")
spells = load("coach_spell_witness", "scripts/replays/spell_witness.py")


class ReplayTests(unittest.TestCase):
    def test_spell_witness_accepts_visible_teamless_cast_only_with_enemy_roster_label(self):
        actor, own = "npc_dota_hero_sand_king", "npc_dota_hero_troll_warlord"
        e = SimpleNamespace(tick=88, log_type="ABILITY", visible_to=lambda side: side == 3, source="s2_direct",
                            attacker_name=actor, inflictor_name="sandking_burrowstrike", attacker_team=None, ability_level=1)
        self.assertEqual(spells.witness(e, 3, own, {actor}), ["cast", 88, actor, "sandking_burrowstrike", 1, "roster-label"])
        self.assertIsNone(spells.witness(e, 3, own, set()))
        self.assertIsNone(spells.witness(e, 2, own, {actor}))
        e.attacker_team = 3
        self.assertIsNone(spells.witness(e, 3, own, {actor}))
        e.attacker_team, e.target_team, e.target_name = 2, 3, own
        e.log_type, e.target_is_hero, e.target_is_hero_present = "DAMAGE", True, True
        e.target_is_illusion_present, e.target_is_illusion, e.value, e.damage_type = True, False, 58, "magical"
        self.assertEqual(spells.witness(e, 3, own, {actor})[5:], [58, "magical", True])
        e.target_is_illusion_present = False
        self.assertFalse(spells.witness(e, 3, own, {actor})[-1])
        e.visible_to = lambda side: None
        self.assertIsNone(spells.witness(e, 3, own, {actor}))

    def test_enemy_sheet_does_not_query_hidden_values_or_identify_real_body_among_images(self):
        class E:
            def __init__(self, state):
                self.state = state
            def get(self, key):
                assert self.state == "visible", "read hidden enemy stats"
                return {"m_lifeState": 0, "m_iCurrentLevel": 5, "m_flMana": 156.9}[key]
        hid, vis = E("hidden"), E("visible")
        self.assertIsNone(spells.visible_sheet([hid], lambda e: e.state))
        self.assertEqual(spells.visible_sheet([vis, hid], lambda e: e.state), [5, 156])
        self.assertIsNone(spells.visible_sheet([vis, E("visible")], lambda e: e.state))
    def test_team_visibility_words_hidden_is_not_missing_and_high_bit_survives(self):
        class Team:
            active = True
            def __init__(self):
                self.calls = []
            def get_uint64(self, key):
                self.calls.append(key)
                return {"m_bNPCVisibleState.0000": 1 << 63, "m_bNPCVisibleState.0001": 0}.get(key)
        team, words = Team(), {}
        self.assertEqual([fog.visibility(team, i, words) for i in (63, 62, 64, 130)], ["visible", "hidden", "hidden", "unknown"])
        self.assertEqual(len(team.calls), 3)
        self.assertEqual(fog.visibility(None, 63, {}), "unknown")
        team.active = False
        self.assertEqual(fog.visibility(team, 63, words), "unknown")

    def test_hidden_body_is_never_read_and_real_body_swap_cannot_identify_an_image(self):
        class E:
            def __init__(self, x, visible):
                self.x, self.visible = x, visible
            def get(self, field):
                assert self.visible, "hidden coordinates or life state read"
                return {"m_lifeState": 0, "CBodyComponent.m_cellX": 128, "CBodyComponent.m_cellY": 128,
                        "CBodyComponent.m_vecX": self.x, "CBodyComponent.m_vecY": 0}[field]
        unit = lambda *looks: fog.project_enemy(3, "npc_dota_hero_axe", looks, lambda e: "visible" if e.visible else "hidden")
        hidden, close, far = E(99999, False), E(30, True), E(950, True)
        self.assertEqual(unit(hidden)["markers"], [])
        self.assertEqual(unit(hidden, close), unit(close, hidden))
        self.assertEqual(unit(far, close), unit(close, far))
        self.assertEqual([m["xy"] for m in unit(close, far)["markers"]], [[30, 0], [950, 0]])
        self.assertNotIn("99999", json.dumps(unit(hidden, close)))

    def test_visible_pack_gross_is_not_a_payout_or_a_guess_for_unknown_units(self):
        unit = lambda x, hp, lo, hi: ["npc_dota_creep_goodguys_melee", [x, 0], hp, 400, lo, hi, None]
        rows = [unit(10, 120, 30, 40), unit(450, 90, 35, 42), unit(1500, 150, 36, 46)]
        pack = fog.pack(rows, [0, 0], [[300, 0]], [[1000, 0]])
        self.assertEqual((pack["straightDistance"], len(pack["units"]), pack["listedGross"]), (10, 2, [65, 82]))
        self.assertEqual(pack["nearestAlliedTowerDistance"], 990)
        self.assertIsNone(fog.pack([unit(20, 140, None, 40)], [0, 0], [], [])["listedGross"])
        self.assertIsNone(fog.pack([], [0, 0], [], []))

    def test_spirit_bear_or_hidden_damage_is_not_its_owners_map_commitment(self):
        e = SimpleNamespace(log_type="DAMAGE", visible_to=lambda team: team == 2, attacker_team=2, target_team=3,
                            attacker_is_hero_present=True, attacker_is_hero=True, target_is_hero_present=True, target_is_hero=True,
                            attacker_name="npc_dota_hero_lone_druid", target_name="npc_dota_hero_axe", value=90)
        self.assertTrue(fog.visible_hero_contact(e, 2))
        self.assertFalse(fog.visible_hero_contact(e, 3))
        e.attacker_name = "npc_dota_lone_druid_bear1"
        self.assertFalse(fog.visible_hero_contact(e, 2))

    def test_cli_storage_and_explicit_root(self):
        cli = Path(decompress.__file__).resolve()
        with tempfile.TemporaryDirectory() as temp:
            work = Path(temp) / "external data"
            env = {**os.environ, "DOTA_COACH_REPLAY_WORK_ROOT": str(work)}
            body = b"PBDEMS2\0fixture"
            archive = bz2.compress(body)
            for folder, args in [(work / "replays", []), (Path(temp) / "explicit", ["--root", str(Path(temp) / "explicit")])]:
                folder.mkdir(parents=True)
                (folder / "1.archive").write_bytes(archive)
                (folder / "1.acquisition.json").write_text(json.dumps({"archiveSha256": hashlib.sha256(archive).hexdigest()}))
                subprocess.run([sys.executable, "-B", str(cli), "1", *args], cwd=temp, env=env,
                               capture_output=True, check=True)
                self.assertEqual((folder / "1.dem").read_bytes(), body)
                self.assertTrue((folder / "tmp").is_dir())
            self.assertFalse((Path(temp) / ".tmp").exists())

    def test_coordinates_do_not_snap_or_flip_axes(self):
        class Entity:
            def __init__(self, values):
                self.values = values
            def get(self, name):
                return self.values.get(name)
        e = Entity({"CBodyComponent.m_cellX": 128, "CBodyComponent.m_cellY": 126,
                    "CBodyComponent.m_vecX": 0, "CBodyComponent.m_vecY": 128.5})
        self.assertEqual(extract.position(e), {"x": 0, "y": -127.5})
        self.assertIsNone(extract.position(Entity({})))

    def test_magic_detection_hash_verification_and_no_overwrite(self):
        body = b"PBDEMS2\0" + b"test bounded decompression" * 20
        for compress in (bz2.compress, zstandard.ZstdCompressor().compress):
            with tempfile.TemporaryDirectory() as tmp:
                root = Path(tmp)
                archive = compress(body)
                (root / "1.archive").write_bytes(archive)
                (root / "1.acquisition.json").write_text(json.dumps({"archiveSha256": hashlib.sha256(archive).hexdigest()}))
                result = decompress.decompress(root, 1)
                self.assertEqual(result["demSha256"], hashlib.sha256(body).hexdigest())
                with self.assertRaises(FileExistsError):
                    decompress.decompress(root, 1)

    def test_corrupt_archive_is_rejected(self):
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            (root / "1.archive").write_bytes(b"corrupt")
            (root / "1.acquisition.json").write_text(json.dumps({"archiveSha256": "0" * 64}))
            with self.assertRaises(ValueError):
                decompress.decompress(root, 1)


if __name__ == "__main__":
    unittest.main()
