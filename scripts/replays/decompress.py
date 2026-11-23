"""Research CLI; the same bounded decompressor is used by the isolated desktop worker."""
import argparse
import json
from pathlib import Path
import sys

if __name__ == "__main__":
    sys.dont_write_bytecode = True
from postgame.pinned_v2.decompress import decompress
from replay_storage import replay_area, set_replay_temp

if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("match_id", type=int)
    parser.add_argument("--root", type=Path)
    args = parser.parse_args()
    root = args.root or replay_area()
    set_replay_temp(root)
    print(json.dumps(decompress(root, args.match_id), indent=2))
