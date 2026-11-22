"""Offline recognizer for the Dota 2 bottom HUD: level, attributes, items, health and mana.

It reads only what the frame shows: the HUD of whichever hero the viewer has selected.
Geometry was measured on the default HUD at 1920x1080; other sizes are scaled to a
1080-pixel height first (validated at 1080p only). Heroes with more ability slots widen
the centre panel, so the left block and the item panel are located independently.

References come from local files only: item icons from extract-assets.ts and the static
HUD templates written by `calibrate` from frames captured with capture_replay.py.
Everything runs locally: item matching in PyTorch and the digit OCR (RapidOCR's PP-OCRv3
line recognizer) in ONNX Runtime, both on CUDA when available. Health and mana readings are
checked against the drawn bar fill, so a misread slash yields null rather than a wrong value.
"""
from __future__ import annotations
import argparse
import difflib
import json
import re
import sys
from pathlib import Path

import cv2
import numpy as np
import torch  # imported before onnxruntime so its CUDA runtime DLLs serve both

REF_W, REF_H = 1920, 1080
HUD_TOP = 900  # nothing above this row is read
SEARCH = 160  # px either way; the widest observed shift is 56
TREE = (716, 946, 770, 998)  # talent-tree button: left-block anchor
PANEL = (1120, 930, 1410, 1080)  # item panel frame: right-block anchor
LEVEL = (510, 1048, 532, 1065)  # inside the level ring, which otherwise reads as brackets
NAME = (530, 912, 715, 937)  # unit name above the portrait, drawn over the game world; moves with the left block
# Attribute values flow right to left from a fixed end, each followed by its coloured icon, so the
# digits are found between the icons. Hue ranges (OpenCV 0-180) of the three icons:
ATTRIBUTE_ROW = (1061, 1079)
ATTRIBUTE_SPAN = (558, 712)  # where the icons are searched, right of the magnifier
MAGNIFIER = 546  # left edge of the portrait magnifier, the leftmost column a value can reach
ATTRIBUTE_HUES = {"strength": ((0, 12), (168, 180)), "agility": ((40, 85),), "intelligence": ((88, 110),)}
BAR_ENDS = (714, 1125)  # the left end moves with the left block, the right end with the panel
# (health top, health bottom, mana top, mana bottom). A barrier adds a thin bar above health and
# both bars shrink to make room.
BAR_ROWS = {"plain": (1018, 1043, 1046, 1071), "barrier": (1030, 1049, 1052, 1071)}
BAR_TEXT = 80  # half width around the bar centre: "10000 / 10000" fits, regeneration text stays out
FILL_MATCH = 0.02  # largest gap between a read value pair and the drawn bar fill
SLOTS = {**{f"slot{i}": (1172 + 66 * (i % 3) - (i % 3 == 2), 965 + 48 * (i // 3)) for i in range(6)},
         **{f"slot{6 + i}": (1171 + 66 * i - (i == 2), 1055) for i in range(3)},
         "neutral0": (1363, 998), "teleport0": (1363, 1047)}  # slot centres
# kind -> views: drawn icon width, compared window w x h around the centre, patches across x down,
# compare edges instead of grey levels. Icons keep their aspect and the slot clips them, so
# references are centre crops. Items on cooldown are drawn smaller (54 px instead of 62), hence
# the second main view; the backpack draws icons grey and soft, where only their outlines match.
VIEWS = {"main": [(62, 62, 40, (4, 4), False), (54, 52, 38, (4, 4), False)],
         "backpack": [(52, 48, 28, (4, 2), True)],
         "neutral": [(58, 28, 28, (3, 3), False), (52, 26, 26, (3, 3), False)],
         "teleport": [(44, 28, 28, (3, 3), False)]}
KEEP = 0.75  # best share of patches that counts: digits and cooldown sweeps cover the rest
JITTER = 2
SAME_ICON = 0.9  # references this alike cannot be told apart; they form one group
MIN_SCORE = 0.3
# Event-only items left out of the references: the offering envelope (cast on a friendly building,
# 25 icon variants) was never in any recorded match's GSI, but its variants matched dim backpack
# icons and recipe scrolls (Point Booster, Famango, recipes in replays and live games).
EVENT_ITEMS = {"item_ofrenda"}
EMPTY_MAD = 10.0
# Whose HUD it is comes from the first ability icons: the name label is replaced by the death summary
# while the hero is dead and depends on the client language. Four slots draw 49 px icons 65 px apart;
# five or more draw 46 px icons 58 px apart. (icon size, first icon's left edge, pitch):
ABILITY_LAYOUTS = ((49, 828, 65), (46, 826, 58))
ABILITY_TOP = 948
# Four icons, scored by the best two: cooldown numbers cover some, and with three a dead hero whose
# abilities were mostly on cooldown went unnamed. Slots further right change with Scepter and Shard.
HERO_SLOTS = 4
HERO_SCORE = 0.5  # best-two-slot mean a hero needs to be named
# Some cosmetic spell icons are still blank boxes in the first frames after a switch (seen live for
# Juggernaut and Pudge): median interquartile range 6-17 there, at least 29.5 in every dataset frame.
BLANK_SPREAD = 20
HERO_MARGIN = 0.08  # over the next hero
# Stack counts are drawn bottom right in one font: white in the main slots, grey in the backpack. A ward dispenser draws
# the ward in front (its count bottom right) over the other kind (its count smaller and grey, bottom left). Templates
# come from GSI-labelled frames (`digits`); a digit is searched for by where its bottom-right corner may sit around the
# slot centre: ((x from, to), (y from, to)).
COUNT_SEARCH = {"main": ((22, 29), (15, 20)), "backpack": ((18, 26), (7, 12))}
BEHIND_SEARCH = {"main": ((-26, -18), (15, 20)), "backpack": ((-27, -16), (5, 13))}  # backpack: 3 labelled samples only
BEHIND_SCALE = 0.85
DIGIT_PATCH = (14, 11)  # rows, columns: the glyph and its outline, anchored bottom right
DIGIT_SCORE, DIGIT_MARGIN = 0.7, 0.05
# Items whose stack counts the digit templates are learnt from (bottles and dispensers draw theirs differently).
COUNTED = {"item_ward_observer", "item_ward_sentry", "item_tango", "item_clarity", "item_flask", "item_smoke_of_deceit", "item_dust",
           "item_enchanted_mango", "item_famango", "item_great_famango", "item_blood_grenade", "item_magic_stick", "item_magic_wand",
           "item_infused_raindrop", "item_urn_of_shadows", "item_spirit_vessel"}
WARD_ITEMS = ("item_ward_observer", "item_ward_sentry", "item_ward_dispenser")
# The front ward's colour, measured over the icon's upper right ((x from, to), (y from, to)) around the slot centre:
# observers are yellow, sentries blue. Backpack icons and items on cooldown are grey: their kind stays unknown.
WARD_TINT = ((2, 26), (-18, 6))
DIGITS_FILE = "hud-digits-1080.npz"


def text_reader(cuda: bool):
    """RapidOCR's PP-OCRv3 line recognizer on its own (the crops are already lines), on CUDA if asked."""
    import onnxruntime
    import rapidocr_onnxruntime
    from rapidocr_onnxruntime.ch_ppocr_v3_rec.text_recognize import TextRecognizer
    onnxruntime.set_default_logger_severity(3)  # a harmless plugin-EP probe warns on every start
    from rapidocr_onnxruntime.utils import read_yaml
    root = Path(rapidocr_onnxruntime.__file__).parent
    config = read_yaml(str(root / "config.yaml"))["Rec"]
    config.update(model_path=str(root / config["model_path"]), use_cuda=cuda)
    return TextRecognizer(config)


def kind(slot: str) -> str:
    if slot.startswith("slot"):
        return "main" if int(slot[4:]) < 6 else "backpack"
    return slot.rstrip("0123456789")


def chroma(bgr: np.ndarray) -> np.ndarray:
    """Mean colour of the central half of an icon, as shares of B, G and R."""
    h, w = bgr.shape[:2]
    mean = bgr[h // 4:h - h // 4, w // 4:w - w // 4].reshape(-1, 3).mean(0) + 1
    return mean / mean.sum()


def gradient(gray: np.ndarray) -> np.ndarray:
    g = cv2.GaussianBlur(gray, (3, 3), 0)
    return cv2.magnitude(cv2.Sobel(g, cv2.CV_32F, 1, 0), cv2.Sobel(g, cv2.CV_32F, 0, 1))


def normalize(image: np.ndarray) -> tuple[np.ndarray, int]:
    """Scale to 1080 rows; returns the frame and the x offset of the 1920-wide reference layout."""
    h, w = image.shape[:2]
    if h != REF_H:
        image = cv2.resize(image, (round(w * REF_H / h), REF_H), interpolation=cv2.INTER_AREA if h > REF_H else cv2.INTER_CUBIC)
    return image, (image.shape[1] - REF_W) // 2


def box(b, dx=0):
    x0, y0, x1, y1 = b
    return slice(y0, y1), slice(x0 + dx, x1 + dx)


def window(slot: str, dx: int, dy: int = 0, view: int = 0):
    cx, cy = SLOTS[slot]
    _, w, h, *_ = VIEWS[kind(slot)][view]
    return cx + dx - w // 2, cy + dy - h // 2, w, h


def patches(images: torch.Tensor, grid: tuple[int, int]) -> torch.Tensor:
    """(N, H, W) -> (patches, N, pixels), each patch zero-mean and unit-norm."""
    gx, gy = grid
    ph, pw = images.shape[-2] // gy, images.shape[-1] // gx
    x = images[:, :gy * ph, :gx * pw].float()
    x = x.reshape(-1, gy, ph, gx, pw).permute(1, 3, 0, 2, 4).reshape(gy * gx, -1, ph * pw)
    x = x - x.mean(-1, keepdim=True)
    return x / (x.norm(dim=-1, keepdim=True) + 1e-3)


class ItemReferences:
    """Item icons as patch features on the GPU; one batched comparison per slot kind and view."""

    def __init__(self, assets: Path, manifest: dict, device: torch.device):
        self.device = device
        self.groups: dict[str, list[list[str]]] = {}
        self.features: dict[str, list[torch.Tensor]] = {}  # (patches, pixels, references)
        items = manifest["items"]
        allowed = {
            "main": lambda n, it: not it["neutral"] and not it["enhancement"],
            "neutral": lambda n, it: (it["neutral"] or it["formerNeutral"]) and not it["enhancement"],
            "teleport": lambda n, it: n == "item_tpscroll",
        }
        allowed["backpack"] = allowed["main"]
        for k, ok in allowed.items():
            icons, owners = [], []
            for name, it in items.items():
                if ok(name, it) and name not in EVENT_ITEMS:
                    for path in [it["icon"], *it["altIcons"]]:
                        icons.append(cv2.imread(str(assets / path), cv2.IMREAD_GRAYSCALE))
                        owners.append(name)
            self.features[k] = []
            for view, (icon_w, w, h, grid, edges) in enumerate(VIEWS[k]):
                refs = []
                for icon in icons:
                    # Odd-sized textures (98 px scrolls, 128 px squares) are stretched into the 88x64 frame.
                    scaled = cv2.resize(icon, (icon_w, round(icon_w * 64 / 88)), interpolation=cv2.INTER_AREA)
                    scaled = gradient(scaled) if edges else scaled
                    oy, ox = (scaled.shape[0] - h) // 2, (scaled.shape[1] - w) // 2
                    refs.append(scaled[oy:oy + h, ox:ox + w])
                feats = patches(torch.from_numpy(np.stack(refs)).to(device), grid)
                if view == 0:
                    # Items whose references look the same are merged; the label prefers what can be bought.
                    sim = torch.bmm(feats, feats.transpose(1, 2)).mean(0).cpu().numpy()
                self.features[k].append(feats.permute(0, 2, 1).contiguous())
            parent = list(range(len(owners)))

            def root(i):
                while parent[i] != i:
                    parent[i] = parent[parent[i]]
                    i = parent[i]
                return i
            for a, b in zip(*np.nonzero(np.triu(sim > SAME_ICON, 1))):
                parent[root(a)] = root(b)
            members: dict[int, set[str]] = {}
            for i, name in enumerate(owners):
                members.setdefault(root(i), set()).add(name)
            order = lambda n: (not items[n]["purchasable"], items[n]["recipe"], n)
            label = {r: sorted(m, key=order) for r, m in members.items()}
            self.groups[k] = [label[root(i)] for i in range(len(owners))]
        # Some icons differ only in colour (observer and sentry wards): the colour mix of each item's
        # own icon, independent of brightness, picks within such a group.
        self.tint = {name: chroma(cv2.imread(str(assets / it["icon"]))) for name, it in items.items()}
        self.buyable = {name for name, it in items.items() if it["purchasable"]}

    def by_tint(self, group: list[str], crop: np.ndarray) -> list[str]:
        # Only among items as buyable as the group's label: never trade Blink Dagger for an internal variant.
        pool = [n for n in group if (n in self.buyable) == (group[0] in self.buyable)]
        if len(pool) < 2:
            return group
        seen = chroma(crop)
        pool.sort(key=lambda n: float(np.abs(self.tint[n] - seen).sum()))
        return pool + [n for n in group if n not in pool]

    def classify(self, gray: np.ndarray, edges: np.ndarray, slots: list[str], dx: int, dy: int) -> dict[str, tuple[list[str], float]]:
        """Best reference per slot: the best KEEP share of patch NCCs, best offset and view."""
        offsets = [(oy, ox) for oy in range(-JITTER, JITTER + 1) for ox in range(-JITTER, JITTER + 1)]
        pending = []
        for k in VIEWS:
            group = [s for s in slots if kind(s) == k]
            if not group:
                continue
            best = None
            for view, (_, _, _, grid, use_edges) in enumerate(VIEWS[k]):
                image, crops = edges if use_edges else gray, []
                for slot in group:
                    x0, y0, w, h = window(slot, dx, dy, view)
                    crops += [image[y0 + oy:y0 + oy + h, x0 + ox:x0 + ox + w] for oy, ox in offsets]
                f = patches(torch.from_numpy(np.stack(crops)).to(self.device, non_blocking=True), grid)
                s = torch.bmm(f, self.features[k][view])  # (patches, crops, references)
                keep = max(1, round(KEEP * s.shape[0]))
                s = s.topk(keep, dim=0).values.mean(0).view(len(group), len(offsets), -1).amax(1)
                best = s if best is None else torch.maximum(best, s)
            pending.append((k, group, best.max(1)))
        out = {}
        for k, group, (score, index) in pending:  # one device sync per kind, after all work is queued
            for slot, s, i in zip(group, score.tolist(), index.tolist()):
                out[slot] = (self.groups[k][i], s)
        return out


class AbilityReferences:
    """Every hero's ability icons, cosmetic variants included, to tell whose HUD a frame shows."""

    def __init__(self, assets: Path, manifest: dict, device: torch.device):
        self.device = device
        root = assets / "decoded/panorama/images/spellicons"
        heroes = manifest["heroes"]
        short = {h.removeprefix("npc_dota_hero_"): h for h in heroes}
        ability = {a["name"]: h for h, info in heroes.items() for a in info["abilities"]}
        owners, icons = [], []
        for path in sorted(root.rglob("*_png.png")):
            stem, parts = path.name[:-len("_png.png")], path.relative_to(root).parts
            # Listed ability, else a cosmetic set folder named after the hero, else the hero-name prefix
            # that ability textures carry (mirana_leap_ti8, kez_switch_weapons_sai_off).
            owner = ability.get(stem) or (short.get(parts[0]) if len(parts) > 1 else None)
            if owner is None:
                prefixes = [s for s in short if stem.startswith(s + "_")]
                owner = short[max(prefixes, key=len)] if prefixes else None
            if owner:
                owners.append(owner)
                icons.append(cv2.imread(str(path), cv2.IMREAD_GRAYSCALE))
        self.heroes = sorted(set(owners))
        index = {h: i for i, h in enumerate(self.heroes)}
        self.owner = torch.tensor([index[o] for o in owners], device=device)
        self.features = [patches(torch.from_numpy(np.stack([cv2.resize(i, (size, size), interpolation=cv2.INTER_AREA)
                                                             for i in icons])).to(device), (4, 4)).permute(0, 2, 1).contiguous()
                         for size, _, _ in ABILITY_LAYOUTS]

    def identify(self, gray: np.ndarray, dl: int, candidates: set[str] | None = None) -> dict:
        """Best hero for the first ability icons, trying both slot layouts. With `candidates` (the
        heroes in this match) only those compete; a weak result falls back to every hero unless all
        ten are known, when a hero outside them can only be a misreading."""
        offsets = [(oy, ox) for oy in range(-JITTER, JITTER + 1) for ox in range(-JITTER, JITTER + 1)]
        spread = max(float(np.median([np.subtract(*np.percentile(
            gray[ABILITY_TOP + 4:ABILITY_TOP + size - 4, left + dl + pitch * i + 4:left + dl + pitch * i + size - 4], [75, 25]))
            for i in range(HERO_SLOTS)])) for size, left, pitch in ABILITY_LAYOUTS)
        if spread < BLANK_SPREAD:  # nothing to compare; near-flat crops would match some icon by chance
            return {"hero": None, "best": None, "score": 0.0, "margin": 0.0, "blank": True}
        best = None
        for (size, left, pitch), refs in zip(ABILITY_LAYOUTS, self.features):
            crops = [gray[ABILITY_TOP + oy:ABILITY_TOP + oy + size, left + dl + pitch * i + ox:left + dl + pitch * i + ox + size]
                     for i in range(HERO_SLOTS) for oy, ox in offsets]
            f = patches(torch.from_numpy(np.stack(crops)).to(self.device, non_blocking=True), (4, 4))
            s = torch.bmm(f, refs)  # (patches, crops, references)
            s = s.topk(max(1, round(KEEP * s.shape[0])), dim=0).values.mean(0).view(HERO_SLOTS, len(offsets), -1).amax(1)
            per_hero = torch.full((HERO_SLOTS, len(self.heroes)), -1.0, device=self.device)
            per_hero = per_hero.scatter_reduce(1, self.owner.expand(HERO_SLOTS, -1), s, "amax")
            score = per_hero.topk(2, dim=0).values.mean(0)  # best two slots: one may be under a cooldown number
            best = score if best is None else torch.maximum(best, score)
        scores = best.tolist()

        def pick(names):
            ranked = sorted(((scores[i], h) for i, h in enumerate(self.heroes) if names is None or h in names), reverse=True)
            (top, hero), runner = ranked[0], (ranked[1][0] if len(ranked) > 1 else -1.0)
            return {"hero": hero if top >= HERO_SCORE and top - runner >= HERO_MARGIN else None,
                    "best": hero, "score": round(top, 3), "margin": round(top - runner, 3), "blank": False}
        out = pick(candidates) if candidates else None
        if out is None or out["hero"] is None and len(candidates) < 10:
            out = pick(None)
        return out


class HeroNames:
    """Simplified-Chinese hero names (the client language the manifest was extracted for) -> npc names."""

    def __init__(self, manifest: dict):
        self.npc = {h["nameZh"]: name for name, h in manifest["heroes"].items() if h.get("nameZh")}

    def match(self, text: str, candidates: set[str] | None = None) -> str | None:
        """Hero named by an OCR'd label. With `candidates` (this match's heroes) a misread character is
        tolerated: the one candidate sharing the most characters wins, if it shares at least half."""
        clean = re.sub(r"[\W_]+", "", text or "")
        if not clean:
            return None
        names = {n: h for n, h in self.npc.items() if candidates is None or h in candidates}
        if whole := self.whole(text, candidates):
            return whole
        if candidates is None:
            close = difflib.get_close_matches(clean, list(names), n=1, cutoff=0.75) if len(clean) >= 2 else []
            return names[close[0]] if close else None
        # Characters in common; a partial read shared alike by two names (幻影: PA and PL) names nobody.
        shared = sorted(((sum(min(clean.count(c), n.count(c)) for c in set(n)), n) for n in names), reverse=True)
        if shared and shared[0][0] >= len(shared[0][1]) / 2 and (len(shared) == 1 or shared[1][0] < shared[0][0]):
            return names[shared[0][1]]
        return None

    def whole(self, text: str, candidates: set[str] | None = None) -> str | None:
        """Hero whose whole name is in the label. The label sits on the game world, so OCR often adds
        a stray mark around it; the longest name wins (沉默术士 over 术士)."""
        clean = re.sub(r"[\W_]+", "", text or "")
        inside = [n for n, h in self.npc.items() if (candidates is None or h in candidates) and n in clean]
        return self.npc[max(inside, key=len)] if inside else None


class Digits:
    """Stack-count digits by masked correlation with templates: each template keeps the pixels that look the same in
    every sample (the glyph and its dark outline), so the item art behind does not count."""

    def __init__(self, templates: dict[str, tuple[np.ndarray, np.ndarray]]):
        self.templates = templates
        rows, cols = round(DIGIT_PATCH[0] * BEHIND_SCALE), round(DIGIT_PATCH[1] * BEHIND_SCALE)
        self.small = {d: (cv2.resize(t, (cols, rows), interpolation=cv2.INTER_AREA),
                          (cv2.resize(m, (cols, rows), interpolation=cv2.INTER_AREA) > 0.5).astype(np.float32))
                      for d, (t, m) in templates.items()}

    @classmethod
    def load(cls, path: Path) -> "Digits | None":
        if not path.exists():
            return None
        data = np.load(path)
        return cls({d: (data[f"t{d}"], data[f"m{d}"]) for d in "0123456789" if f"t{d}" in data})

    @staticmethod
    def score(patch: np.ndarray, template: np.ndarray, mask: np.ndarray) -> float:
        w = mask.sum()
        a = (patch - (patch * mask).sum() / w) * mask
        b = (template - (template * mask).sum() / w) * mask
        return float((a * b).sum() / (np.linalg.norm(a) * np.linalg.norm(b) + 1e-6))

    def best(self, value: np.ndarray, cx: int, cy: int, search, small=False) -> tuple[float, str, float]:
        """(score, digit, margin over the best other digit) for a glyph whose bottom-right corner is in `search`."""
        (x0, x1), (y0, y1) = search
        scores = {}
        for d, (t, m) in (self.small if small else self.templates).items():
            rows, cols = t.shape
            for yb in range(cy + y0, cy + y1):
                for xr in range(cx + x0, cx + x1):
                    patch = value[yb - rows + 1:yb + 1, xr - cols + 1:xr + 1]
                    if patch.shape == t.shape:
                        scores[d] = max(scores.get(d, -1.0), self.score(patch, t, m))
        if not scores:
            return -1.0, "", 0.0
        ranked = sorted(scores.items(), key=lambda kv: -kv[1])
        return ranked[0][1], ranked[0][0], ranked[0][1] - (ranked[1][1] if len(ranked) > 1 else -1.0)

    def read(self, value: np.ndarray, cx: int, cy: int, search, small=False) -> int | None:
        """A one-digit count, or None when unsure. A second digit left of it (10 or more) also gives None."""
        score, digit, margin = self.best(value, cx, cy, search, small)
        if score < DIGIT_SCORE or margin < DIGIT_MARGIN:
            return None
        cols = DIGIT_PATCH[1] - 3  # a glyph is at most 8 wide; a tens digit sits that far further left
        (x0, x1), ys = search
        if not small and self.best(value, cx, cy, ((x0 - cols - 2, x1 - cols + 1), ys))[0] >= DIGIT_SCORE + 0.1:
            return None
        return int(digit)


class HudRecognizer:
    def __init__(self, assets: Path, calibration: Path, device: str | None = None):
        self.device = torch.device(device or ("cuda" if torch.cuda.is_available() else "cpu"))
        self.manifest = json.loads((assets / "manifest.json").read_text(encoding="utf-8"))
        cal = np.load(calibration)
        self.tree = gradient(cal["tree"])
        self.panel, self.panel_mask = cal["panel"], cal["panel_mask"].copy()
        # Before the first neutral item the slot draws a large token-count ring ("0/6") over the panel
        # frame; the calibration frames all had neutral items, so that area is left out of the anchor.
        x, y = SLOTS["neutral0"]
        cv2.circle(self.panel_mask, (x - PANEL[0], y - 12 - PANEL[1]), 44, 0, -1)
        self.empty = {k: cal[f"empty_{k}"] for k in VIEWS if f"empty_{k}" in cal}
        self.items = ItemReferences(assets, self.manifest, self.device)
        self.abilities = AbilityReferences(assets, self.manifest, self.device)
        self.names = HeroNames(self.manifest)
        self.text_reader = text_reader(self.device.type == "cuda")
        # Written by `digits` next to the HUD calibration; without it ward counts are not read.
        self.digits = Digits.load(calibration.with_name(DIGITS_FILE))

    def ocr(self, crops: list[np.ndarray]) -> list[tuple[str, float]]:
        prepared = [cv2.copyMakeBorder(cv2.resize(c, None, fx=3, fy=3, interpolation=cv2.INTER_CUBIC),
                                       8, 8, 16, 16, cv2.BORDER_REPLICATE) for c in crops]
        results, _ = self.text_reader(prepared)
        return [(text, float(score)) for text, score in results]

    def anchors(self, gray: np.ndarray, x: int) -> dict:
        x0, y0, x1, y1 = TREE
        band = gradient(gray[y0 - 4:y1 + 4, x + x0 - SEARCH:x + x1 + SEARCH])
        _, left_score, _, left = cv2.minMaxLoc(cv2.matchTemplate(band, self.tree, cv2.TM_CCOEFF_NORMED))
        x0, y0, x1, y1 = PANEL
        band = gray[y0 - 3:y1 + 3, x + x0 - SEARCH:x + x1 + SEARCH].astype(np.float32)
        r = cv2.matchTemplate(band, self.panel, cv2.TM_SQDIFF, mask=self.panel_mask)
        low, _, right, _ = cv2.minMaxLoc(r)
        return {"left": x + left[0] - SEARCH, "leftY": left[1] - 4, "leftScore": round(left_score, 3),
                "right": x + right[0] - SEARCH, "rightY": right[1] - 3,
                "rightRms": round(float(np.sqrt(max(low, 0) / self.panel_mask.sum())), 2)}

    def read(self, image: np.ndarray, heroes: set[str] | None = None, full: bool = True) -> dict:
        """`heroes`: the heroes in this match, when known, for naming the HUD's hero. Without `full`
        only whose HUD it is, level, health, mana and alive are read (items and attributes are None)."""
        frame, x = normalize(image)
        gray = np.zeros(frame.shape[:2], np.uint8)
        cv2.cvtColor(frame[HUD_TOP:], cv2.COLOR_BGR2GRAY, dst=gray[HUD_TOP:])  # only the HUD is read
        a = self.anchors(gray, x)
        if a["leftScore"] < 0.5 or a["rightRms"] > 12:
            return {"hud": False, "anchors": a}
        dl, dr, dy = a["left"], a["right"], a["rightY"]
        items, filled = {}, []
        for slot in SLOTS if full else ():
            x0, y0, w, h = window(slot, dr, dy)
            empty = self.empty.get(kind(slot))
            if empty is not None and float(np.abs(gray[y0:y0 + h, x0:x0 + w].astype(np.float32) - empty).mean()) < EMPTY_MAD:
                items[slot] = None
            else:
                filled.append(slot)
        classified = {}
        if filled:
            edges = np.zeros(gray.shape, np.float32)
            edges[HUD_TOP:] = gradient(gray[HUD_TOP:])
            classified = self.items.classify(gray, edges, filled, dr, dy)
        for slot, (group, score) in classified.items():
            if kind(slot) != "backpack":  # the backpack draws its icons grey
                x0, y0, w, h = window(slot, dr, dy)
                group = self.items.by_tint(group, frame[y0:y0 + h, x0:x0 + w])
            name = "item_recipe" if all(n.startswith("item_recipe_") for n in group) else group[0]
            info = self.manifest["items"].get(name, {})
            items[slot] = {"name": name if score >= MIN_SCORE else None, "nameZh": info.get("nameZh") if score >= MIN_SCORE else None,
                           "score": round(score, 3), **({"sameIcon": group[1:6]} if len(group) > 1 and name != "item_recipe" else {}),
                           **({"best": name} if score < MIN_SCORE else {})}
        items = {slot: items[slot] for slot in SLOTS} if full else None
        wards = self.wards(frame, items, dr, dy) if full else None
        hero = self.abilities.identify(gray, dl, heroes)
        bars = self.bars(frame, dl, dr)
        # A dead hero's HUD covers the attributes with the buyback panel.
        dead = bars["health"][2] == 0
        attributes = None if dead or not full else self.attribute_spans(frame, dl)
        centre = ((BAR_ENDS[0] + dl) + (BAR_ENDS[1] + dr)) // 2
        crops = {"level": frame[box(LEVEL, dl)], "name": frame[box(NAME, dl)]}
        crops |= {k: frame[ATTRIBUTE_ROW[0]:ATTRIBUTE_ROW[1], x0:x1] for k, (x0, x1) in (attributes or {}).items()
                  if x1 - x0 >= 5}
        crops |= {k: frame[y0:y1 + 1, centre - BAR_TEXT:centre + BAR_TEXT] for k, (y0, y1, _) in bars.items()}
        text = dict(zip(crops, self.ocr(list(crops.values()))))
        values = {k: ratio(text[k][0], fill) for k, (_, _, fill) in bars.items()}
        # Abilities all on cooldown can leave the icons undecided; the name label then decides. A
        # whole name in the label also overrules the icons, which can show another hero's spells
        # (Rubick's stolen pair, such as Sharpshooter and its release).
        named = self.names.match(text["name"][0], heroes)
        hero["named"] = named
        if named and (hero["hero"] is None or named == self.names.whole(text["name"][0], heroes)):
            hero["hero"] = named
        return {
            "hud": True, "anchors": a, "alive": not dead,
            "hero": hero["hero"], "heroMatch": hero,
            "level": number(text["level"][0], 1, 30),
            "attributes": full and {k: number(text[k][0], 0, 999, last=True) if k in text else None for k in ATTRIBUTE_HUES} or None,
            **values,
            "fill": {k: round(fill, 4) for k, (_, _, fill) in bars.items()},
            "items": items,
            "wards": wards,
            "ocr": {k: [t, round(s, 3)] for k, (t, s) in text.items()},
        }

    def wards(self, frame: np.ndarray, items: dict, dr: int, dy: int) -> dict | None:
        """Wards in the main slots and backpack: {"observer", "sentry", "unknown"} counts, unknown for grey icons
        (backpack, cooldown) whose kind the colour cannot tell. Each ward slot also gets "count" (and for a dispenser
        "front" and "behind"). None when a ward slot's count cannot be read, or without digit templates: such a read
        says nothing about how many wards the hero has."""
        if self.digits is None:
            return None
        out = {"observer": 0, "sentry": 0, "unknown": 0}
        for slot, item in items.items():
            if not item or item["name"] not in WARD_ITEMS or not slot.startswith("slot"):
                continue
            cx, cy = SLOTS[slot][0] + dr, SLOTS[slot][1] + dy
            # Brightness around the slot; the search positions below are relative to its centre at (40, 32).
            value = cv2.cvtColor(frame[cy - 32:cy + 24, cx - 40:cx + 34], cv2.COLOR_BGR2HSV)[..., 2].astype(np.float32)
            back = kind(slot) == "backpack"
            count = self.digits.read(value, 40, 32, COUNT_SEARCH["backpack" if back else "main"])
            front = None if back else self.ward_tint(frame, cx, cy)
            item["count"] = count
            if count is None:
                return None
            if item["name"] != "item_ward_dispenser":
                # A grey icon's match cannot be trusted with the kind: a sentry on cooldown matched the observer.
                out[front or "unknown"] += count
                continue
            behind = self.digits.read(value, 40, 32, BEHIND_SEARCH["backpack" if back else "main"], small=True)
            item["front"], item["behind"] = front, behind
            if behind is None:
                return None
            if front is None:
                out["unknown"] += count + behind
            else:
                out[front] += count
                out["sentry" if front == "observer" else "observer"] += behind
        return out

    @staticmethod
    def ward_tint(frame: np.ndarray, cx: int, cy: int) -> str | None:
        """"observer" (yellow) or "sentry" (blue) for the front ward of a main slot; None when grey."""
        (x0, x1), (y0, y1) = WARD_TINT
        hsv = cv2.cvtColor(frame[cy + y0:cy + y1, cx + x0:cx + x1], cv2.COLOR_BGR2HSV).reshape(-1, 3)
        vivid = hsv[hsv[:, 1] > 80, 0]
        if len(vivid) < 100:
            return None
        yellow, blue = float(((vivid >= 12) & (vivid <= 40)).mean()), float(((vivid >= 80) & (vivid <= 110)).mean())
        return "observer" if yellow > 0.4 and yellow > blue else "sentry" if blue > 0.4 else None

    @staticmethod
    def attribute_spans(frame: np.ndarray, dl: int) -> dict[str, tuple[int, int]] | None:
        """Column span of each attribute value: from the previous icon to its own icon."""
        x0 = ATTRIBUTE_SPAN[0] + dl
        hsv = cv2.cvtColor(frame[ATTRIBUTE_ROW[0]:ATTRIBUTE_ROW[1], x0:ATTRIBUTE_SPAN[1] + dl], cv2.COLOR_BGR2HSV)
        hue, vivid = hsv[..., 0], (hsv[..., 1] > 120) & (hsv[..., 2] > 90)
        icons = {}
        for name, ranges in ATTRIBUTE_HUES.items():
            mask = vivid & np.any([(hue >= a) & (hue <= b) for a, b in ranges], axis=0)
            columns = np.flatnonzero(mask.sum(0) >= 3)
            runs = [r for r in np.split(columns, np.flatnonzero(np.diff(columns) > 2) + 1) if 6 <= len(r) <= 18]
            if not runs:
                return None
            run = max(runs, key=len)
            icons[name] = (x0 + run[0], x0 + run[-1])
        s, a, i = icons["strength"], icons["agility"], icons["intelligence"]
        if not s[1] < a[0] < a[1] < i[0]:
            return None
        # Strength has no icon on its left, only the portrait magnifier: three digits reach into
        # it, two leave a gap. The value is the run of light columns left of the icon.
        hsv = cv2.cvtColor(frame[1063:1078, MAGNIFIER + dl:s[0]], cv2.COLOR_BGR2HSV)
        light = np.flatnonzero((((hsv[..., 2] > 120) & (hsv[..., 1] < 80)).sum(0) >= 2)[::-1])  # from the icon leftwards
        start = s[0] - 1
        if len(light) and light[0] <= 8:
            run = light[:np.argmax(np.append(np.diff(light) > 6, True)) + 1]  # a 1 leaves 4-5 px gaps
            start = s[0] - 1 - run[-1]
        return {"strength": (max(MAGNIFIER + dl, start - 2), s[0] - 1), "agility": (s[1] + 2, a[0] - 1),
                "intelligence": (a[1] + 2, i[0] - 1)}

    @staticmethod
    def bars(frame: np.ndarray, dl: int, dr: int) -> dict[str, tuple[int, int, float]]:
        """Rows and drawn fill share of the health and mana bars."""
        left, right = BAR_ENDS[0] + dl, BAR_ENDS[1] + dr
        bright = np.zeros((frame.shape[0], right - left), np.float32)
        bright[1015:1075] = frame[1015:1075, left:right].max(2)
        # Health fills from the left, so near its start the barrier layout shows a bright thin bar
        # over dark rows where the plain layout has one continuous bar. (The first 15 columns
        # hold the barrier icon and the bars' end glow.)
        start = bright[:, 20:60].mean(1)
        layout = "barrier" if start[1019:1023].mean() - start[1024:1029].mean() > 80 else "plain"
        h0, h1, m0, m1 = BAR_ROWS[layout]
        out = {}
        for name, top, bottom in (("health", h0, h1), ("mana", m0, m1)):
            # Rows just under the top edge stay clear of the centred value and regeneration text.
            # Filled columns are bright; a dead hero's bars are dimmed.
            threshold = 70 if name == "mana" and out["health"][2] == 0 else 95
            lit = bright[top + 2:top + 4].mean(0) > threshold
            if layout == "barrier" and name == "health":
                lit = HudRecognizer.under_text(frame[top + 2:top + 4, left:right], lit)
            out[name] = (top, bottom, float(lit.mean()))
        return out

    @staticmethod
    def under_text(rows: np.ndarray, lit: np.ndarray) -> np.ndarray:
        """The barrier layout's shorter health bar has its white value text, with a dark outline, in
        the measured rows. Health fills from the left, so the columns the text spans are all filled
        when the bar is lit right of them, and otherwise up to the last coloured column among them:
        text over the empty part of the bar must not add to the fill."""
        colour = rows.astype(np.float32).mean(0)
        value = colour.max(1)
        coloured = value - colour.min(1) >= 0.25 * value
        text = np.flatnonzero(lit & ~coloured)
        if not len(text):
            return lit
        a, b = text[0], text[-1] + 1
        out = lit.copy()
        out[a:b] = lit[b:].any()
        if not lit[b:].any() and len(inside := np.flatnonzero(lit[a:b] & coloured[a:b])):
            out[a:a + inside[-1] + 1] = True
        return out


def number(text: str, low: int, high: int, last: bool = False) -> int | None:
    """Digits of a single value. The level ring comes back as brackets around them; with `last`,
    stray marks to the left (the magnifier, the previous icon) are dropped too."""
    text = text.replace("O", "0").replace("o", "0")
    if last:
        runs = re.findall(r"\d+", text)
        digits = runs[-1] if runs else None
    else:
        m = re.fullmatch(r"[\s()\[\]{}|]*(\d+)[\s()\[\]{}|.]*", text)
        digits = m[1] if m else None
    return int(digits) if digits is not None and low <= int(digits) <= high else None


def ratio(text: str, fill: float) -> dict | None:
    """'650 / 962' -> current and max. The slash often comes back as 1 or goes missing, so every
    reading of the digits is tried and the one matching the drawn bar fill wins."""
    t = re.sub(r"[|lI\\／⁄∕]", "/", text)  # the Chinese recognizer may return a full-width slash
    groups = re.findall(r"\d+|/", t)
    digits = "".join(groups)
    pairs = {}  # (current, max) -> rank; lower ranks are the more common misreadings

    def add(a, b, rank):
        if a and b and not (len(a) > 1 and a[0] == "0") and b[0] != "0" and int(a) <= int(b):
            pairs[(int(a), int(b))] = min(rank, pairs.get((int(a), int(b)), rank))
    # With the slash read, only a digit right beside it can be the slash read twice ("2131/435"):
    # regrouping further lets a bar that lags a changing value fit a wrong reading, such as
    # 770/814 for "1770 / 1814" after a trim.
    slash = digits.count("/") == 1
    for trim, extra in ((digits, 0), (digits[1:], 3), (digits[:-1], 3)):  # one stray mark at an end
        if trim.count("/") == 1:
            a, _, b = trim.partition("/")
            add(a, b, extra)
        if slash:
            continue
        plain = trim.replace("/", "")
        for i in range(1, len(plain) - 1):
            if plain[i] in "17":
                add(plain[:i], plain[i + 1:], 1 + extra)  # slash read as a digit
    if slash:
        a, _, b = digits.partition("/")
        if a[-1:] in ("1", "7"):
            add(a[:-1], b, 1)
        if b[:1] in ("1", "7"):
            add(a, b[1:], 1)
    numbers = [g for g in groups if g != "/"]
    if len(numbers) == 2:
        add(numbers[0], numbers[1], 2)  # slash dropped
    # Small currents leave the fill unable to separate readings, so the likelier reading wins.
    fits = [(rank, abs(c / m - fill), c, m) for (c, m), rank in pairs.items() if abs(c / m - fill) <= FILL_MATCH]
    if not fits:
        return None
    _, _, current, maximum = min(fits)
    return {"current": current, "max": maximum}


def calibrate(datasets: list[Path], reference: Path, out: Path, replays: set[str] | None = None):
    """Static HUD templates and empty-slot appearance from captured, GSI-labelled frames."""
    ref = cv2.cvtColor(normalize(cv2.imread(str(reference)))[0], cv2.COLOR_BGR2GRAY)
    tree = ref[box(TREE)]
    T = gradient(tree)
    rows = [(d, row) for d in datasets for row in map(json.loads, open(d / "captures.jsonl", encoding="utf-8"))
            if replays is None or row["replay"] in replays]
    standard, frames = [], []
    for d, row in rows:
        frame, x = normalize(cv2.imread(str(d / row["image"])))
        gray = cv2.cvtColor(frame, cv2.COLOR_BGR2GRAY)
        x0, y0, x1, y1 = TREE
        band = gradient(gray[y0 - 4:y1 + 4, x + x0 - SEARCH:x + x1 + SEARCH])
        _, score, _, loc = cv2.minMaxLoc(cv2.matchTemplate(band, T, cv2.TM_CCOEFF_NORMED))
        if score > 0.8 and loc == (SEARCH, 4):
            standard.append(gray[PANEL[1]:PANEL[3], x + PANEL[0]:x + PANEL[2]].astype(np.float32))
            frames.append((gray, x, row))
    stack = np.stack(standard)
    median = np.median(stack, 0)
    mask = ((stack.std(0) < 5) & (median > 8)).astype(np.float32)
    # Empty slots of one kind share a look, and some slots are rarely empty, so they are pooled.
    empties = {}
    for gray, x, row in frames:
        items = row["gsi"]["items"][row["selected"]["team"]][f"player{row['selected']['playerId']}"]
        for slot in SLOTS:
            if (items.get(slot) or {}).get("name") == "empty":
                x0, y0, w, h = window(slot, x)
                empties.setdefault(kind(slot), []).append(gray[y0:y0 + h, x0:x0 + w].astype(np.float32))
    np.savez_compressed(out, tree=tree, panel=median, panel_mask=mask,
                        **{f"empty_{k}": np.median(np.stack(v), 0) for k, v in empties.items() if len(v) >= 3})
    print(f"{len(standard)} standard-layout frames of {len(rows)}; static panel share {mask.mean():.2f};",
          "empty references:", {k: len(v) for k, v in empties.items()})


def count_glyph(value: np.ndarray, saturation: np.ndarray, cx: int, cy: int) -> tuple[int, int] | None:
    """The pixel diagonally past the bottom-right corner of the one white digit in a main slot's count corner (the
    corner a template patch is anchored at: it keeps a row and column of outline), or None (none, two, or unclear)."""
    x0, y0 = cx + 4, cy + 3
    mask = ((value[y0:cy + 24, x0:cx + 32] >= 160) & (saturation[y0:cy + 24, x0:cx + 32] <= 90)).astype(np.uint8)
    n, _, stats, _ = cv2.connectedComponentsWithStats(mask, 8)
    glyphs = [s for s in stats[1:] if 8 <= s[3] <= 13 and abs(y0 + s[1] + s[3] - (cy + 17)) <= 1]
    if len(glyphs) != 1:
        return None
    x, y, w, h, _ = glyphs[0]
    return x0 + x + w, y0 + y + h


def calibrate_digits(hud: "HudRecognizer", datasets: list[Path], out: Path, replays: set[str] | None = None):
    """Stack-count digit templates from GSI-labelled frames: one-digit counts in the main slots of items that draw them
    there. Pixels that vary between samples (the art behind the glyph) are masked out."""
    rows_, cols_ = DIGIT_PATCH
    patches: dict[str, list[np.ndarray]] = {}
    for d in datasets:
        for row in map(json.loads, open(d / "captures.jsonl", encoding="utf-8")):
            if replays is not None and row["replay"] not in replays:
                continue
            frame, x = normalize(cv2.imread(str(d / row["image"])))
            a = hud.anchors(cv2.cvtColor(frame, cv2.COLOR_BGR2GRAY), x)
            if a["leftScore"] < 0.5 or a["rightRms"] > 12:
                continue
            hsv = cv2.cvtColor(frame, cv2.COLOR_BGR2HSV)
            value, saturation = hsv[..., 2].astype(np.float32), hsv[..., 1]
            items = row["gsi"]["items"][row["selected"]["team"]][f"player{row['selected']['playerId']}"]
            for i in range(6):
                item = items.get(f"slot{i}") or {}
                charges = item.get("charges")
                if item.get("name") not in COUNTED or not isinstance(charges, int) or not 1 <= charges <= 9:
                    continue
                cx, cy = SLOTS[f"slot{i}"][0] + a["right"], SLOTS[f"slot{i}"][1] + a["rightY"]
                if (corner := count_glyph(value, saturation, cx, cy)) is not None:
                    xr, yb = corner
                    patches.setdefault(str(charges), []).append(value[yb - rows_ + 1:yb + 1, xr - cols_ + 1:xr + 1])
    if set(patches) != set("123456789") or any(len(v) < 3 for v in patches.values()):
        raise SystemExit(f"Too few labelled digits: { {k: len(v) for k, v in sorted(patches.items())} }")
    arrays = {}
    for digit, samples in patches.items():
        stack = np.stack(samples)
        arrays[f"t{digit}"], arrays[f"m{digit}"] = stack.mean(0), (stack.std(0) < 45).astype(np.float32)
    np.savez_compressed(out, **arrays)
    print("digit templates:", {k: len(v) for k, v in sorted(patches.items())}, "->", out)


def main():
    ap = argparse.ArgumentParser(description=__doc__)
    sub = ap.add_subparsers(dest="command", required=True)
    c = sub.add_parser("calibrate")
    c.add_argument("datasets", nargs="+", type=Path)
    c.add_argument("--reference", type=Path, required=True, help="a 4-ability hero frame")
    c.add_argument("--out", type=Path, default=Path(".tmp/vision-assets/hud-1080.npz"))
    c.add_argument("--replays", nargs="*", help="only frames from these replay files (keeps others for evaluation)")
    g = sub.add_parser("digits", help="stack-count digit templates (ward counts) from GSI-labelled frames")
    g.add_argument("datasets", nargs="+", type=Path)
    g.add_argument("--assets", type=Path, default=Path(".tmp/vision-assets"))
    g.add_argument("--calibration", type=Path, default=Path(".tmp/vision-assets/hud-1080.npz"))
    g.add_argument("--replays", nargs="*", help="only frames from these replay files (keeps others for evaluation)")
    r = sub.add_parser("read")
    r.add_argument("images", nargs="+", type=Path)
    r.add_argument("--assets", type=Path, default=Path(".tmp/vision-assets"))
    r.add_argument("--calibration", type=Path, default=Path(".tmp/vision-assets/hud-1080.npz"))
    args = ap.parse_args()
    if args.command == "calibrate":
        calibrate(args.datasets, args.reference, args.out, set(args.replays) if args.replays else None)
        return
    hud = HudRecognizer(args.assets, args.calibration)
    if args.command == "digits":
        calibrate_digits(hud, args.datasets, args.calibration.with_name(DIGITS_FILE), set(args.replays) if args.replays else None)
        return
    sys.stdout.reconfigure(encoding="utf-8")
    for path in args.images:
        print(json.dumps({"image": path.name, **hud.read(cv2.imread(str(path)))}, ensure_ascii=False))


if __name__ == "__main__":
    main()
