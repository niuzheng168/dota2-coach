"""Live hero scanner for the coach machine's Dota spectator view.

Cycles the spectator's selected hero and reads each hero's bottom HUD with hud_recognizer.py: level,
health, mana, alive and items. Whose HUD a frame shows is read from the frame (ability icons, else
the name label), not assumed from the selection. Where each hero was last seen comes from the same
client's GSI minimap.

Selection: `keys` posts the spectator hotkeys (SpectatorCameraFocusPlayer1-10, 1-0 by default) to
the Dota window. Dota acts on them only while it is the foreground window. Standalone scans try bringing
it to the front once; the coach launcher waits instead. In the background the scanner just watches; posted keys never reach another program. `console`
sends `dota_camera_focus_player <id>` through -netconport, which Dota opens only with -insecure: for
local replays, not for live spectating.

It uses only what this spectator's own screen and GSI show. In Dota Plus live spectating that is the
friend team's vision: enemies are scanned while that team sees them and otherwise keep their last
observed state with its age. Allies are not selected there unless --allies: nothing live needs them, and
the enemies come round sooner. Delayed or replay spectating shows both teams in full and scans all ten;
the state file says so in "mode", and live coaching must not use such data.

Ward counts (observer, sentry, or unknown kind for grey icons) are read with every item read; the last few
changes of each enemy's counts go to the live file with the game time of the first and latest read of each,
to a fraction of a second: the coach client guesses where a ward went when an enemy's count drops.

Speed: heroes already matched to their player are selected one per captured frame; Dota shows a new
selection about every 20 ms, at 60 Hz and 144 Hz alike, so a sweep of ten heroes takes ~0.2 s. A hero
whose items were read --items-every seconds ago (3 by default; an enemy carrying wards --ward-items-every,
0.4) stays ~0.13 s at the end of a sweep, as item slots cross-fade after a switch; sweeps without one
follow each other without a pause.
Recognition runs in parallel on the GPU, on a HUD band that Windows.Graphics.Capture (wgc.py) crops on
the GPU.

The coach client's 启动视觉扫描 button runs `scan --stop-with-stdin --team-view-only` with local assets.
It stops by closing standard input. In this mode only the bounded live file goes to the coach, which
checks the participant before archiving. The standalone session files below are for offline evaluation.

`setup` writes this tool's GSI config (read by Dota at start) and prints the launch options. Each run
records a session folder: timeline.jsonl (a snapshot every --timeline-every seconds, shown by the
local sandbox page /hero-scan), scan.jsonl (every reading), state.json (latest) and summary.json.
"""
from __future__ import annotations
import argparse
import collections
import ctypes
import http.server
import json
import os
import queue
import re
import secrets
import shutil
import sys
import threading
import time
from collections.abc import Callable
from pathlib import Path

import cv2
import numpy as np

sys.path.insert(0, str(Path(__file__).resolve().parent))  # python -I leaves the script folder off the path
from capture_replay import Console, INSTALL  # noqa: E402
from hud_recognizer import SLOTS  # noqa: E402
import wgc  # noqa: E402

GSI_NAME = "gamestate_integration_coach_hero_scan.cfg"
SECRETS = Path(os.environ.get("LOCALAPPDATA", ".")) / "DotaCoach" / "hero-scan" / "secrets.json"
# Read by DotaCoach.CoachClient, which shows its enemy levels and items in a panel on the game machine;
# the panel's icons are copied to icons/ beside it.
LIVE = Path(os.environ.get("LOCALAPPDATA", ".")) / "DotaCoach" / "hero-scan" / "live.json"
BAND_TOP = 880  # 1080-scale row where the captured HUD band starts (the name label begins at 912)
CHANGE = (945, 1075, -300, 500)  # rows, and columns around the centre, compared to detect a new hero
SWITCH_MAD = 15.0  # hero switches measured 20-44 here; other HUD changes up to ~6
SWITCH_TIMEOUT = 0.15  # no change by then: the hero cannot be selected now (enemy out of vision)
# Hero identity, health, mana and level are drawn with the first switched frame; item slots (main,
# backpack, TP) cross-fade from the previous hero for ~7 frames at 60 Hz, so items are read only on a
# visit that stays this long after the switch.
ITEM_SETTLE = 0.13
STREAM_TAIL = 0.07  # after the last streamed selection: its switch shows within ~2 refreshes
RETRY_UNKNOWN = 2.0  # seconds between attempts at a player whose hero is still unknown
STREAM_MISSED = 1.0  # a streamed hero not read for this long gets its own visit next cycle
FRESH_GSI = 3.0
# Ends the run early (--stop-with-stdin): the coach client starts the scanner with a pipe as standard input
# and closes it to stop the scan; the pipe also closes when the client exits or crashes.
STOP = threading.Event()


def stop_when_stdin_closes():
    try:
        sys.stdin.buffer.read()  # nothing is ever written: returns when the pipe closes
    except (OSError, ValueError):
        pass
    STOP.set()


def running(end: float) -> bool:
    return time.perf_counter() < end and not STOP.is_set()


class ConsoleSelect:
    """`dota_camera_focus_player` over -netconport (Dota opens the port only with -insecure)."""

    def __init__(self, password: str, port: int):
        self.console = Console(password, port)
        self.console.send("echo HERO_SCAN_READY")
        if not self.console.wait("^HERO_SCAN_READY$", 3):
            raise SystemExit("The game console did not answer: check -insecure -netconport/-netconpassword")

    def ready(self) -> bool:
        return True

    def select(self, player: int):
        self.console.send(f"dota_camera_focus_player {player}")

    def tick(self):
        self.console.drain()  # the console prints constantly; nothing else reads it


class KeySelect:
    """Spectator hotkeys posted to the Dota window; Dota acts on them only while in the foreground."""

    def __init__(self, window: int, keys: str):
        if len(keys) != 10 or not all(k.isascii() and k.isalnum() for k in keys):
            raise SystemExit("--keys needs the ten hotkeys of SpectatorCameraFocusPlayer1-10, e.g. 1234567890")
        self.window, self.user32 = window, ctypes.windll.user32
        self.keys = [(ord(k.upper()), self.user32.MapVirtualKeyW(ord(k.upper()), 0)) for k in keys]

    def ready(self) -> bool:
        return self.user32.GetForegroundWindow() == self.window

    def select(self, player: int):
        vk, scan = self.keys[player]
        self.user32.PostMessageW(self.window, 0x0100, vk, 1 | scan << 16)  # WM_KEYDOWN
        self.user32.PostMessageW(self.window, 0x0101, vk, 1 | scan << 16 | 3 << 30)  # WM_KEYUP

    def front(self) -> bool:
        """Once at start: foreground changes are refused to background processes unless input is shared."""
        u, kernel = self.user32, ctypes.windll.kernel32
        thread, own = u.GetWindowThreadProcessId(u.GetForegroundWindow(), None), kernel.GetCurrentThreadId()
        u.AttachThreadInput(own, thread, True)
        try:
            u.BringWindowToTop(self.window)
            u.SetForegroundWindow(self.window)
        finally:
            u.AttachThreadInput(own, thread, False)
        time.sleep(0.2)
        return self.ready()

    def tick(self):
        pass


