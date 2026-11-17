"""Capture bottom-HUD frames from local replays, labelled with the client's own GSI.

Offline dataset tool for the HUD recognizer. It starts its OWN Dota process in
-insecure mode (no matchmaking, local console only), never attaches to a game the
user is playing, and removes its temporary GSI config on exit. Which hero the
replay director has selected comes from GSI `selected_unit`; numeric ground truth
comes later from the replay itself (truth.py), not from this capture.
"""
from __future__ import annotations
import argparse
import ctypes
import ctypes.wintypes as wt
import http.server
import json
import os
import queue
import random
import re
import secrets
import socket
import subprocess
import threading
import time
from pathlib import Path

import cv2
import numpy as np

INSTALL = Path("C:/Steam/steamapps/common/dota 2 beta")
CONSOLE_PORT, GSI_PORT = 29009, 3299
GSI_NAME = "gamestate_integration_coach_hud_capture.cfg"
user32, gdi32 = ctypes.windll.user32, ctypes.windll.gdi32
user32.SetProcessDpiAwarenessContext(ctypes.c_void_p(-4))  # physical pixels


class Console:
    """Line-oriented client for the game's -netconport console."""

    def __init__(self, password: str):
        self.sock = socket.create_connection(("127.0.0.1", CONSOLE_PORT), timeout=5)
        self.sock.settimeout(None)  # the console is often silent for longer than any read timeout
        self.lines: queue.Queue[str] = queue.Queue()
        threading.Thread(target=self._read, daemon=True).start()
        self.send(f"PASS {password}")

    def _read(self):
        buffer = b""
        while True:
            try:
                data = self.sock.recv(65536)
            except OSError:
                return
            if not data:
                return
            buffer += data
            *lines, buffer = buffer.split(b"\n")
            for line in lines:
                self.lines.put(line.decode("utf-8", "replace").strip())

    def send(self, command: str):
        self.sock.sendall((command + "\n").encode())

    def drain(self):
        while not self.lines.empty():
            self.lines.get_nowait()

    def wait(self, pattern: str, timeout: float) -> re.Match | None:
        deadline, rx = time.monotonic() + timeout, re.compile(pattern)
        while time.monotonic() < deadline:
            try:
                m = rx.search(self.lines.get(timeout=max(0.01, deadline - time.monotonic())))
            except queue.Empty:
                break
            if m:
                return m
        return None


class GsiSink(http.server.ThreadingHTTPServer):
    """Keeps only the newest authenticated payload and when it arrived."""

    def __init__(self, token: str):
        self.token, self.latest, self.at = token, None, 0.0
        sink = self

        class Handler(http.server.BaseHTTPRequestHandler):
            def do_POST(self):
                body = self.rfile.read(min(int(self.headers.get("Content-Length", 0)), 4 << 20))
                try:
                    payload = json.loads(body)
                    if payload.get("auth", {}).get("token") == sink.token:
                        sink.latest, sink.at = payload, time.monotonic()
                except ValueError:
                    pass
                self.send_response(200)
                self.end_headers()

            def log_message(self, *_):
                pass

        super().__init__(("127.0.0.1", GSI_PORT), Handler)
        threading.Thread(target=self.serve_forever, daemon=True).start()

    def after(self, t: float, timeout: float = 3.0):
        deadline = time.monotonic() + timeout
        while time.monotonic() < deadline:
            if self.at > t:
                return self.latest
            time.sleep(0.05)
        return None


def window() -> int:
    handle = user32.FindWindowW(None, "Dota 2")
    if not handle:
        raise RuntimeError("Dota window not found")
    return handle


