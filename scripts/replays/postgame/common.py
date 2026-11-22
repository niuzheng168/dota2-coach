import json
import sys

MAX_OUTPUT = 32 * 1024**2


def report(kind, stage, **fields):
    print(json.dumps({"type": kind, "stage": stage, **fields}, ensure_ascii=True, allow_nan=False),
          file=sys.stderr, flush=True)


def progress(stage, **fields):
    report("progress", stage, **fields)