class GsiFeed(http.server.ThreadingHTTPServer):
    """This client's own GSI: game clock, spectator mode, own team and the minimap.

    Last seen = the newest packet whose minimap held the hero with a vision radius (dead heroes
    leave static radius-0 entries). Full-spectator packets also carry every hero's data; that is
    kept only as evaluation truth (`truth`)."""

    def __init__(self, port: int, token: str, keep_truth: bool, team_view_only: bool = False):
        self.token, self.keep_truth, self.lock = token, keep_truth, threading.Lock()
        self.team_view_only = team_view_only
        self.clock = self.state = self.match_id = self.own_team = None
        self.mode, self.packets, self.received = "none", 0, 0.0
        self.epoch, self.live_ready, self.ended_match = 0, False, None
        self.views = collections.deque(maxlen=64)  # sight at capture time, not at OCR completion
        self.ticks = collections.deque(maxlen=64)  # (arrival, clock) of each packet that brought a new clock
        self.seen: dict[str, dict] = {}  # hero -> last sighting
        self.visible: set[str] = set()
        self.teams: dict[str, int] = {}  # hero -> 2 radiant / 3 dire
        self.truth = collections.deque(maxlen=64)  # (arrival, {hero: fields})
        feed = self

        class Handler(http.server.BaseHTTPRequestHandler):
            def do_POST(self):
                body = self.rfile.read(min(int(self.headers.get("Content-Length", 0)), 8 << 20))
                self.send_response(200)
                self.end_headers()
                try:
                    payload = json.loads(body)
                except ValueError:
                    return
                if isinstance(payload, dict) and isinstance(payload.get("auth"), dict) and payload["auth"].get("token") == feed.token:
                    feed.update(payload, time.perf_counter())

            def log_message(self, *_):
                pass

        super().__init__(("127.0.0.1", port), Handler)
        threading.Thread(target=self.serve_forever, daemon=True).start()

    def update(self, p: dict, now: float):
        # A malformed authenticated packet must withdraw the previous view, not leave it live until timeout.
        malformed = any(key in p and not isinstance(p[key], dict)
                        for key in ("map", "player", "hero", "items", "abilities", "buildings", "minimap"))
        if malformed:
            p = {}
        game = p.get("map") or {}
        player = p.get("player") or {}
        grouped = "allplayers" in p or any(re.fullmatch(r"team\d+|player\d+|allplayers", k)
                    for section in ("player", "hero", "items", "abilities") for k in (p.get(section) or {}))
        mode = "unknown" if malformed else "menu" if not game else "full-spectator" if grouped else \
            "player" if player.get("team_name") in ("radiant", "dire") else \
            "team-view" if player.get("team_name") == "spectator" else "unknown"
        units = [] if self.team_view_only and mode != "team-view" else [u for u in (p.get("minimap") or {}).values() if isinstance(u, dict)
                 and re.fullmatch(r"npc_dota_hero_[a-z0-9_]{1,60}", str(u.get("unitname", ""))) and u.get("team") in (2, 3)]
        def located(u):
            return all(type(u.get(k)) in (int, float) and -12000 <= u[k] <= 12000 for k in ("xpos", "ypos")) and (u["xpos"], u["ypos"]) != (0, 0)
        sides = {u["team"] for u in units if u.get("image") == "minimap_herocircle_self" and located(u)}
        sides.update(i for i, name in ((2, "radiant"), (3, "dire")) if name in (p.get("buildings") or {}))
        own = {"radiant": 2, "dire": 3}.get(player.get("team_name")) if mode == "player" else next(iter(sides)) if len(sides) == 1 else None
        match = str(game.get("matchid"))
        match = match if re.fullmatch(r"[1-9][0-9]{0,19}", match) and int(match) < 2**64 else None
        clock = game.get("clock_time")
        clock = clock if type(clock) in (float, int) and -300 <= clock <= 43200 else None
        ready = (mode == "team-view" and match is not None and own in (2, 3) and clock is not None
                 and game.get("paused", False) is False and game.get("game_state") in
                 ("DOTA_GAMERULES_STATE_PRE_GAME", "DOTA_GAMERULES_STATE_GAME_IN_PROGRESS"))
        with self.lock:
            if now < self.received:
                return
            # A terminal is purely a fence. Never adopt its roster/HUD, or a late playing echo of that match.
            if mode == "team-view" and match and own in (2, 3) and game.get("game_state") == "DOTA_GAMERULES_STATE_POST_GAME":
                self.ended_match = match
            ready = ready and match != self.ended_match
            if self.team_view_only and not ready:
                units, game = [], {}
                match = own = clock = None
            if ((match, mode, own, ready) != (self.match_id, self.mode, self.own_team, self.live_ready)
                    or now - self.received >= FRESH_GSI
                    or clock is not None and self.clock is not None and clock < self.clock):
                self.epoch += 1
                self.seen, self.teams = {}, {}
                self.views.clear(); self.ticks.clear(); self.truth.clear()
            if not self.ticks or clock != self.clock or game.get("paused"):
                self.ticks.append((now, clock))
            self.match_id, self.clock, self.state = match, clock, game.get("game_state")
            self.own_team, self.live_ready = own, ready
            self.mode, self.packets, self.received = mode, self.packets + 1, now
            visible = collections.defaultdict(list)
            for u in units:
                self.teams[u["unitname"]] = u.get("team")
                if located(u) and type(u.get("visionrange")) in (int, float) and 0 < u["visionrange"] <= 10000:
                    visible[u["unitname"]].append([u.get("xpos"), u.get("ypos")])
            for hero, spots in visible.items():
                self.seen[hero] = {"x": spots[0][0], "y": spots[0][1], **({"others": spots[1:]} if len(spots) > 1 else {}),
                                   "clock": self.clock, "at": now}
            self.visible = set(visible)
            self.views.append((now, clock, self.visible, ready))
            if self.keep_truth and grouped and not self.team_view_only:
                self.truth.append((now, truth_of(p)))

    def view_at(self, at: float):
        """Called under lock. Never promote an old HUD frame into a new view, or a stale GSI ping into sight."""
        view = next((v for v in reversed(self.views) if v[0] <= at), None)
        return view if view and at - view[0] < FRESH_GSI and (self.mode != "team-view" or self.live_ready and view[3]) else None

    def game_time(self, at: float) -> float | None:
        """Game time at perf-counter time `at`, to a fraction of a second: GSI clocks are whole seconds, so the arrival
        of the packet that brought the second places `at` within it, as the coach client does for its own packets.
        Call with the lock held."""
        tick = next((t for t in reversed(self.ticks) if t[0] <= at), None)
        if tick is None or not isinstance(tick[1], (int, float)):
            return None
        return round(tick[1] + min(max(at - tick[0], 0.0), 0.99), 2)

    def truth_at(self, t: float, hero: str) -> dict | None:
        with self.lock:
            best = min(self.truth, key=lambda e: abs(e[0] - t), default=None)
        return best and best[1].get(hero)


