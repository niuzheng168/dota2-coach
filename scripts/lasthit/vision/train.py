"""Trains the creep-attack model on samples from prepare.py and exports it to ONNX.

For one creep in one picture the model sees its "near" and "far" crops in this and earlier pictures (stacked as channels)
and predicts the damage creeps and towers land on it in each tenth of a second over the next second. Auxiliary heads learn
the creep's own swing (under way or not, how far along, where its target stands), which is what the pictures show most
directly. Rounds whose number is a multiple of 5 are held out for validation; --test DIR holds out whole recordings.

    python train.py OUT DATA_DIR [DATA_DIR ...] [--test DATA_DIR ...] [--epochs 12] [--batch 512] [--workers 16]
--limit N: a quick check of the script on a few samples (any machine).
Writes OUT/model.pt (best validation), OUT/model.onnx, OUT/log.jsonl, OUT/predictions.npz (validation and test samples).
"""
import argparse, io, json, mmap, os, time

import numpy as np
import torch
import torch.nn as nn
import torch.nn.functional as F
from PIL import Image


class Recording:
    """One prepare.py output: crops read straight from crops.bin."""

    def __init__(self, path):
        s = np.load(os.path.join(path, "samples.npz"))
        self.path = path
        self.stacks, self.targets, self.facts = s["stacks"], s["targets"], s["facts"]
        self.fields = [str(f) for f in s["fields"]]
        self.size = int(s["spec"][2])
        self.index = np.load(os.path.join(path, "crops.idx.npy"))
        self.blob = None

    def field(self, name):
        return self.facts[:, self.fields.index(name)]

    def crops(self, k):
        """(near, far) uint8 arrays [steps * 3, size, size] for sample k."""
        if self.blob is None:  # opened lazily in each worker process
            f = open(os.path.join(self.path, "crops.bin"), "rb")
            self.blob = mmap.mmap(f.fileno(), 0, access=mmap.ACCESS_READ)
        near, far = [], []
        for c in self.stacks[k]:
            off, n1, n2 = self.index[c]
            near.append(np.asarray(Image.open(io.BytesIO(self.blob[off:off + n1])).convert("RGB")))
            far.append(np.asarray(Image.open(io.BytesIO(self.blob[off + n1:off + n1 + n2])).convert("RGB")))
        return np.concatenate(near, 2).transpose(2, 0, 1), np.concatenate(far, 2).transpose(2, 0, 1)


class Samples(torch.utils.data.Dataset):
    def __init__(self, recordings, picks):
        self.recordings, self.picks = recordings, picks  # picks: [(recording, sample)]

    def __len__(self):
        return len(self.picks)

    def __getitem__(self, n):
        r, k = self.picks[n]
        rec = self.recordings[r]
        near, far = rec.crops(k)
        windup = rec.facts[k, rec.fields.index("windup")]
        aim = rec.facts[k, [rec.fields.index("aim_x"), rec.fields.index("aim_y")]]
        aux = np.array([windup >= 0, max(windup, 0) / .8, aim[0] / 200, aim[1] / 200], np.float32)
        return torch.from_numpy(near), torch.from_numpy(far), torch.from_numpy(rec.targets[k]), torch.from_numpy(aux), n


def stem(channels):
    layers, width = [], channels
    for out in (32, 64, 96, 128, 160):  # 128 -> 4
        layers += [nn.Conv2d(width, out, 3, 2, 1, bias=False), nn.BatchNorm2d(out), nn.ReLU(inplace=True),
                   nn.Conv2d(out, out, 3, 1, 1, bias=False), nn.BatchNorm2d(out), nn.ReLU(inplace=True)]
        width = out
    return nn.Sequential(*layers, nn.Flatten())


