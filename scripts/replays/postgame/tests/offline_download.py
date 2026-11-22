"""Run the real --download workflow, replacing only the external HTTP transport for the test."""
from pathlib import Path
import os
import sys

HERE = Path(__file__).resolve().parent
exe = os.environ.get("POSTGAME_TEST_EXE")
sys.path.insert(0, str(Path(exe).resolve().parent / "scripts/replays/postgame" if exe else HERE.parent))
import cli

real_child = cli.run_child


def cached_child(command, root, stage, *args):
    if stage == "download":
        command = [command[0], "--import", (HERE / "cache_transport.mts").as_uri(), *command[1:]]
    return real_child(command, root, stage, *args)


if __name__ == "__main__":
    cli.run_child = cached_child
    sys.exit(cli.entry())
