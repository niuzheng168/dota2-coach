"""Python counterpart to src/replays/storage.ts. Importing it never creates directories."""
import os
from pathlib import Path
import tempfile


def replay_area(name="replays"):
    default = Path(r"Z:\dota2coach\data") if os.name == "nt" else Path(__file__).resolve().parents[2] / ".tmp"
    base = Path(os.environ.get("DOTA_COACH_REPLAY_WORK_ROOT", default))
    if not base.is_absolute():
        raise ValueError("DOTA_COACH_REPLAY_WORK_ROOT must be an absolute work directory")
    return base / name


def set_replay_temp(directory):
    scratch = Path(directory).absolute() / "tmp"
    scratch.mkdir(parents=True, exist_ok=True)  # no silent tempfile fallback to the system drive
    tempfile.tempdir = str(scratch)
    for key in ("TEMP", "TMP", "TMPDIR"):
        os.environ[key] = str(scratch)
