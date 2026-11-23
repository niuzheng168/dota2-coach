"""Static inspection sheets from recorded coordinates; interactive viewer remains primary."""
import json
import sys
from pathlib import Path
from PIL import Image, ImageDraw, ImageFont
from replay_storage import replay_area

root = Path(".")
index = json.loads(Path(sys.argv[1] if len(sys.argv) > 1 else "data/luna-hundred-index.json").read_text(encoding="utf8"))
reference = json.loads((root / "data/dota2-map-reference.json").read_text(encoding="utf8"))
background_file = replay_area("map-reference") / "dota-map-741f.png"
background = Image.open(background_file if background_file.is_file() else root / ".tmp/map-reference/dota-map-741f.png").convert("RGB")
font = ImageFont.truetype("C:/Windows/Fonts/msyh.ttc", 15)
small = ImageFont.truetype("C:/Windows/Fonts/msyh.ttc", 12)
size, gap, header, footer = 390, 18, 54, 48
span = max(reference["bounds"]["max"][k] - reference["bounds"]["min"][k] for k in ("x", "y"))
overview = reference["overview"]


def project(x, y):
    return size / 2 + x * size / span, size / 2 - y * size / span


output = replay_area() / "study/sheets"
output.mkdir(parents=True, exist_ok=True)
for page in range((len(index["matches"]) + 11) // 12):
    page_matches = index["matches"][page * 12:(page + 1) * 12]
    page_rows = (len(page_matches) + 2) // 3
    sheet = Image.new("RGB", (3 * (size + gap) + gap, page_rows * (size + header + footer + gap) + gap), "#101a21")
    draw = ImageDraw.Draw(sheet)
    for i, match in enumerate(page_matches):
        x, y = gap + i % 3 * (size + gap), gap + i // 3 * (size + header + footer + gap)
        name = "".join(c for c in match["player"] if c.isprintable())
        draw.text((x, y), f'{match["matchId"]}  {name[:22]}', font=font, fill="#e8e9de")
        draw.text((x, y + 25), f'{match["team"]} · {"PRO" if match["providerPro"] else match.get("matchMmr")} · {"W" if match["won"] else "L"}', font=small, fill="#96aab5")
        tile = Image.new("RGB", (size, size), "#07121a")
        origin = project(overview["posX"], overview["posY"])
        dim = round(overview["coordinateSpan"] * size / span)
        bg = background.resize((dim, dim))
        tile.paste(bg, (round(origin[0]), round(origin[1])))
        tile = Image.blend(Image.new("RGB", tile.size, "#07121a"), tile, .55)
        pen = ImageDraw.Draw(tile)
        for camp in reference["camps"]:
            cx, cy = project(camp["x"], camp["y"])
            pen.ellipse((cx - 2, cy - 2, cx + 2, cy + 2), fill="#bda466")
        previous = None
        end = min(1200, match["duration"])
        for t, px, py, alive, break_before in match["trace"]:
            if not 600 <= t <= end:
                previous = None
                continue
            point = project(px, py)
            if previous and alive and previous[1] and not break_before:
                proportion = (t - 600) / max(1, end - 600)
                color = tuple(round(a + (b - a) * proportion) for a, b in zip((87, 217, 197), (255, 184, 92)))
                pen.line((previous[0], point), fill=color, width=2)
            previous = point, alive
        sheet.paste(tile, (x, y + header))
        a = next(f for f in match["frames"] if f["time"] >= 600)
        b = next((f for f in match["frames"] if f["time"] >= 1200), match["frames"][-1])
        lane, neutral = b["gold"][0] - a["gold"][0], b["gold"][1] - a["gold"][1]
        rate = (lane + neutral) * 60 / (b["time"] - a["time"])
        draw.text((x, y + header + size + 5), f'10:00–{int(b["time"])//60:02}:{int(b["time"])%60:02}  Lane {lane} + Neutral {neutral}', font=small, fill="#e8bf70")
        draw.text((x, y + header + size + 25), f'{rate:.1f} farm gold/min · recorded body path', font=small, fill="#96aab5")
    sheet.save(output / f"routes-{page + 1:02}.jpg", quality=90)
print(f"Rendered {page + 1} sheets for {len(index['matches'])} unique matches.")
