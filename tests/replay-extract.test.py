"""Optional parser/decompression tests; run in the isolated replay venv."""
import bz2
import hashlib
import importlib.util
import json
from pathlib import Path
import tempfile
import unittest
import zstandard


def load(name, path):
    spec = importlib.util.spec_from_file_location(name, path)
    mod = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(mod)
    return mod


extract = load("coach_extract", "scripts/replays/extract.py")
decompress = load("coach_decompress", "scripts/replays/decompress.py")


class ReplayTests(unittest.TestCase):
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
