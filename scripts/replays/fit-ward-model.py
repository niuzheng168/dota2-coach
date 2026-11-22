"""Fit the spot weights of the coach's ward guesses (WardModel in desktop/DotaCoach.TwoPc.Core/WardGuesses.cs).

Input: the features WardEval writes for every guess of replayed games, each spot's features per part of the guess window
(a stretch in sight, a gap out of sight) and which spots lie within 400 of a ward really placed in that window:

    dotnet run --project desktop/DotaCoach.TwoPc.Tests -c Release -- --ward-eval --spots HELD-OUT-SPOTS.json \
        --features .tmp/ward-corpus/features.jsonl .tmp/ward-corpus/*.wards.json
    .tmp/vision-tools/Scripts/python.exe -I scripts/replays/fit-ward-model.py .tmp/ward-corpus/features.jsonl

Model, as WardModel.Likelihood computes it: within a part each spot scores w·f (stretch or gap weights), its share of
the part is the softmax over the spots that fit; parts weigh softmax(ln seconds + gap × (base + length × ln(1 + reach))).
The fit maximises the log of the share given to the spots near the real ward (L2 0.001 on all but the prior weight),
by L-BFGS, over every guess with a real ward in its window. Reports five-fold cross-validation by match against the
weights now in WardGuesses.cs: how often the likeliest spot is within 600 of the real ward, for guesses not anchored on
the hero (an anchored guess's first mark does not use the weights, only its second and third). Prints the C# lines for
the fitted weights. Offline only; nothing here runs in the coach.
"""
from __future__ import annotations
import argparse
import json
import math
import re
from pathlib import Path

import numpy as np
import torch

SOURCE = Path(__file__).resolve().parents[2] / "desktop/DotaCoach.TwoPc.Core/WardGuesses.cs"


def current_weights():
    text = SOURCE.read_text(encoding="utf-8")
    grab = lambda name: [float(v) for v in re.search(rf"{name} = \[([^\]]*)\]", text).group(1).split(",")]
    gap = re.search(r"GapBase = ([-\d.]+), GapLength = ([-\d.]+)", text)
    names = lambda name: re.findall(r'"(\w+)"', re.search(rf"{name} = \[([^\]]*)\]", text).group(1))
    return dict(stretch=grab("StretchWeights"), gap=grab("GapWeights"), beta=[float(gap.group(1)), float(gap.group(2))],
                stretch_names=names("StretchFeatures"), gap_names=names("GapFeatures"))


def load(path: Path):
    guesses = []
    for line in path.open(encoding="utf-8"):
        g = json.loads(line)
        spots = np.array(g["spots"], float).reshape(-1, 2)
        if not g["parts"] or not len(spots):
            continue
        truth = np.array(g["truth"], float).reshape(-1, 2)
        near = np.zeros(len(spots), bool)
        for x, y in truth:
            near |= np.hypot(spots[:, 0] - x, spots[:, 1] - y) <= 400
        g["spots"], g["truth"], g["near"] = spots, truth, near
        guesses.append(g)
    return guesses


def tensors(guesses, ns, ng):
    rows = [(i, p) for i, g in enumerate(guesses) for p in g["parts"]]
    n = max(len(g["spots"]) for g in guesses)
    fs, fg = torch.zeros(len(rows), n, ns), torch.zeros(len(rows), n, ng)
    ok = torch.zeros(len(rows), n, dtype=torch.bool)
    for r, (_, p) in enumerate(rows):
        for j, f in enumerate(p["f"]):
            if f is not None:
                ok[r, j] = True
                (fg if p["Gap"] else fs)[r, j] = torch.tensor(f)
    near = torch.zeros(len(guesses), n, dtype=torch.bool)
    for i, g in enumerate(guesses):
        near[i, :len(g["near"])] = torch.from_numpy(g["near"])
    return dict(fs=fs, fg=fg, ok=ok, near=near, G=len(guesses), index=torch.tensor([i for i, _ in rows]),
                gap=torch.tensor([p["Gap"] for _, p in rows]), seconds=torch.tensor([p["Seconds"] for _, p in rows]),
                reach=torch.tensor([p["Reach"] for _, p in rows]))


