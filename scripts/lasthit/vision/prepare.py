"""Training samples for the creep-attack model from one last-hit lab recording (scripts/lasthit/lab.ts --frames DIR).

Reads TRACE.jsonl (engine truth), DIR/frames.jsonl (pictures and the bars the game client found) and DIR/labels.jsonl
(DotaCoach.TwoPc.Tests --vision-label: each picture's server time, each bar's unit, every unit's bar centre on screen).
Writes OUT/:
  crops.bin, crops.idx.npy   JPEG crops, two per creep per picture: "near" (NEAR px around the creep, for melee swings) and
                             "far" (FAR px, for ranged creeps and towers aiming at it), both SIZE x SIZE; idx = offset, length
  samples.npz                one sample per creep whose bar was found and matched in a picture: the crops of that creep in
                             this and earlier pictures (STEPS back), the damage creeps and towers land on it in each tenth of
                             a second over the next HORIZON s (the target), and facts for analysis (see SAMPLE_FIELDS)
Heights scale: NEAR and FAR are for a 1080-pixel-high picture. Hero hits are left out of the target: the player knows their
own, and the lab has no enemy hero.

    python prepare.py TRACE.jsonl DIR OUT [--near 256] [--far 768] [--size 128] [--steps 0,1,2,4] [--bins 10] [--horizon 1.0]
"""
import argparse, bisect, io, json, os
from multiprocessing import Pool

import numpy as np
from PIL import Image

SAMPLE_FIELDS = ["picture", "unit", "t", "round", "hp", "max", "kind", "team", "ally", "windup", "aim_x", "aim_y", "dies_in"]
KINDS = {"melee": 0, "ranged": 1, "flagbearer": 2, "siege": 3}


def unit_kind(name):
    for k in ("ranged", "flagbearer", "siege"):
        if k in name:
            return k
    return "melee"


def read_trace(path):
    units, towers, rounds, hits, swings, launches, deaths, states = {}, set(), [], {}, {}, {}, {}, []
    for line in open(path, encoding="utf-8"):
        r = json.loads(line)
        k = r["k"]
        if k == "unit":
            units[r["id"]] = (r["name"], r["team"])
        elif k == "tower":
            towers.add(r["id"])
        elif k == "round":
            rounds.append((r["t"], r["n"]))
        elif k == "dmg" and r["inflictor"] < 0:
            hits.setdefault(r["v"], []).append((r["t"], r["a"], r["d"]))
        elif k == "swing":
            swings.setdefault(r["a"], []).append((r["t"], r["v"]))
        elif k == "launch":
            launches.setdefault(r["a"], []).append((r["t"], r["v"]))
        elif k == "kill":
            deaths[r["v"]] = r["t"]
        elif k == "f":
            if not states or states[-1][0] != r["t"]:
                states.append((r["t"], {}))
            for c in r["c"]:
                states[-1][1][c[0]] = (c[1], c[2])
    for lst in list(hits.values()) + list(swings.values()) + list(launches.values()):
        lst.sort()
    return units, towers, rounds, hits, swings, launches, deaths, states


def crop_picture(job):
    """All crops of one picture: [(unit, near_jpeg, far_jpeg)]."""
    path, height, centres, near, far, size = job
    scale = height / 1080
    out = []
    with Image.open(path) as im:
        im = im.convert("RGB")
        for unit, x, y in centres:
            # The bar is above the creep: centre on the body.
            cx, cy = x, y + 40 * scale
            pair = []
            for side in (near, far):
                h = side * scale / 2
                patch = im.crop((round(cx - h), round(cy - h), round(cx + h), round(cy + h))).resize((size, size), Image.BILINEAR)
                buf = io.BytesIO()
                patch.save(buf, "JPEG", quality=90)
                pair.append(buf.getvalue())
            out.append((unit, pair[0], pair[1]))
    return out