def grab(handle: int) -> np.ndarray:
    """PrintWindow with full-content rendering: works while the window is covered."""
    rect = wt.RECT()
    user32.GetClientRect(handle, ctypes.byref(rect))
    w, h = rect.right, rect.bottom
    hdc = user32.GetDC(handle)
    mdc, bmp = gdi32.CreateCompatibleDC(hdc), gdi32.CreateCompatibleBitmap(hdc, w, h)
    try:
        gdi32.SelectObject(mdc, bmp)
        if not user32.PrintWindow(handle, mdc, 3):  # PW_CLIENTONLY | PW_RENDERFULLCONTENT
            raise RuntimeError("PrintWindow failed")
        header = (ctypes.c_uint32 * 10)(40, w, (-h) & 0xFFFFFFFF, 1 | (32 << 16), 0, 0, 0, 0, 0, 0)
        pixels = ctypes.create_string_buffer(w * h * 4)
        if gdi32.GetDIBits(mdc, bmp, 0, h, pixels, header, 0) != h:
            raise RuntimeError("GetDIBits failed")
        return np.frombuffer(pixels, np.uint8).reshape(h, w, 4)[:, :, :3].copy()
    finally:
        gdi32.DeleteObject(bmp)
        gdi32.DeleteDC(mdc)
        user32.ReleaseDC(handle, hdc)


def selected(gsi: dict) -> dict | None:
    for team, players in (gsi.get("hero") or {}).items():
        for slot, hero in players.items():
            if isinstance(hero, dict) and hero.get("selected_unit"):
                pid = int(slot.removeprefix("player"))
                items = ((gsi.get("items") or {}).get(team) or {}).get(slot) or {}
                return {"playerId": pid, "team": team, "hero": hero,
                        "items": {k: v.get("name") for k, v in items.items() if isinstance(v, dict)}}
    return None


def dota_running() -> bool:
    listing = subprocess.run(["tasklist", "/FI", "IMAGENAME eq dota2.exe", "/NH"], capture_output=True, text=True).stdout
    return "dota2.exe" in listing


def launch(width: int, height: int, password: str):
    # The started process may hand over to a relaunched one, so ownership is "no Dota existed before".
    exe = INSTALL / "game/bin/win64/dota2.exe"
    subprocess.Popen([str(exe), "-insecure", "-gamestateintegration", "-console", "-novid", "-windowed",
                      "-w", str(width), "-h", str(height), "-netconport", str(CONSOLE_PORT),
                      "-netconpassword", password], cwd=exe.parent)


def connect(password: str) -> Console:
    # The first process can accept and then hand over to a relaunch, so reconnect until one answers.
    def answers(console: Console) -> bool:
        console.send("echo HUD_CAPTURE_READY")
        return bool(console.wait("^HUD_CAPTURE_READY$", 2))

    for _ in range(90):
        try:
            console = Console(password)
            if answers(console):
                time.sleep(15)  # dashboard finishes loading; then the same socket must still answer
                if answers(console):
                    return console
            console.sock.close()
        except OSError:
            pass
        time.sleep(2)
    raise RuntimeError("Game console did not answer")


def write_gsi_config(token: str) -> Path:
    folder = INSTALL / "game/dota/cfg/gamestate_integration"
    folder.mkdir(exist_ok=True)
    path = folder / GSI_NAME
    data = "\n".join(f' "{k}" "1"' for k in ("provider", "map", "player", "hero", "abilities", "items"))
    with open(path, "x", encoding="utf-8") as f:  # never overwrite a file we did not create
        f.write(f'"coach_hud_capture"\n{{\n "uri" "http://127.0.0.1:{GSI_PORT}/"\n "timeout" "5.0"\n'
                f' "buffer" "0.1"\n "throttle" "0.1"\n "heartbeat" "1.0"\n "auth" {{ "token" "{token}" }}\n'
                f' "data"\n {{\n{data}\n }}\n}}\n')
    return path


