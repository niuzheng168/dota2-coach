"""Offline bounded decompression; extension does not determine compression."""
import argparse
import bz2
import hashlib
import json
from pathlib import Path
import time


def decompress(root: Path, match_id: int) -> dict:
    archive = root / f"{match_id}.archive"
    output = root / f"{match_id}.dem"
    partial = root / f"{match_id}.dem.partial"
    manifest = root / f"{match_id}.acquisition.json"
    evidence = json.loads(manifest.read_text(encoding="utf-8"))
    if output.exists() or partial.exists():
        raise FileExistsError("Existing replay/partial file; refusing to overwrite evidence")
    with archive.open("rb") as f:
        if hashlib.file_digest(f, "sha256").hexdigest() != evidence["archiveSha256"]:
            raise ValueError("Archive hash mismatch")
    with archive.open("rb") as f:
        magic = f.read(4)
    if magic[:3] == b"BZh":
        reader = bz2.open(archive, "rb")
    elif magic == b"\x28\xb5\x2f\xfd":
        import zstandard
        reader = zstandard.ZstdDecompressor().stream_reader(archive.open("rb"))
    else:
        raise ValueError("Unknown compression magic")
    size, started, sha = 0, time.monotonic(), hashlib.sha256()
    with reader, partial.open("xb") as f:
        while chunk := reader.read(1024 * 1024):
            if size == 0 and not chunk.startswith(b"PBDEMS2\0"):
                raise ValueError("Not a Source 2 replay")
            size += len(chunk)
            if size > 2 * 1024**3 or time.monotonic() - started > 120:
                raise ValueError("Decompression limit exceeded")
            sha.update(chunk)
            f.write(chunk)
    if size == 0:
        raise ValueError("Empty replay")
    partial.rename(output)
    evidence.update(demSha256=sha.hexdigest(), demBytes=size)
    manifest.write_text(json.dumps(evidence, indent=2) + "\n", encoding="utf-8")
    return evidence


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("match_id", type=int)
    parser.add_argument("--root", type=Path, default=Path(".tmp/replays"))
    args = parser.parse_args()
    print(json.dumps(decompress(args.root, args.match_id), indent=2))