def truth_of(p: dict) -> dict:
    """Every hero's HUD fields and position from a full-spectator packet (replay evaluation only)."""
    out = {}
    for team, players in (p.get("hero") or {}).items():
        for slot, h in (players or {}).items():
            if isinstance(h, dict) and h.get("name"):
                items = ((p.get("items") or {}).get(team) or {}).get(slot) or {}
                out[h["name"]] = {"level": h.get("level"), "alive": h.get("alive"), "x": h.get("xpos"), "y": h.get("ypos"),
                                  "health": [h.get("health"), h.get("max_health")], "mana": [h.get("mana"), h.get("max_mana")],
                                  "items": {k: (items.get(k) or {}).get("name") for k in SLOTS}}
    return out


def leftover(roster: set[str], players: dict[int, str], player: int | None) -> str | None:
    """The hero of the one player not yet matched while the other nine are: the match's ten heroes
    (minimap names, the same heroes the top bar shows) minus theirs. A dead hero whose HUD icons are
    blank and whose name label says "阵亡总结" can be named no other way until it respawns."""
    if player is None or player in players or len(players) != 9 or len(roster) != 10:
        return None
    rest = roster - set(players.values())
    return next(iter(rest)) if len(rest) == 1 else None


def fits(player: int, hero: str, teams: dict[str, int], players: dict[int, str]) -> bool:
    """Whether a switch that showed `hero` may teach that it is `player`'s: hotkeys 1-5 select Radiant's players and
    6-0 Dire's, and a hero already matched to another player is not this one's. On 2026-11-21 the HUD changed to the
    friend's Radiant hero just as Dire's tenth player was selected (at the horn); taken for an ally, that player, Invoker,
    was never selected again and the enemy panel never got his items."""
    if teams.get(hero) in (2, 3) and teams[hero] != 2 + player // 5:
        return False
    return all(h != hero for p, h in players.items() if p != player)