class CreepAttackNet(nn.Module):
    """uint8 near/far stacks in, damage per tenth of a second (HP) and the auxiliary swing outputs out."""

    def __init__(self, steps, bins, size):
        super().__init__()
        self.near, self.far = stem(3 * steps), stem(3 * steps)
        flat = 160 * (size // 32) ** 2
        self.trunk = nn.Sequential(nn.Linear(2 * flat, 512), nn.ReLU(inplace=True), nn.Dropout(.1), nn.Linear(512, 256), nn.ReLU(inplace=True))
        self.damage = nn.Linear(256, bins)
        self.aux = nn.Linear(256, 4)

    def forward(self, near, far):
        x = torch.cat([self.near(near.float() / 255 - .5), self.far(far.float() / 255 - .5)], 1)
        h = self.trunk(x)
        return F.softplus(self.damage(h)) * 20, self.aux(h)


class Exported(nn.Module):
    """The ONNX graph: damage only."""

    def __init__(self, net):
        super().__init__()
        self.net = net

    def forward(self, near, far):
        return self.net(near, far)[0]


def losses(pred, aux_pred, target, aux):
    damage = F.smooth_l1_loss(pred / 20, target / 20)
    swing = F.binary_cross_entropy_with_logits(aux_pred[:, 0], aux[:, 0])
    on = aux[:, 0] > 0
    detail = F.smooth_l1_loss(aux_pred[on, 1:], aux[on, 1:]) if on.any() else aux_pred.sum() * 0
    return damage + .2 * swing + .2 * detail, damage


def evaluate(net, loader, device, mean):
    """MAE of the damage landed by .3/.5/.8/1 s, against predicting the training mean."""
    net.eval()
    cut = [2, 4, 7, 9]
    err, base, total, preds, ids = np.zeros(4), np.zeros(4), 0, [], []
    with torch.no_grad(), torch.autocast(device.type, dtype=torch.bfloat16, enabled=device.type == "cuda"):
        for near, far, target, aux, n in loader:
            pred = net(near.to(device, non_blocking=True), far.to(device, non_blocking=True))[0].float().cpu()
            cp, ct = pred.cumsum(1)[:, cut], target.cumsum(1)[:, cut]
            err += (cp - ct).abs().sum(0).numpy(); base += (mean.cumsum(0)[cut] - ct).abs().sum(0).numpy(); total += len(target)
            preds.append(pred.numpy()); ids.append(n.numpy())
    net.train()
    return err / max(1, total), base / max(1, total), np.concatenate(preds) if preds else None, np.concatenate(ids) if ids else None


def main():
    p = argparse.ArgumentParser()
    p.add_argument("out"); p.add_argument("data", nargs="+"); p.add_argument("--test", nargs="*", default=[])
    p.add_argument("--epochs", type=int, default=12); p.add_argument("--batch", type=int, default=512)
    p.add_argument("--lr", type=float, default=2e-3); p.add_argument("--workers", type=int, default=16)
    p.add_argument("--limit", type=int, default=0, help="at most this many samples per split (a quick check of the script)")
    a = p.parse_args()
    os.makedirs(a.out, exist_ok=True)
    recordings = [Recording(d) for d in a.data + a.test]
    steps, bins = len(recordings[0].stacks[0]), recordings[0].targets.shape[1]
    train, val, test = [], [], []
    for r, rec in enumerate(recordings):
        held = rec.field("round") % 5 == 0
        for k in range(len(rec.targets)):
            (test if r >= len(a.data) else val if held[k] else train).append((r, k))
    if not val:  # fewer than five rounds: the last one
        last = max(recordings[r].field("round")[k] for r, k in train)
        val = [(r, k) for r, k in train if recordings[r].field("round")[k] == last]
        train = [(r, k) for r, k in train if recordings[r].field("round")[k] != last]
    if a.limit:
        rng = np.random.default_rng(0)
        train, val, test = ([s[i] for i in sorted(rng.permutation(len(s))[:a.limit])] for s in (train, val, test))
    print(json.dumps({"train": len(train), "val": len(val), "test": len(test), "steps": steps, "bins": bins}), flush=True)
    device = torch.device("cuda" if torch.cuda.is_available() else "cpu")
    net = CreepAttackNet(steps, bins, recordings[0].size).to(device).to(memory_format=torch.channels_last)
    mean = torch.tensor(np.mean([recordings[r].targets[k] for r, k in train], 0))
    load = lambda picks, shuffle: torch.utils.data.DataLoader(Samples(recordings, picks), batch_size=a.batch, shuffle=shuffle,
                                                              num_workers=a.workers, pin_memory=True, persistent_workers=a.workers > 0, drop_last=shuffle)
    train_loader, val_loader = load(train, True), load(val, False)
    test_loader = load(test, False) if test else None
    opt = torch.optim.AdamW(net.parameters(), lr=a.lr, weight_decay=1e-4)
    sched = torch.optim.lr_scheduler.OneCycleLR(opt, a.lr, total_steps=a.epochs * len(train_loader))
    best, log = float("inf"), open(os.path.join(a.out, "log.jsonl"), "a")
    for epoch in range(a.epochs):
        started, running, seen = time.time(), 0.0, 0
        for near, far, target, aux, _ in train_loader:
            near, far = near.to(device, non_blocking=True), far.to(device, non_blocking=True)
            target, aux = target.to(device, non_blocking=True), aux.to(device, non_blocking=True)
            with torch.autocast(device.type, dtype=torch.bfloat16, enabled=device.type == "cuda"):
                pred, aux_pred = net(near, far)
            loss, damage = losses(pred.float(), aux_pred.float(), target, aux)
            opt.zero_grad(set_to_none=True); loss.backward(); opt.step(); sched.step()
            running += damage.item() * len(target); seen += len(target)
        err, base, _, _ = evaluate(net, val_loader, device, mean)
        row = {"epoch": epoch, "train_damage_loss": running / max(1, seen), "val_mae_by_.3/.5/.8/1s": err.round(2).tolist(),
               "mean_baseline": base.round(2).tolist(), "seconds": round(time.time() - started)}
        print(json.dumps(row), flush=True); log.write(json.dumps(row) + "\n"); log.flush()
        if err[-1] < best:
            best = err[-1]
            torch.save({"state": net.state_dict(), "steps": steps, "bins": bins, "size": recordings[0].size}, os.path.join(a.out, "model.pt"))
    net.load_state_dict(torch.load(os.path.join(a.out, "model.pt"))["state"])
    out = {}
    for name, loader, picks in (("val", val_loader, val), ("test", test_loader, test)):
        if loader is None or not picks:
            continue
        err, base, preds, ids = evaluate(net, loader, device, mean)
        print(json.dumps({name: {"mae_by_.3/.5/.8/1s": err.round(2).tolist(), "mean_baseline": base.round(2).tolist()}}), flush=True)
        out[name + "_pred"] = preds
        out[name + "_who"] = np.array([[picks[i][0], picks[i][1]] for i in ids])
    np.savez_compressed(os.path.join(a.out, "predictions.npz"), recordings=np.array([r.path for r in recordings]), **out)
    size = recordings[0].size
    dummy = torch.zeros(1, 3 * steps, size, size, dtype=torch.uint8, device=device)
    torch.onnx.export(Exported(net.eval()), (dummy, dummy), os.path.join(a.out, "model.onnx"), input_names=["near", "far"], output_names=["damage"],
                      dynamic_axes={"near": {0: "n"}, "far": {0: "n"}, "damage": {0: "n"}}, opset_version=17, external_data=False)
    print("exported", os.path.join(a.out, "model.onnx"))


if __name__ == "__main__":
    main()