def shares(t, ws, wg, beta):
    score = torch.where(t["gap"][:, None], t["fg"] @ wg, t["fs"] @ ws).masked_fill(~t["ok"], -math.inf)
    fits = t["ok"].any(1)
    p = torch.softmax(score.masked_fill(~fits[:, None], 0), 1) * fits[:, None]
    logit = torch.log(t["seconds"].clamp(min=0.05)) + t["gap"] * (beta[0] + beta[1] * torch.log1p(t["reach"]))
    logit = logit.masked_fill(~fits, -1e9)
    top = torch.full((t["G"],), -1e9).scatter_reduce(0, t["index"], logit, "amax")
    w = torch.exp(logit - top[t["index"]])
    w = w / torch.zeros(t["G"]).scatter_add(0, t["index"], w)[t["index"]]
    return torch.zeros(t["G"], p.shape[1]).index_add(0, t["index"], w[:, None] * p)


def fit(t, start, l2=1e-3):
    ws = torch.tensor(start["stretch"], requires_grad=True)
    wg = torch.tensor(start["gap"], requires_grad=True)
    beta = torch.tensor(start["beta"], requires_grad=True)
    opt = torch.optim.LBFGS([ws, wg, beta], max_iter=300, line_search_fn="strong_wolfe")
    has = t["near"].any(1)

    def closure():
        opt.zero_grad()
        p = (shares(t, ws, wg, beta) * t["near"]).sum(1)[has].clamp(min=1e-9)
        loss = -torch.log(p).mean() + l2 * (ws[1:].pow(2).sum() + wg[1:].pow(2).sum() + beta.pow(2).sum())
        loss.backward()
        return loss
    opt.step(closure)
    return dict(stretch=ws.detach().tolist(), gap=wg.detach().tolist(), beta=beta.detach().tolist())


def likeliest_hits(guesses, t, w):
    with torch.no_grad():
        p = shares(t, torch.tensor(w["stretch"]), torch.tensor(w["gap"]), torch.tensor(w["beta"])).numpy()
    out = []
    for i, g in enumerate(guesses):
        k = int(np.argmax(p[i, :len(g["spots"])]))
        top = np.hypot(g["truth"][:, 0] - g["spots"][k, 0], g["truth"][:, 1] - g["spots"][k, 1]).min() if len(g["truth"]) else math.inf
        out.append(top <= 600 if p[i].sum() > 0 else False)
    return np.array(out)


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("features", type=Path)
    args = ap.parse_args()
    torch.set_default_dtype(torch.float64)
    now = current_weights()
    every = [g for g in load(args.features) if len(g["truth"])]
    guesses = [g for g in every if not g["anchored"]]
    ns, ng = len(now["stretch"]), len(now["gap"])
    matches = sorted({g["match"] for g in every})
    fold = {m: i % 5 for i, m in enumerate(matches)}
    hits_now, hits_fit = np.zeros(len(guesses), bool), np.zeros(len(guesses), bool)
    for k in range(5):
        train = [g for g in every if fold[g["match"]] != k]
        test = [i for i, g in enumerate(guesses) if fold[g["match"]] == k]
        w = fit(tensors(train, ns, ng), now)
        t = tensors([guesses[i] for i in test], ns, ng)
        hits_fit[test] = likeliest_hits([guesses[i] for i in test], t, w)
        hits_now[test] = likeliest_hits([guesses[i] for i in test], t, now)
    unseen = np.array([g["Unseen"] for g in guesses])
    print(f"{len(matches)} matches, {len(every)} guesses with a real ward, {len(guesses)} of them not anchored on the hero; "
          "likeliest spot within 600 for these:")
    for label, m in (("all", unseen >= 0), ("in sight", unseen == 0), ("unseen <15 s", (unseen > 0) & (unseen < 15)),
                     ("unseen 15-60 s", (unseen >= 15) & (unseen < 60)), ("unseen 60 s+", unseen >= 60)):
        if m.any():
            print(f"  {label:<15} n={m.sum():5d}  now {hits_now[m].mean():5.1%}  fitted (five-fold) {hits_fit[m].mean():5.1%}")
    w = fit(tensors(every, ns, ng), now)
    fmt = lambda v: ", ".join(f"{x:.2f}" for x in v)
    print("fitted on all:")
    print("  " + ", ".join(f"{n} {v:.2f}" for n, v in zip(now["stretch_names"], w["stretch"])))
    print("  " + ", ".join(f"{n} {v:.2f}" for n, v in zip(now["gap_names"], w["gap"])))
    print(f"    private static readonly double[] StretchWeights = [{fmt(w['stretch'])}];")
    print(f"    private static readonly double[] GapWeights = [{fmt(w['gap'])}];")
    print(f"    private const double GapBase = {w['beta'][0]:.2f}, GapLength = {w['beta'][1]:.2f};")


if __name__ == "__main__":
    main()
