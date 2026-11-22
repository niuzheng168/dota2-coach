"""Private, network-free child. Only the supervisor publishes the finished result."""
import hashlib
from importlib.metadata import version
import json
import logging
import os
from pathlib import Path
import stat
import sys

HERE = Path(__file__).resolve().parent
sys.path[:0] = [str(HERE), str(HERE.parent)]  # also in isolated (-I / ._pth) Python
from common import MAX_OUTPUT, progress, report


def snapshot(source, target, limit, *, hash_archive=False):
    digest = hashlib.sha256() if hash_archive else None
    with source.open("rb") as src, target.open("xb") as out:
        before = os.fstat(src.fileno())
        if not stat.S_ISREG(before.st_mode) or not 16 < before.st_size <= limit:
            raise ValueError("Expected a bounded regular DEM file")
        size = 0
        while chunk := src.read(1024**2):
            size += len(chunk)
            if size > before.st_size or size > limit:
                raise ValueError("Input DEM changed while copying")
            if digest:
                digest.update(chunk)
            out.write(chunk)
        after = os.fstat(src.fileno())
        if size != before.st_size or before.st_mtime_ns != after.st_mtime_ns or after.st_size != size:
            raise ValueError("Input DEM changed while copying")
    return digest.hexdigest() if digest else None


def write_result(result, target):
    # Bound bytes on disk, not just number of Python objects; no NaN/Infinity/BOM.
    head = {k: v for k, v in result.items() if k != "events"}
    def encode(value):
        return json.dumps(value, ensure_ascii=True, allow_nan=False, separators=(",", ":")).encode("ascii")
    with target.open("xb") as out:
        out.write(encode(head)[:-1] + b',"events":[')
        for i, event in enumerate(result["events"]):
            row = encode(event)
            if out.tell() + len(row) + 4 > MAX_OUTPUT:
                raise ValueError("Timeline exceeds output byte budget")
            out.write((b"," if i else b"") + row)
        out.write(b"]}\n")
        out.flush()
        os.fsync(out.fileno())


def work(match_id, root, source):
    from decompress import decompress
    from verify import inspect_dem, MAX_DEM
    from timeline import extract

    dem = root / f"{match_id}.dem"
    expected_sha = None
    compressed_local = source is not None and source.name.lower().endswith(".dem.bz2")
    if source is not None:
        progress("copy", bytes=source.stat().st_size)
        if compressed_local:
            sha = snapshot(source, root / f"{match_id}.archive", 512 * 1024**2, hash_archive=True)
            (root / f"{match_id}.acquisition.json").write_text(json.dumps({"archiveSha256": sha}), encoding="utf8")
        else:
            snapshot(source, dem, MAX_DEM)  # never parse a changing user path
    if source is None or compressed_local:
        progress("decompress")
        expected_sha = decompress(root, match_id)["demSha256"]
    progress("verify")
    meta = inspect_dem(dem, match_id)
    if expected_sha is not None and meta.sha256 != expected_sha:
        raise ValueError("Decompressed DEM hash mismatch")
    result = extract(dem, meta, progress)
    progress("serialize", events=len(result["events"]), replaySha256=meta.sha256)
    write_result(result, root / "timeline.partial")


if __name__ == "__main__":
    logging.disable(logging.CRITICAL)  # parser warnings are not a success signal; check parse_error explicitly
    try:
        if sys.version_info[:3] != (3, 13, 13) or version("gem-dota") != "0.14.0":
            raise ValueError("Expected CPython 3.13.13 and gem-dota 0.14.0")
        work(int(sys.argv[1]), Path(sys.argv[2]), Path(sys.argv[3]) if len(sys.argv) == 4 else None)
    except Exception as exc:
        report("error", "verify/extract", message=str(exc)[:240] or type(exc).__name__)
        sys.exit(4)
