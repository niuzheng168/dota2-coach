"""Verify and extract one *completed* Dota 2 replay; no live/observer mode."""
import argparse
import os
from pathlib import Path, PureWindowsPath
import re
import shutil
import signal
import stat
import subprocess
import sys
import tempfile
import time

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE))
from common import MAX_OUTPUT, progress, report


class Arguments(argparse.ArgumentParser):
    def error(self, message):
        raise ValueError(message)


class ChildFailed(Exception):
    def __init__(self, code):
        self.code = code


def local_path(text):
    if not text or text.startswith(("\\\\", "//")) or "://" in text:
        raise ValueError("Expected a local path, not a URL, UNC path or device")
    path = Path(text).absolute()
    if os.name == "nt":
        from ctypes import windll
        w = PureWindowsPath(path)
        if (not w.drive or w.drive.startswith("\\") or ":" in str(w)[len(w.drive):]
                or any(os.path.isreserved(part) for part in w.parts[1:])):
            raise ValueError("Device paths and alternate data streams are unsupported")
        if windll.kernel32.GetDriveTypeW(w.drive + "\\") == 4:
            raise ValueError("Network drives are not local input/output")
        # Do not resolve a junction/placeholder and accidentally open a network path.
        for part in (*reversed(path.parents), path):
            try:
                attrs = part.lstat().st_file_attributes
            except FileNotFoundError:
                break
            if attrs & stat.FILE_ATTRIBUTE_REPARSE_POINT:
                raise ValueError("Replay paths must not pass through reparse points")
    return path.resolve()


def cancellation(marker):
    if marker is not None and marker.exists():
        raise KeyboardInterrupt


def run_child(command, root, stage, timeout, marker, failure):
    cancellation(marker)
    progress(stage)
    # Neither the downloader nor the offline parser gets the application's environment/secrets.
    env = {k: os.environ[k] for k in ("SystemRoot", "WINDIR", "SystemDrive", "TEMP", "TMP") if k in os.environ}
    env.update(OPENBLAS_NUM_THREADS="1", OMP_NUM_THREADS="1", NODE_NO_WARNINGS="1")
    process = subprocess.Popen(command, shell=False, cwd=root, env=env,
                               stdin=subprocess.DEVNULL, stdout=subprocess.DEVNULL)
    deadline, heartbeat = time.monotonic() + timeout, time.monotonic() + 10
    try:
        while process.poll() is None:
            cancellation(marker)
            now = time.monotonic()
            if now >= deadline:
                raise TimeoutError(f"{stage} exceeded {timeout}s budget")
            if now >= heartbeat:
                progress(stage, elapsedSeconds=round(now - (deadline - timeout)))
                heartbeat = now + 10
            time.sleep(0.1)
        if process.returncode:
            report("error", stage, message=f"Child exited {process.returncode}; no verified output")
            raise ChildFailed(failure)
    finally:
        if process.poll() is None:
            process.kill()
        process.wait()


def publish(staged, dest):
    if not 0 < staged.stat().st_size <= MAX_OUTPUT:
        raise ValueError("Invalid staged timeline size")
    # Same volume. No replace, even if another caller wins the race after the initial exists() check.
    if os.name == "nt":
        os.rename(staged, dest)
    else:
        os.link(staged, dest)


def main(argv=None):
    ap = Arguments(description=__doc__, allow_abbrev=False)
    ap.add_argument("--match-id", required=True)
    mode = ap.add_mutually_exclusive_group(required=True)
    mode.add_argument("--download", action="store_true", help="one public OpenDota lookup and at most one Valve GET")
    mode.add_argument("--dem", help="offline regular local SourceTV .dem or .dem.bz2 (bzip2/Zstd); no network")
    ap.add_argument("--output", required=True, help="new JSON file in an existing local directory")
    ap.add_argument("--cancel-file", help="caller creates this marker to cancel; wait for exit 130")
    args = ap.parse_args(argv)
    if not re.fullmatch(r"[1-9][0-9]{0,15}", args.match_id) or int(args.match_id) > 2**53 - 1:
        raise ValueError("Expected a canonical positive match ID (JS safe integer)")
    dest = local_path(args.output)
    source = local_path(args.dem) if args.dem is not None else None
    marker = local_path(args.cancel_file) if args.cancel_file else None
    if dest.suffix.lower() != ".json" or dest.exists() or not dest.parent.is_dir():
        raise ValueError("Output must be a new .json in an existing directory")
    if marker == dest or (source is not None and marker == source):
        raise ValueError("Cancellation marker must have a separate path")
    cancellation(marker)
    if source is not None and (not source.name.lower().endswith((".dem", ".dem.bz2")) or not source.is_file()
                               or not stat.S_ISREG(source.stat().st_mode)):
        raise ValueError("Expected an existing local .dem or .dem.bz2 file")
    if os.name == "nt":
        import winlimits
        winlimits.enter_job()
        signal.signal(signal.SIGBREAK, signal.default_int_handler)
    with tempfile.TemporaryDirectory(prefix=".postgame-", dir=dest.parent) as temp:
        verified = Path(temp) / "verified"
        with tempfile.TemporaryDirectory(dir=temp) as scratch:
            root = Path(scratch)
            if args.download:
                runtime = Path(sys.executable).parent
                node = (runtime.parent / "node" / "node.exe" if (runtime / "python313._pth").exists()
                        else shutil.which("node"))
                if not node or not Path(node).is_file():
                    raise ValueError("Download requires the staged Node runtime or Node 24 on PATH in source mode")
                run_child([node, HERE / "acquire.mts", args.match_id, root], root, "download", 335, marker, 3)
            run_child([sys.executable, "-I", "-B", HERE / "worker.py", args.match_id, root,
                       *([source] if source is not None else [])], root, "extract", 900, marker, 4)
            (root / "timeline.partial").rename(verified)
        # Raw metadata, archive and DEM are gone before any visible commit or success notification.
        cancellation(marker)
        publish(verified, dest)
    report("success", "published", matchId=args.match_id, bytes=dest.stat().st_size)
    return 0


def entry():
    try:
        return main()
    except KeyboardInterrupt:
        report("error", "cancelled", message="Cancelled; no timeline published")
        return 130
    except ChildFailed as exc:
        return exc.code
    except (OSError, ValueError) as exc:
        report("error", "cli", message=str(exc)[:240])
        return 5 if isinstance(exc, OSError) else 2


if __name__ == "__main__":
    sys.exit(entry())