def main():
    p = argparse.ArgumentParser()
    p.add_argument("trace"); p.add_argument("pictures"); p.add_argument("out")
    p.add_argument("--near", type=int, default=256); p.add_argument("--far", type=int, default=768)
    p.add_argument("--size", type=int, default=128); p.add_argument("--steps", default="0,1,2,4")
    p.add_argument("--bins", type=int, default=10); p.add_argument("--horizon", type=float, default=1.0)
    p.add_argument("--workers", type=int, default=os.cpu_count())
    a = p.parse_args()
    steps = [int(s) for s in a.steps.split(",")]
    os.makedirs(a.out, exist_ok=False)
    units, towers, rounds, hits, swings, launches, deaths, states = read_trace(a.trace)
    state_times = [s[0] for s in states]
    round_times = [r[0] for r in rounds]
    frames = {r["i"]: r for r in map(json.loads, open(os.path.join(a.pictures, "frames.jsonl"))) if "file" in r}
    labels = {r["i"]: r for r in map(json.loads, open(os.path.join(a.pictures, "labels.jsonl")))}
    order = sorted(i for i in labels if i in frames)

    # Crops of every creep on screen in every picture.
    jobs = []
    for i in order:
        f, lab = frames[i], labels[i]
        centres = [(u, x, y) for u, x, y in lab["units"] if u in units and 0 <= x < f["width"] and 0 <= y < f["height"]]
        jobs.append((os.path.join(a.pictures, f["file"]), f["height"], centres, a.near, a.far, a.size))
    crop_of = {}  # (picture, unit) -> crop number
    index = []
    with open(os.path.join(a.out, "crops.bin"), "wb") as blob, Pool(a.workers) as pool:
        for i, crops in zip(order, pool.imap(crop_picture, jobs, chunksize=8)):
            for unit, near, far in crops:
                crop_of[(i, unit)] = len(index)
                index.append((blob.tell(), len(near), len(far)))
                blob.write(near); blob.write(far)
            if len(index) and i % 2000 == 0:
                print(f"picture {i}: {len(index)} crops", flush=True)
    np.save(os.path.join(a.out, "crops.idx.npy"), np.array(index, dtype=np.int64))

    # Samples: creeps whose bar was found and matched.
    position = {i: {u: (x, y) for u, x, y in labels[i]["units"]} for i in order}
    stacks, targets, facts = [], [], []
    bin_width = a.horizon / a.bins
    for n, i in enumerate(order):
        lab = labels[i]
        t = lab["t"]
        state = states[max(0, bisect.bisect_right(state_times, t) - 1)][1]
        rnd = rounds[max(0, bisect.bisect_right(round_times, t) - 1)][1] if rounds else 0
        for bar, u in zip(frames[i]["bars"], lab["bars"]):
            if u < 0 or (i, u) not in crop_of or u not in state:
                continue
            stack = []
            for s in steps:
                j = order[max(0, n - s)]
                stack.append(crop_of.get((j, u), stack[-1] if stack else crop_of[(i, u)]))
            target = np.zeros(a.bins, np.float32)
            for ht, attacker, d in hits.get(u, [])[bisect.bisect_right(hits.get(u, []), (t, 1 << 30, 0)):]:
                if ht > t + a.horizon:
                    break
                if attacker in units or attacker in towers:
                    target[min(a.bins - 1, int((ht - t) / bin_width))] += d
            hp, mx = state[u]
            name, team = units[u]
            # The creep's own swing under way: seconds since it began, and where its target stands relative to it.
            windup, aim = -1.0, (0.0, 0.0)
            mine = swings.get(u, [])
            k = bisect.bisect_right(mine, (t, 1 << 30)) - 1
            if k >= 0:
                start, target_unit = mine[k]
                launched = any(start < lt <= t for lt, _ in launches.get(u, []))
                if not launched and t - start < 1.5:
                    windup = t - start
                    if target_unit in position[i] and u in position[i]:
                        tx, ty = position[i][target_unit]; ux, uy = position[i][u]
                        aim = ((tx - ux) * 1080 / frames[i]["height"], (ty - uy) * 1080 / frames[i]["height"])
            stacks.append(stack)
            targets.append(target)
            facts.append([i, u, t, rnd, hp, mx, KINDS[unit_kind(name)], team, bar[4], windup, aim[0], aim[1],
                          deaths.get(u, 1e9) - t])
    np.savez_compressed(os.path.join(a.out, "samples.npz"), stacks=np.array(stacks, np.int32), targets=np.array(targets, np.float32),
                        facts=np.array(facts, np.float64), fields=np.array(SAMPLE_FIELDS), steps=np.array(steps),
                        spec=np.array([a.near, a.far, a.size, a.bins, a.horizon]))
    print(json.dumps({"pictures": len(order), "crops": len(index), "samples": len(stacks),
                      "damage_per_sample": float(np.mean([x.sum() for x in targets])) if targets else 0}))


if __name__ == "__main__":
    main()