def selectable(mode: str, own: int | None, visible: set[str], teams: dict[str, int], players: dict[int, str],
               due: Callable[[int], bool], allies: bool) -> list[int]:
    """Players to select this cycle. In Plus team view an enemy is selectable only while the friend team sees it, and
    allies are left out unless `allies`: the enemy panel and ward guesses need enemies only, and every hero skipped
    makes the enemies' turn come sooner. Unknown players are tried when `due(player)`, except in a five-player half of
    the hotkeys (1-5, 6-0) where every player matched so far is an ally. Delayed spectating selects everyone."""
    ally_halves = set()
    if mode == "team-view" and own in (2, 3) and not allies:
        sides: dict[int, set[bool]] = {}
        for p, hero in players.items():
            sides.setdefault(p // 5, set()).add(teams.get(hero) == own)
        ally_halves = {half for half, kinds in sides.items() if kinds == {True}}
    out = []
    for p in range(10):
        hero = players.get(p)
        if hero is None:
            if due(p) and p // 5 not in ally_halves:
                out.append(p)
        elif mode != "team-view":
            out.append(p)
        elif teams.get(hero) == own:
            if allies or own not in (2, 3):
                out.append(p)
        elif hero in visible:
            out.append(p)
    return out


class ItemSlots:
    """Enemy inventories for the enemy panel on the game machine: the six main slots and the neutral
    slot from the latest item read, recipes shown as empty slots. A read with an unknown slot is skipped.
    Replayed against full-spectator truth, the latest read was right more often than waiting for two
    reads to agree, which lags behind every purchase.

    Also each enemy's last few ward counts from complete reads (observer, sentry and unknown kind), one entry per
    change: "first" the game time of the first read with these counts and "clock" of the latest, to a fraction of a
    second. The coach client guesses where a ward went when the count drops: it went down between the "clock" of one
    entry and the "first" of the next. (Clients before 2026-11-22 know only "clock", which then still ends the window.)"""

    WARD_READS = 4

    def __init__(self, items: dict):
        self.recipes = {k for k, v in items.items() if v.get("recipe")} | {"item_recipe"}
        self.shown: dict[str, tuple] = {}  # hero -> (slot0..slot5, neutral0)
        self.shown_time: dict[str, float] = {}  # game clock of last complete inventory, not the latest fast level read
        self.wards: dict[str, collections.deque] = {}  # hero -> recent {"first", "clock", "observer", "sentry", "unknown"}

    def read(self, hero: str, slots: dict):
        # The recognizer explicitly emits None for an empty slot. An absent key is an unread slot.
        current = tuple(slots.get(f"slot{i}", "?") for i in range(6)) + (slots.get("neutral0", "?"),)
        time_ = slots.get("time")
        if "?" not in current and (hero not in self.shown_time or time_ is not None and time_ > self.shown_time[hero]):
            self.shown[hero] = tuple(None if n in self.recipes else n for n in current)
            if time_ is not None:
                self.shown_time[hero] = time_
        # The backpack counts too: a ward moved there is still carried.
        counts = slots.get("wards")
        if counts is not None and time_ is not None and "?" not in current + tuple(slots.get(f"slot{i}", "?") for i in range(6, 9)):
            reads = self.wards.setdefault(hero, collections.deque(maxlen=self.WARD_READS))
            if reads and time_ <= reads[-1]["clock"]:
                return
            if reads and all(reads[-1][k] == v for k, v in counts.items()):
                reads[-1]["clock"] = time_
            else:
                reads.append({"first": time_, "clock": time_, **counts})

    def carrying(self, hero: str) -> bool:
        """Whether the newest ward read of this enemy had any: only a carrier can place one."""
        reads = self.wards.get(hero)
        return bool(reads) and reads[-1]["observer"] + reads[-1]["sentry"] + reads[-1]["unknown"] > 0


def dota_window() -> int:
    user32 = ctypes.windll.user32
    handle = user32.FindWindowW(None, "Dota 2")
    pid = ctypes.c_ulong()
    user32.GetWindowThreadProcessId(handle, ctypes.byref(pid))
    if not handle or not pid.value:
        raise RuntimeError("Dota window not found")
    return handle


class Scanner:
    def __init__(self, args, hud, feed: GsiFeed, selector):
        """`selector(window)` makes the ConsoleSelect or KeySelect used to switch heroes."""
        self.args, self.hud, self.feed = args, hud, feed
        self.window = dota_window()
        self.make_selector = selector
        self.selector = selector(self.window)
        self.selecting = True  # False while Dota ignores selections (keys: not in the foreground)
        self.open_capture()
        self.canvas = np.zeros((1080, self.width1080, 3), np.uint8)  # used by the worker thread only
        self.player_hero: dict[int, str] = {}
        self.tried: dict[int, float] = {}
        self.heroes: dict[str, dict] = {}  # hero -> {"status": newest reading, "items": newest settled reading}
        self.shown: str | None = None  # hero the HUD showed last
        self.epoch = -1
        self.missed: set[int] = set()
        # player -> when an item read of it was queued: the worker records it a little later, and the
        # next cycle must not pick the same hero again meanwhile
        self.items_queued: dict[int, float] = {}
        self.work: queue.Queue = queue.Queue()
        self.lock = threading.Lock()
        self.stats = collections.Counter()
        self.switch_ms: list[float] = []
        self.read_ms: list[float] = []
        self.cycle_s: list[float] = []
        self.refresh_s: list[float] = []  # time between two status readings of the same hero
        # Only the coach can bind this source to a participant. Don't keep an unbound parallel
        # archive (including frames from a different game) in the packaged live launcher.
        self.log = open(os.devnull if args.team_view_only else args.out / "scan.jsonl", "a", encoding="utf-8")
        self.timeline = open(os.devnull if args.team_view_only else args.out / "timeline.jsonl", "a", encoding="utf-8")
        names = lambda kind: {k: v.get("nameZh") for k, v in hud.manifest[kind].items() if v.get("nameZh")}
        self.timeline.write(json.dumps({"type": "header", "started": time.strftime("%Y-%m-%dT%H:%M:%S%z"), "slots": list(SLOTS),
                                        "truth": args.truth, "every": args.timeline_every, "names": {"heroes": names("heroes"), "items": names("items")}},
                                       ensure_ascii=False, separators=(",", ":")) + "\n")
        self.started = time.perf_counter()
        self.item_slots = ItemSlots(hud.manifest["items"])  # enemies in Plus team view only
        self.exported: set[str] = set()  # icons copied next to the live file in this run
        self.run_id = secrets.token_hex(8)

    # ---- controller: capture and selection (main thread) ----
    def open_capture(self):
        self.capture = wgc.WindowCapture(self.window, lambda w, h: (0, h * BAND_TOP // 1080, w, h - h * BAND_TOP // 1080))
        cw, ch = self.capture.client
        self.width1080 = round(cw * 1080 / ch)
        s = self.capture.crop[3] / (1080 - BAND_TOP)
        r0, r1, c0, c1 = CHANGE
        self.change_rows = slice(round((r0 - BAND_TOP) * s), round((r1 - BAND_TOP) * s), 4)
        self.change_cols = slice(cw // 2 + round(c0 * s), cw // 2 + round(c1 * s), 4)
        print(f"Capturing the Dota window ({cw}x{ch}).", flush=True)  # the coach client shows the scan as running from here

    def reopen(self, end: float) -> bool:
        """Dota's window changed size: minimized on alt-tab from fullscreen, resized, or a new display mode.
        Keep publishing what is known, wait until the window is usable again and capture it afresh.
        False when the run ended first."""
        self.capture.close()
        self.stats["reopened"] += 1
        print("Dota window changed: waiting for it, then capturing again.", flush=True)
        last = -1e9
        while running(end):
            now = time.perf_counter()
            if now - last >= self.args.timeline_every:
                snap = self.snapshot()
                self.record(snap)
                self.write_live(snap)
                last = now
            try:
                window = dota_window()
                if not ctypes.windll.user32.IsIconic(window) and min(wgc.client_rect(window)[2:]) > 0:
                    if window != self.window:  # Dota was restarted
                        self.window, self.selector = window, self.make_selector(window)
                    self.open_capture()
                    return True
            except (RuntimeError, ValueError, OSError):
                pass  # not there yet, or still changing
            time.sleep(0.25)
        return False

    def bind_view(self, epoch: int):
        """With the scanner lock held. Full spectator → team view of the SAME game must clear all HUD/slot state too."""
        if epoch != self.epoch:
            self.epoch, self.player_hero, self.heroes, self.shown = epoch, {}, {}, None
            self.tried, self.missed, self.items_queued = {}, set(), {}
            self.item_slots = ItemSlots(self.hud.manifest["items"])

    def plan(self, now: float) -> list[tuple[int, bool]]:
        """One cycle: (player, item visit) for every hero that can be selected now. The heroes whose
        items were read longest ago (at most --item-visits) stay long enough for their items."""
        with self.feed.lock, self.lock:
            mode, own, visible, teams = self.feed.mode, self.feed.own_team, set(self.feed.visible), dict(self.feed.teams)
            self.bind_view(self.feed.epoch)
            if (not 0 <= now - self.feed.received < FRESH_GSI or mode == "team-view" and not self.feed.live_ready
                    or self.args.team_view_only and mode != "team-view"):
                return []
            players = dict(self.player_hero)
            items_at = {h: (r.get("items") or {}).get("at", -1e9) for h, r in self.heroes.items()}
            status_at = {h: (r.get("status") or {}).get("at", -1e9) for h, r in self.heroes.items()}
        if mode not in ("team-view", "full-spectator"):
            return []  # menus or playing: the hero hotkeys would land in whatever Dota shows
        out = selectable(mode, own, visible, teams, players, lambda p: now - self.tried.get(p, -1e9) >= RETRY_UNKNOWN,
                         self.args.allies)
        self.missed = {p for p in out if p in players and now - status_at.get(players[p], -1e9) > STREAM_MISSED}
        with self.lock:
            carriers = {p for p in out if p in players and self.item_slots.carrying(players[p])}
        # An enemy carrying wards is due for items sooner: the sooner its count is read again after it places one, the
        # closer the coach knows where it stood then.
        stale = sorted((max(items_at.get(players[p], -1e9), self.items_queued.get(p, -1e9)), p) for p in out if p in players)
        due = [p for at, p in stale if now - at >= (self.args.ward_items_every if p in carriers else self.args.items_every)]
        slow = set(due[:self.args.item_visits])
        return [(p, p in slow) for p in out]

    def changed(self, band: np.ndarray, reference: np.ndarray) -> float:
        a = band[self.change_rows, self.change_cols, :3].astype(np.int16)
        return float(np.abs(a - reference[self.change_rows, self.change_cols, :3]).mean())

    def first_frame(self, end: float):
        while running(end):
            try:
                if (frame := self.capture.latest()) is not None:
                    return frame
            except (wgc.WindowChanged, TimeoutError, OSError):  # window resized/minimized, or capture lost
                if not self.reopen(end):
                    return None
            time.sleep(0.001)
        return None

    def run(self, duration: float):
        end = self.started + duration
        if (frame := self.first_frame(end)) is None:
            self.work.put(None)
            return
        visit = None  # closed-loop visit: {"player", "items", "sent", "ref", "switched"}
        stream: collections.deque[int] = collections.deque()  # players selected one per captured frame
        tail = None  # after the last streamed selection: {"sent", "switched", "items"}
        streamed = -1e9  # when the last streamed selection was sent
        visits: collections.deque[tuple[int, bool]] = collections.deque()
        last_print = last_drain = last_record = cycle_start = self.started
        while running(end):
            try:
                got = self.capture.latest()
            except (wgc.WindowChanged, TimeoutError, OSError):  # window resized/minimized, or capture lost
                # Frames of the old size cannot be compared with new ones: start the cycle afresh.
                if not self.reopen(end) or (frame := self.first_frame(end)) is None:
                    break
                stream.clear(); visits.clear()
                visit = tail = None
                continue
            now = time.perf_counter()
            if self.selecting != (ready := self.selector.ready()):
                self.selecting = ready
                if not ready:  # abandon the cycle; until Dota acts on selections again, only watch
                    stream.clear(); visits.clear()
                    visit = tail = None
            if got is not None:
                previous, frame = frame, got
                if (stream or tail or not self.selecting) and self.changed(frame[1], previous[1]) > SWITCH_MAD:
                    self.work.put((None, True, False, *frame))  # stamp the actual capture, not the polling loop
                    self.stats["streamFrames"] += 1
                    if tail:
                        tail["switched"] = now
                if stream and self.work.qsize() < 4:
                    self.select(stream.popleft(), now)
                    streamed = now
                    if not stream:
                        tail["sent"] = now
                if visit and visit["switched"] is None and self.changed(frame[1], visit["ref"]) > SWITCH_MAD:
                    visit["switched"] = now
                    self.switch_ms.append((now - visit["sent"]) * 1000)
                    if not visit["items"]:
                        self.work.put((visit["player"], True, False, *frame))
                        visit = None
                if visit and visit["switched"] is not None and now - visit["switched"] >= ITEM_SETTLE:
                    self.work.put((visit["player"], True, True, *frame))
                    self.items_queued[visit["player"]] = now
                    visit = None
            if tail and not stream and tail["items"] is None:
                tail = None  # no hero due for items: the next cycle streams on without a pause
            elif tail and not stream and now - tail["sent"] > STREAM_TAIL and now - tail["switched"] >= ITEM_SETTLE:
                # The last streamed selection has had time to show. Its hero is the one due for items:
                # it stays on screen until the item slots have stopped fading.
                self.work.put((tail["items"], False, True, *frame))
                self.items_queued[tail["items"]] = now
                tail = None
            if visit and visit["switched"] is None and now - visit["sent"] > SWITCH_TIMEOUT:
                # Unchanged: the command had no effect, or the hero was already shown. The frame is
                # still read (it has been still for the timeout, so items too); it tells whose HUD it is.
                self.stats["unchanged"] += 1
                self.work.put((visit["player"], False, True, *frame))
                self.items_queued[visit["player"]] = now
                visit = None
            if self.selecting and visit is None and tail is None and self.work.qsize() < 3:
                if not visits and (targets := self.plan(now)):
                    self.stats["cycles"] += 1
                    self.cycle_s.append(now - cycle_start)
                    cycle_start = now
                    # Heroes already matched to their player are streamed, the one due for items last;
                    # unknown players wait for their own switch.
                    # A hero the stream could not read (nothing in the frame names it) gets its own visit,
                    # where the switch itself says whose HUD it is.
                    known = [(p, items) for p, items in targets if p in self.player_hero and p not in self.missed]
                    if len(known) >= 2 and not self.args.no_stream:
                        known.sort(key=lambda t: t[1])
                        stream.extend(p for p, _ in known)
                        tail = {"sent": now, "switched": now, "items": known[-1][0] if known[-1][1] else None}
                        targets = [t for t in targets if t not in known]
                    visits.extend(targets)
                if stream:
                    self.select(stream.popleft(), now)
                    streamed = now
                    if not stream:
                        tail["sent"] = now
                elif visits and now - streamed > STREAM_TAIL:
                    # A visit learns whose HUD its own switch shows, so it waits until the streamed
                    # selections still in flight have shown.
                    p, items = visits.popleft()
                    if len(visits) and self.player_hero.get(p) == self.shown:
                        visits.append((p, items))  # already on screen: visit it after the others
                        p, items = visits.popleft()
                    self.select(p, now)
                    visit = {"player": p, "items": items, "sent": now, "ref": frame[1], "switched": None}
            if now - last_drain > 1:
                self.selector.tick()
                last_drain = now
            if now - last_record >= self.args.timeline_every:
                snap = self.snapshot()
                self.record(snap)
                self.write_live(snap)
                last_record = now
                if now - last_print > 1:
                    self.write_state(snap)
                    if self.args.print:
                        self.print_table(snap)
                    last_print = now
            if got is None:
                time.sleep(0.0003)
        self.work.put(None)

    def select(self, player: int, now: float):
        # Also check already queued selections if the view changes halfway through a sweep.
        with self.feed.lock:
            if (not 0 <= now - self.feed.received < FRESH_GSI
                    or not (self.feed.live_ready or self.feed.mode == "full-spectator" and not self.args.team_view_only)):
                return
        self.tried[player] = now
        self.selector.select(player)

    # ---- recognition (worker thread) ----
    def recognize(self):
        while (job := self.work.get()) is not None:
            player, changed, settled, at, band = job
            t0 = time.perf_counter()
            rows, width = 1080 - BAND_TOP, self.width1080  # the width changes when Dota's window does
            bgr = band[:, :, :3]
            if bgr.shape[0] != rows or bgr.shape[1] != width:
                bgr = cv2.resize(bgr, (width, rows), interpolation=cv2.INTER_AREA)
            if self.canvas.shape[1] != width:
                self.canvas = np.zeros((1080, width, 3), np.uint8)
            self.canvas[BAND_TOP:] = bgr
            with self.feed.lock, self.lock:
                view = self.feed.view_at(at)
                if (view is None or not 0 <= t0 - at < FRESH_GSI or self.feed.mode not in ("team-view", "full-spectator")
                        or self.args.team_view_only and self.feed.mode != "team-view"):
                    continue
                _, clock, visible, _ = view
                epoch, mode, own, teams = self.feed.epoch, self.feed.mode, self.feed.own_team, dict(self.feed.teams)
                self.bind_view(epoch)
                roster = set(teams)
                game_time = self.feed.game_time(at)
                if mode == "team-view" and game_time is None:
                    continue
                expected = self.player_hero.get(player) or leftover(roster, self.player_hero, player)
            out = self.hud.read(self.canvas, roster, full=settled)
            self.read_ms.append((time.perf_counter() - t0) * 1000)
            hero = out.get("hero")
            match = out.get("heroMatch") or {}
            if hero is None and changed and expected and out.get("hud") and (
                    match.get("best") == expected or match.get("blank")):
                # This visit selected one player and the HUD changed: the icons are undecided (all on
                # cooldown, best guess this player's hero) or show nothing (a dead hero's may be blank).
                hero = expected
            entry = {"t": round(at - self.started, 3), "player": player, "changed": changed, "hero": hero, "clock": clock}
            if hero is None:
                kind = "unidentified" if out.get("hud") else "noHud"
                self.stats[kind] += 1
                entry["match"] = out.get("heroMatch")
                if self.stats[kind] <= self.args.save_misses:  # for looking at what the recognizer could not read
                    cv2.imwrite(str(self.args.out / f"{kind}-{self.stats[kind]:03d}.png"), self.canvas[BAND_TOP:])
                self.log.write(json.dumps(entry, ensure_ascii=False) + "\n")
                continue
            status = {"at": at, "clock": clock, "time": game_time, "level": out["level"], "alive": out["alive"],
                      "health": out["health"], "mana": out["mana"]}
            items = settled and {"at": at, "clock": clock, "time": game_time,
                                 **{s: (i and (i["name"] or "?")) for s, i in out["items"].items()},
                                 # items drawn with the same icon (Dagon levels): the name is one of these
                                 "alt": {s: i["sameIcon"] for s, i in out["items"].items() if i and i.get("sameIcon")},
                                 "wards": out["wards"]}
            # In Plus team view an enemy can stay on the HUD after the friend team loses sight of it (on 2026-11-21 Ancient
            # Apparition for 8 s): that is its last sighting, not now. Such reads would date the panel and the ward counts
            # the coach compares as current, so they only name the hero.
            unseen = mode == "team-view" and (hero not in visible or teams.get(hero) not in (2, 3))
            with self.feed.lock, self.lock:
                if epoch != self.feed.epoch:  # view changed during GPU recognition; never relabel the result
                    continue
                # A changed frame that still shows the previous hero (a HUD change other than the
                # switch) must not teach the player's hero, nor one of the other team or another player's.
                if player is not None and changed and hero != self.shown and fits(player, hero, teams, self.player_hero):
                    self.player_hero[player] = hero
                if not unseen:
                    if (last := self.heroes.get(hero, {}).get("status")) is not None:
                        self.refresh_s.append(at - last["at"])
                    record = self.heroes.setdefault(hero, {})
                    record["status"] = status
                    if items:
                        record["items"] = items
                        if mode == "team-view" and teams[hero] != own:
                            self.item_slots.read(hero, items)
                self.shown = hero
            if unseen:
                self.stats["unseen"] += 1
                self.log.write(json.dumps(entry | {"unseen": True, "mode": mode}, ensure_ascii=False) + "\n")
                continue
            self.stats["readings"] += 1
            self.stats["itemReadings"] += bool(items)
            entry |= {k: v for k, v in status.items() if k not in ("at", "clock", "time")}
            if items:
                entry["items"] = {k: v for k, v in items.items() if k not in ("at", "clock", "time")}
                entry["time"] = items["time"]
            if self.args.truth:
                entry["truth"] = self.feed.truth_at(at, hero)
            entry["mode"] = mode
            self.log.write(json.dumps(entry, ensure_ascii=False) + "\n")

    # ---- output ----
    def snapshot(self) -> dict:
        """The match as this spectator knows it now. Ages are seconds since the reading or sighting;
        item lists follow hud_recognizer.SLOTS."""
        with self.feed.lock, self.lock:
            now = time.perf_counter()
            self.bind_view(self.feed.epoch)
            scan_clock = self.feed.game_time(now)
            gsi_age = round(now - self.feed.received, 3) if self.feed.packets else None
            # The coach must not compare charges across an interruption, even within this same scanner process.
            feed = {"mode": self.feed.mode, "clock": self.feed.clock, "matchId": self.feed.match_id, "selecting": self.selecting,
                    "ownTeam": self.feed.own_team, "gsiAge": gsi_age, "run": f"{self.run_id}-{self.epoch}",
                    "scanClock": scan_clock, "sampledUnix": round(time.time(), 3),
                    "liveReady": self.feed.live_ready and scan_clock is not None and gsi_age is not None and 0 <= gsi_age < FRESH_GSI
                                 and not self.args.truth and self.args.select != "console"}
            seen, visible, teams = dict(self.feed.seen), set(self.feed.visible), dict(self.feed.teams)
            truth = self.feed.truth[-1][1] if self.args.truth and self.feed.truth else None
            heroes, players = {h: dict(r) for h, r in self.heroes.items()}, dict(self.player_hero)
            shown = dict(self.item_slots.shown)
            item_ages = {h: round(max(0, scan_clock - at), 2) for h, at in self.item_slots.shown_time.items()} if scan_clock is not None else {}
            wards = {h: [dict(e) for e in r] for h, r in self.item_slots.wards.items()}
        age = lambda r: round(now - r["at"], 2)
        out = {}
        for hero in sorted(set(heroes) | set(seen) | set(teams)):
            h, s = heroes.get(hero) or {}, seen.get(hero)
            st, it, team = h.get("status"), h.get("items"), teams.get(hero)
            out[hero] = {
                "team": team, "ally": None if feed["ownTeam"] is None or team is None else team == feed["ownTeam"],
                "status": st and {"level": st["level"], "alive": st["alive"], "health": pair(st["health"]),
                                  "mana": pair(st["mana"]), "clock": st["clock"], "time": st["time"], "age": age(st)},
                "items": it and {"slots": [it[s] for s in SLOTS], **({"alt": it["alt"]} if it["alt"] else {}),
                                 "clock": it["clock"], "age": age(it)},
                "seen": s and {"x": s["x"], "y": s["y"], **({"others": s["others"]} if "others" in s else {}),
                               "visible": hero in visible, "clock": s["clock"], "age": age(s)},
                **({"truth": truth[hero]} if truth and hero in truth else {}),
            }
        snap = {**feed, "players": {str(p): h for p, h in sorted(players.items())}, "heroes": out}
        snap["enemies"] = self.enemies(snap, shown, item_ages, wards)
        return snap

    def write_state(self, snap: dict):
        if self.args.team_view_only:
            return
        path = self.args.out / "state.json"
        tmp = path.with_suffix(".tmp")
        tmp.write_text(json.dumps(snap, ensure_ascii=False, indent=1), encoding="utf-8")
        os.replace(tmp, path)

    @staticmethod
    def enemies(snap: dict, shown: dict, item_ages: dict, wards: dict) -> list[dict]:
        """The enemy panel on the game machine (Plus team view only), in player order: level and items as
        last read while the friend team saw the hero, and the age of that reading in seconds. Never-read
        enemies are listed with nulls."""
        if (snap["mode"] != "team-view" or snap["ownTeam"] not in (2, 3) or not snap["liveReady"]
                or snap["gsiAge"] is None or not 0 <= snap["gsiAge"] < FRESH_GSI):
            return []
        order = {hero: int(player) for player, hero in snap["players"].items()}
        heroes = sorted((h for h, s in snap["heroes"].items() if s["team"] in (2, 3) and s["team"] != snap["ownTeam"]),
                        key=lambda h: (order.get(h, 99), h))[:5]
        out = []
        for hero in heroes:
            status = snap["heroes"][hero]["status"]
            slots = shown.get(hero) if hero in item_ages else None
            level = status and status["level"]
            age = round(max(0, snap["scanClock"] - status["time"]), 2) if level is not None else None
            if slots is not None:
                age = max(age or 0, item_ages[hero])  # a fast level read doesn't make the older gear fresh
            out.append({"hero": hero, "level": level, "age": age,
                        "items": slots and list(slots[:6]), "neutral": slots and slots[6],
                        **({"wards": wards[hero]} if hero in wards else {})})
        return out

    def export_icons(self, enemies: list[dict]):
        """Copies the panel's hero and item icons next to the live file, where the coach client serves them to
        the game machine. Valve artwork stays on the user's own machines."""
        folder = self.args.live.parent / "icons"
        keys = {e["hero"] for e in enemies} | {n for e in enemies for n in (e["items"] or []) + [e["neutral"]] if n}
        for key in sorted(keys - self.exported):
            icon = (self.hud.manifest["heroes" if key.startswith("npc_dota_hero_") else "items"].get(key) or {}).get("icon")
            if icon:
                folder.mkdir(parents=True, exist_ok=True)
                try:
                    shutil.copyfile(self.args.assets / icon, folder / f"{key}.png")
                except PermissionError:
                    continue  # the coach client is reading the old copy: tried again with the next write
            self.exported.add(key)

    def write_live(self, snap: dict, stopped: bool = False):
        """Scanner status and the enemy panel for the coach client, replaced atomically."""
        if not self.args.live:
            return
        mode = snap["mode"]
        if mode == "team-view" and (not snap["liveReady"] or snap["gsiAge"] is None or not 0 <= snap["gsiAge"] < FRESH_GSI):
            mode = "waiting"
        enemies = snap["enemies"] if not stopped and mode == "team-view" else []
        self.export_icons(enemies)
        live = {"version": 2, "run": snap["run"], "written": snap["sampledUnix"], "stopped": stopped, "mode": mode, "gsiAge": snap["gsiAge"],
                "matchId": snap["matchId"], "ownTeam": snap["ownTeam"], "clock": snap["scanClock"], "selecting": snap["selecting"],
                "enemies": enemies}
        self.args.live.parent.mkdir(parents=True, exist_ok=True)
        tmp = self.args.live.with_suffix(".tmp")
        tmp.write_text(json.dumps(live, ensure_ascii=False), encoding="utf-8")
        # Windows refuses to replace a file another process has open: the coach client (before 2026-11-21 without
        # sharing it for delete) may be reading it this moment. Retry briefly, else skip; the next write is 0.5 s away.
        for _ in range(5):
            try:
                os.replace(tmp, self.args.live)
                return
            except PermissionError:
                time.sleep(0.02)

    def record(self, snap: dict):
        if self.args.team_view_only:
            return
        snap = {"type": "snapshot", "t": round(time.perf_counter() - self.started, 2), "cycle": self.cycle_now(), **snap}
        self.timeline.write(json.dumps(snap, ensure_ascii=False, separators=(",", ":")) + "\n")
        self.timeline.flush()

    def cycle_now(self) -> float | None:
        recent = self.refresh_s[-30:]
        return round(float(np.median(recent)), 3) if recent else None

    def print_table(self, snap: dict):
        zh = lambda n: (self.hud.manifest["items"].get(n) or {}).get("nameZh") or n
        lines = [f"-- {snap['mode']} clock {snap['clock']} own team {snap['ownTeam']}  readings {self.stats['readings']}"
                 f"  switch {np.median(self.switch_ms or [0]):.0f} ms  hero refresh {self.cycle_now()} s"]
        for hero, s in snap["heroes"].items():
            st, it, seen = s["status"], s["items"], s["seen"]
            status = "-" if not st else (f"Lv{st['level']} HP {fmt(st['health'])} MP {fmt(st['mana'])}"
                                         f"{'' if st['alive'] else ' 死亡'} ({st['age']:.1f}s)")
            items = "" if not it else " ".join(zh(i) for i in it["slots"][:6] if i) + f" ({it['age']:.0f}s)"
            where = "未见" if not seen else f"({seen['x']},{seen['y']}) " + ("可见" if seen["visible"] else f"{seen['age']:.0f}s前")
            side = "?" if s["ally"] is None else "友" if s["ally"] else "敌"
            name = (self.hud.manifest["heroes"].get(hero) or {}).get("nameZh") or hero
            lines.append(f"{side} {name:8s} {status} {items} | {where}")
        print("\n".join(lines), flush=True)

    def summary(self) -> dict:
        elapsed = time.perf_counter() - self.started
        q = lambda xs, p, digits=1: round(float(np.percentile(xs, p)), digits) if xs else None
        return {"seconds": round(elapsed, 1), **self.stats, "readingsPerSecond": round(self.stats["readings"] / elapsed, 1),
                "switchMs": {"p10": q(self.switch_ms, 10), "p50": q(self.switch_ms, 50), "p90": q(self.switch_ms, 90)},
                "cycleSeconds": {"p50": q(self.cycle_s[1:], 50, 3), "p90": q(self.cycle_s[1:], 90, 3)},
                "heroRefreshSeconds": {"p50": q(self.refresh_s, 50, 3), "p90": q(self.refresh_s, 90, 3)},
                "readMs": {"p50": q(self.read_ms, 50), "p90": q(self.read_ms, 90)}, "players": self.player_hero}


def pair(v):
    return v and [v["current"], v["max"]]


def fmt(v):
    return f"{v[0]}/{v[1]}" if v else "?"


def load_secrets(path: Path, create: bool) -> dict:
    if path.exists():
        return json.loads(path.read_text(encoding="utf-8"))
    if not create:
        raise SystemExit(f"No secrets at {path}; run `setup` first")
    data = {"consolePassword": secrets.token_hex(16), "gsiToken": secrets.token_hex(16)}
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(data), encoding="utf-8")
    return data


def setup(args):
    data = load_secrets(args.secrets, create=True)
    path = args.install / "game/dota/cfg/gamestate_integration" / GSI_NAME
    # hero and items stay empty in Plus team view; delayed and replay spectating fill them for --truth.
    body = (f'"coach_hero_scan"\n{{\n "uri" "http://127.0.0.1:{args.gsi_port}/"\n "timeout" "5.0"\n "buffer" "0.0"\n'
            f' "throttle" "0.1"\n "heartbeat" "1.0"\n "auth" {{ "token" "{data["gsiToken"]}" }}\n "data"\n {{\n'
            + "".join(f'  "{k}" "1"\n' for k in ("provider", "map", "player", "hero", "items", "buildings", "minimap")) + " }\n}\n")
    if path.exists():
        if path.read_text(encoding="utf-8") != body:
            raise SystemExit(f"Existing scanner GSI config differs; back it up and resolve it manually: {path}")
        print("GSI config already current:", path)
    else:
        path.write_text(body, encoding="utf-8")
        print("Wrote", path, "- restart Dota once so it is loaded.")
    print("Steam launch options for Dota 2 need -gamestateintegration. Live spectating selects heroes with the")
    print("spectator hotkeys (--select keys), which needs nothing else. Local replays can use the console instead:")
    print(f"  dota2.exe -insecure -gamestateintegration -netconport {args.console_port} -netconpassword {data['consolePassword']}")
    print("(-netconport is ignored without -insecure; the port listens on all interfaces and every connection needs the password.)")


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("command", choices=["setup", "scan"])
    ap.add_argument("--install", type=Path, default=INSTALL)
    ap.add_argument("--secrets", type=Path, default=SECRETS)
    ap.add_argument("--live", type=lambda v: Path(v) if v else None, default=LIVE,
                    help="status and the enemy panel for the coach client (empty: do not write)")
    ap.add_argument("--select", choices=["keys", "console"], default="keys",
                    help="keys: spectator hotkeys (live spectating); console: -netconport under -insecure (replays)")
    ap.add_argument("--keys", default="1234567890", help="hotkeys of SpectatorCameraFocusPlayer1-10")
    ap.add_argument("--console-port", type=int, default=29009)
    ap.add_argument("--gsi-port", type=int, default=3298)
    ap.add_argument("--out", type=Path, default=Path(".tmp/hero-scan"), help="sessions go to <out>/<session>")
    ap.add_argument("--session", help="session folder name (default: start time)")
    ap.add_argument("--timeline-every", type=float, default=0.5, help="seconds between timeline snapshots")
    ap.add_argument("--duration", type=float, default=3600)
    ap.add_argument("--assets", type=Path, default=Path(".tmp/vision-assets"))
    ap.add_argument("--calibration", type=Path, default=Path(".tmp/vision-assets/hud-1080.npz"))
    ap.add_argument("--print", action="store_true", help="print the hero table every second")
    ap.add_argument("--truth", action="store_true", help="log full-spectator GSI with each reading (replay evaluation)")
    ap.add_argument("--item-visits", type=int, default=1, help="heroes per cycle that stay long enough for items")
    ap.add_argument("--allies", action="store_true",
                    help="in Plus team view also select the friend team's heroes (by default only enemies are scanned)")
    ap.add_argument("--team-view-only", action="store_true", help="coach launcher: only Plus live output, no unbound session archive")
    ap.add_argument("--save-misses", type=int, default=0, help="save this many HUD bands that could not be read")
    ap.add_argument("--no-stream", action="store_true", help="wait for every switch instead of one selection per frame")
    ap.add_argument("--items-every", type=float, default=3.0,
                    help="seconds between item reads of the same hero; each read holds the HUD ~0.15 s")
    ap.add_argument("--ward-items-every", type=float, default=0.4,
                    help="the same for an enemy carrying wards (Plus team view), so a ward it places is timed closely")
    ap.add_argument("--stop-with-stdin", action="store_true",
                    help="end the scan when standard input closes (how the coach client starts and stops it)")
    args = ap.parse_args()
    sys.stdout.reconfigure(encoding="utf-8")
    if args.command == "setup":
        return setup(args)
    if args.team_view_only:
        if args.truth or args.select == "console":
            ap.error("--team-view-only cannot be combined with replay/evaluation modes")
        args.save_misses = 0
    elif args.truth or args.select == "console":
        args.live = None  # a replay/evaluation must not even replace the live scanner's file
    if args.stop_with_stdin:
        threading.Thread(target=stop_when_stdin_closes, daemon=True).start()
    data = load_secrets(args.secrets, create=False)
    session = args.session or time.strftime("%Y%m%d-%H%M%S")
    if not re.fullmatch(r"[A-Za-z0-9_-]{1,64}", session):
        raise SystemExit("Session names use letters, digits, - and _")
    args.out = args.out / session
    if not args.team_view_only:
        args.out.mkdir(parents=True, exist_ok=True)
    from hud_recognizer import HudRecognizer
    hud = HudRecognizer(args.assets, args.calibration)
    feed = GsiFeed(args.gsi_port, data["gsiToken"], args.truth, args.team_view_only)
    if args.select == "console":
        selector = lambda window: ConsoleSelect(data["consolePassword"], args.console_port)
    else:
        def selector(window):
            keys = KeySelect(window, args.keys)
            if not keys.ready() and (args.team_view_only or not keys.front()):
                print("Dota is not in the foreground: watching only until it is.", flush=True)
            return keys
    announced = False
    while True:  # may be started before Dota
        if STOP.is_set():
            feed.shutdown()
            return
        try:
            dota_window()
            break
        except RuntimeError:
            if not announced:
                print("Waiting for the Dota window...", flush=True)
                announced = True
            time.sleep(1)
    scanner = Scanner(args, hud, feed, selector)
    worker = threading.Thread(target=scanner.recognize)
    worker.start()
    try:
        scanner.run(args.duration)
    except KeyboardInterrupt:
        pass
    finally:
        # Also on an error: the worker waits for this, and the run would otherwise hang half-stopped.
        scanner.work.put(None)
        worker.join()
        snap = scanner.snapshot()
        scanner.record(snap)
        scanner.write_state(snap)
        scanner.write_live(snap, stopped=True)
        scanner.capture.close()
        feed.shutdown()
        scanner.log.close(); scanner.timeline.close()
        if args.team_view_only:
            print("Vision scan stopped.", flush=True)
        else:
            summary = json.dumps(scanner.summary(), ensure_ascii=False)
            (args.out / "summary.json").write_text(summary, encoding="utf-8")
            print(summary, flush=True)


if __name__ == "__main__":
    main()