def capture_replay(console: Console, gsi: GsiSink, dem: Path, samples: int, rng: random.Random, out: Path, log):
    console.drain()
    console.send(f'playdemo "{dem.as_posix()}"')
    if not console.wait(r"Requesting playback", 10):
        raise RuntimeError(f"Replay rejected: {dem}")
    for _ in range(12):  # map load
        time.sleep(5)
        console.drain()
        console.send("demo_info")
        total = console.wait(r"playback_ticks: (\d+)", 5)
        if total:
            break
    else:
        raise RuntimeError("demo_info did not report a length")
    time.sleep(5)
    ticks = sorted(rng.sample(range(int(int(total[1]) * 0.08), int(int(total[1]) * 0.97)), samples))
    for goal in ticks:
        for attempt in range(3):
            # Seek a little early and play into the target so the director's selection and the
            # HUD settle. Seeks only complete while playing; demo_step_tick proved unreliable,
            # so the paused state is labelled from GSI rather than from an exact tick.
            console.send("demo_resume")
            time.sleep(0.3)
            console.drain()
            console.send(f"demo_gototick {goal - 90 + attempt * 150}")
            seek = console.wait(r"Demo Skipping finished at tick (\d+)", 60)
            if not seek:
                print("seek timed out", goal, flush=True)
                continue
            time.sleep(2.5)
            console.send("demo_pause")
            stamp = time.monotonic()
            time.sleep(1.5)  # HUD transitions settle; GSI heartbeats the paused state
            state = gsi.after(stamp + 0.5)
            pick = state and selected(state)
            if not pick:
                print("no selected hero", goal, "gsi" if state else "no gsi", flush=True)
                continue
            image = grab(window())
            if (selected(gsi.latest) or {}).get("playerId") != pick["playerId"]:
                print("selection changed during capture", goal, flush=True)
                continue
            name = f"{dem.stem}-{seek[1]}-{image.shape[1]}x{image.shape[0]}.png"
            cv2.imwrite(str(out / name), image)
            log.write(json.dumps({"image": name, "replay": dem.name, "seekTick": int(seek[1]),
                                  "width": image.shape[1], "height": image.shape[0], "selected": pick,
                                  "gsi": {k: state.get(k) for k in ("map", "hero", "items")}},
                                 ensure_ascii=False) + "\n")
            log.flush()
            break


def main():
    ap = argparse.ArgumentParser(description=__doc__)
    ap.add_argument("replays", nargs="+", type=Path)
    ap.add_argument("--out", type=Path, required=True)
    ap.add_argument("--samples", type=int, default=30, help="seek targets per replay")
    ap.add_argument("--width", type=int, default=1920)
    ap.add_argument("--height", type=int, default=1080)
    ap.add_argument("--seed", type=int, default=7)
    args = ap.parse_args()
    args.out.mkdir(parents=True, exist_ok=True)
    if dota_running():
        raise RuntimeError("A Dota process already exists; close it (this tool never attaches to a user game)")
    index = args.out / "captures.jsonl"
    done = {json.loads(line)["replay"] for line in open(index, encoding="utf-8")} if index.exists() else set()
    password = secrets.token_hex(16)
    config = write_gsi_config(password)

    def stop_game():
        # Safe because no Dota existed when this run started.
        subprocess.run(["taskkill", "/IM", "dota2.exe", "/F"], capture_output=True)
        for _ in range(60):
            if not dota_running():
                return
            time.sleep(1)

    try:
        sink = GsiSink(password)
        rng = random.Random(args.seed)
        with open(index, "a", encoding="utf-8") as log:
            for dem in args.replays:
                if dem.name in done:
                    continue
                # A second playdemo in the same process leaves the director without a selected hero,
                # so every replay gets a fresh game process.
                launch(args.width, args.height, password)
                try:
                    capture_replay(connect(password), sink, dem.resolve(), args.samples, rng, args.out, log)
                    print("captured", dem.name, flush=True)
                except RuntimeError as error:
                    print("skipped", dem.name, error, flush=True)
                finally:
                    stop_game()
    finally:
        stop_game()
        os.remove(config)


if __name__ == "__main__":
    main()
