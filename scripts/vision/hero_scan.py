"""Live hero scanner for the coach machine's Dota spectator view.

Cycles the spectator's selected hero and reads each hero's bottom HUD with hud_recognizer.py: level,
health, mana, alive and items. Whose HUD a frame shows is read from the frame (ability icons, else
the name label), not assumed from the selection. Where each hero was last seen comes from the same
client's GSI minimap.

Selection: `keys` posts the spectator hotkeys (SpectatorCameraFocusPlayer1-10, 1-0 by default) to
the Dota window. Dota acts on them only while it is the foreground window, so the scanner brings it to
the front once at start and otherwise just watches; posted keys never reach another program. `console`
sends `dota_camera_focus_player <id>` through -netconport, which Dota opens only with -insecure: for
local replays, not for live spectating.

It uses only what this spectator's own screen and GSI show. In Dota Plus live spectating that is the
friend team's vision: enemies are scanned while that team sees them and otherwise keep their last
observed state with its age. Delayed or replay spectating shows both teams in full; the state file
says so in "mode", and live coaching must not use such data.

Speed: heroes already matched to their player are selected one per captured frame; Dota shows a new
selection about every 20 ms, at 60 Hz and 144 Hz alike, so a sweep of ten heroes takes ~0.2 s. A hero
whose items were read --items-every seconds ago (3 by default) stays ~0.13 s at the end of a sweep, as
item slots cross-fade after a switch; sweeps without one follow each other without a pause.
Recognition runs in parallel on the GPU, on a HUD band that Windows.Graphics.Capture (wgc.py) crops on
the GPU.

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
import sys
import threading
import time
from pathlib import Path

import cv2
import numpy as np

sys.path.insert(0, str(Path(__file__).resolve().parent))  # python -I leaves the script folder off the path
from capture_replay import Console, INSTALL  # noqa: E402
from hud_recognizer import SLOTS  # noqa: E402
import wgc  # noqa: E402

GSI_NAME = "gamestate_integration_coach_hero_scan.cfg"
SECRETS = Path(os.environ.get("LOCALAPPDATA", ".")) / "DotaCoach" / "hero-scan" / "secrets.json"
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

    def __init__(self, port: int, token: str, keep_truth: bool):
        self.token, self.keep_truth, self.lock = token, keep_truth, threading.Lock()
        self.clock = self.state = self.match_id = self.own_team = None
        self.mode, self.packets, self.received = "none", 0, 0.0
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
                if isinstance(payload, dict) and (payload.get("auth") or {}).get("token") == feed.token:
                    feed.update(payload, time.perf_counter())

            def log_message(self, *_):
                pass

        super().__init__(("127.0.0.1", port), Handler)
        threading.Thread(target=self.serve_forever, daemon=True).start()

    def update(self, p: dict, now: float):
        game = p.get("map") or {}
        player = p.get("player") or {}
        grouped = any(re.fullmatch(r"team\d", k) for k in player)
        mode = "menu" if not game else "full-spectator" if grouped else \
            "player" if player.get("team_name") in ("radiant", "dire") else "team-view"
        units = [u for u in (p.get("minimap") or {}).values() if isinstance(u, dict)
                 and str(u.get("unitname", "")).startswith("npc_dota_hero_")]
        with self.lock:
            if game.get("matchid") != self.match_id:
                self.seen, self.teams, self.own_team = {}, {}, None
            self.match_id, self.clock, self.state = game.get("matchid"), game.get("clock_time"), game.get("game_state")
            self.mode, self.packets, self.received = mode, self.packets + 1, now
            visible = collections.defaultdict(list)
            for u in units:
                self.teams[u["unitname"]] = u.get("team")
                if u.get("image") == "minimap_herocircle_self" and u.get("team") in (2, 3):
                    self.own_team = u["team"]
                if (u.get("visionrange") or 0) > 0:
                    visible[u["unitname"]].append([u.get("xpos"), u.get("ypos")])
            if mode == "player":
                self.own_team = {"radiant": 2, "dire": 3}[player["team_name"]]
            elif self.own_team is None and mode == "team-view" and len(p.get("buildings") or {}) == 1:
                self.own_team = 2 if "radiant" in p["buildings"] else 3
            for hero, spots in visible.items():
                self.seen[hero] = {"x": spots[0][0], "y": spots[0][1], **({"others": spots[1:]} if len(spots) > 1 else {}),
                                   "clock": self.clock, "at": now}
            self.visible = set(visible)
            if self.keep_truth and grouped:
                self.truth.append((now, truth_of(p)))

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
        self.selector = selector(self.window)
        self.selecting = True  # False while Dota ignores selections (keys: not in the foreground)
        self.capture = wgc.WindowCapture(self.window, lambda w, h: (0, h * BAND_TOP // 1080, w, h - h * BAND_TOP // 1080))
        cw, ch = self.capture.client
        self.width1080 = round(cw * 1080 / ch)
        self.canvas = np.zeros((1080, self.width1080, 3), np.uint8)  # used by the worker thread only
        s = self.capture.crop[3] / (1080 - BAND_TOP)
        r0, r1, c0, c1 = CHANGE
        self.change_rows = slice(round((r0 - BAND_TOP) * s), round((r1 - BAND_TOP) * s), 4)
        self.change_cols = slice(cw // 2 + round(c0 * s), cw // 2 + round(c1 * s), 4)
        self.player_hero: dict[int, str] = {}
        self.tried: dict[int, float] = {}
        self.heroes: dict[str, dict] = {}  # hero -> {"status": newest reading, "items": newest settled reading}
        self.shown: str | None = None  # hero the HUD showed last
        self.match_id = None
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
        self.log = open(args.out / "scan.jsonl", "a", encoding="utf-8")
        self.timeline = open(args.out / "timeline.jsonl", "a", encoding="utf-8")
        names = lambda kind: {k: v.get("nameZh") for k, v in hud.manifest[kind].items() if v.get("nameZh")}
        self.timeline.write(json.dumps({"type": "header", "started": time.strftime("%Y-%m-%dT%H:%M:%S%z"), "slots": list(SLOTS),
                                        "truth": args.truth, "every": args.timeline_every, "names": {"heroes": names("heroes"), "items": names("items")}},
                                       ensure_ascii=False, separators=(",", ":")) + "\n")
        self.started = time.perf_counter()

    # ---- controller: capture and selection (main thread) ----
    def plan(self, now: float) -> list[tuple[int, bool]]:
        """One cycle: (player, item visit) for every hero that can be selected now. The heroes whose
        items were read longest ago (at most --item-visits) stay long enough for their items."""
        with self.feed.lock:
            mode, own, visible, teams = self.feed.mode, self.feed.own_team, set(self.feed.visible), dict(self.feed.teams)
            match_id = self.feed.match_id
        with self.lock:
            if match_id != self.match_id:  # another match: nothing known carries over
                self.match_id, self.player_hero, self.heroes, self.shown = match_id, {}, {}, None
                self.items_queued = {}
            players = dict(self.player_hero)
            items_at = {h: (r.get("items") or {}).get("at", -1e9) for h, r in self.heroes.items()}
            status_at = {h: (r.get("status") or {}).get("at", -1e9) for h, r in self.heroes.items()}
        out = []
        for p in range(10):
            hero = players.get(p)
            if hero is None:
                if now - self.tried.get(p, -1e9) >= RETRY_UNKNOWN:
                    out.append(p)
            elif mode != "team-view" or teams.get(hero) == own or hero in visible:
                out.append(p)
        self.missed = {p for p in out if p in players and now - status_at.get(players[p], -1e9) > STREAM_MISSED}
        stale = sorted((max(items_at.get(players[p], -1e9), self.items_queued.get(p, -1e9)), p) for p in out if p in players)
        slow = {p for at, p in stale[:self.args.item_visits] if now - at >= self.args.items_every}
        return [(p, p in slow) for p in out]

    def changed(self, band: np.ndarray, reference: np.ndarray) -> float:
        a = band[self.change_rows, self.change_cols, :3].astype(np.int16)
        return float(np.abs(a - reference[self.change_rows, self.change_cols, :3]).mean())

    def run(self, duration: float):
        frame = None
        while frame is None:
            frame = self.capture.latest()
            time.sleep(0.001)
        visit = None  # closed-loop visit: {"player", "items", "sent", "ref", "switched"}
        stream: collections.deque[int] = collections.deque()  # players selected one per captured frame
        tail = None  # after the last streamed selection: {"sent", "switched", "items"}
        streamed = -1e9  # when the last streamed selection was sent
        visits: collections.deque[tuple[int, bool]] = collections.deque()
        last_print = last_drain = last_record = cycle_start = self.started
        end = self.started + duration
        while time.perf_counter() < end:
            got = self.capture.latest()
            now = time.perf_counter()
            if self.selecting != (ready := self.selector.ready()):
                self.selecting = ready
                if not ready:  # abandon the cycle; until Dota acts on selections again, only watch
                    stream.clear(); visits.clear()
                    visit = tail = None
            if got is not None:
                previous, frame = frame, got
                if (stream or tail or not self.selecting) and self.changed(frame[1], previous[1]) > SWITCH_MAD:
                    self.work.put((None, True, False, now, frame[1]))  # the frame says whose HUD it is
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
                        self.work.put((visit["player"], True, False, now, frame[1]))
                        visit = None
                if visit and visit["switched"] is not None and now - visit["switched"] >= ITEM_SETTLE:
                    self.work.put((visit["player"], True, True, now, frame[1]))
                    self.items_queued[visit["player"]] = now
                    visit = None
            if tail and not stream and tail["items"] is None:
                tail = None  # no hero due for items: the next cycle streams on without a pause
            elif tail and not stream and now - tail["sent"] > STREAM_TAIL and now - tail["switched"] >= ITEM_SETTLE:
                # The last streamed selection has had time to show. Its hero is the one due for items:
                # it stays on screen until the item slots have stopped fading.
                self.work.put((tail["items"], False, True, now, frame[1]))
                self.items_queued[tail["items"]] = now
                tail = None
            if visit and visit["switched"] is None and now - visit["sent"] > SWITCH_TIMEOUT:
                # Unchanged: the command had no effect, or the hero was already shown. The frame is
                # still read (it has been still for the timeout, so items too); it tells whose HUD it is.
                self.stats["unchanged"] += 1
                self.work.put((visit["player"], False, True, now, frame[1]))
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
        self.tried[player] = now
        self.selector.select(player)

    # ---- recognition (worker thread) ----
    def recognize(self):
        while (job := self.work.get()) is not None:
            player, changed, settled, at, band = job
            t0 = time.perf_counter()
            rows = 1080 - BAND_TOP
            bgr = band[:, :, :3]
            if bgr.shape[0] != rows or bgr.shape[1] != self.width1080:
                bgr = cv2.resize(bgr, (self.width1080, rows), interpolation=cv2.INTER_AREA)
            self.canvas[BAND_TOP:] = bgr
            with self.feed.lock:
                clock, mode, roster = self.feed.clock, self.feed.mode, set(self.feed.teams)
            out = self.hud.read(self.canvas, roster, full=settled)
            self.read_ms.append((time.perf_counter() - t0) * 1000)
            hero = out.get("hero")
            with self.lock:
                expected = self.player_hero.get(player) or leftover(roster, self.player_hero, player)
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
            status = {"at": at, "clock": clock, "level": out["level"], "alive": out["alive"],
                      "health": out["health"], "mana": out["mana"]}
            items = settled and {"at": at, "clock": clock, **{s: (i and (i["name"] or "?")) for s, i in out["items"].items()},
                                 # items drawn with the same icon (Dagon levels): the name is one of these
                                 "alt": {s: i["sameIcon"] for s, i in out["items"].items() if i and i.get("sameIcon")}}
            with self.lock:
                # A changed frame that still shows the previous hero (a HUD change other than the
                # switch) must not teach the player's hero.
                if player is not None and changed and hero != self.shown:
                    self.player_hero[player] = hero
                if (last := self.heroes.get(hero, {}).get("status")) is not None:
                    self.refresh_s.append(at - last["at"])
                record = self.heroes.setdefault(hero, {})
                record["status"] = status
                if items:
                    record["items"] = items
            self.shown = hero
            self.stats["readings"] += 1
            self.stats["itemReadings"] += bool(items)
            entry |= {k: v for k, v in status.items() if k not in ("at", "clock")}
            if items:
                entry["items"] = {k: v for k, v in items.items() if k not in ("at", "clock")}
            if self.args.truth:
                entry["truth"] = self.feed.truth_at(at, hero)
            entry["mode"] = mode
            self.log.write(json.dumps(entry, ensure_ascii=False) + "\n")

    # ---- output ----
    def snapshot(self) -> dict:
        """The match as this spectator knows it now. Ages are seconds since the reading or sighting;
        item lists follow hud_recognizer.SLOTS."""
        now = time.perf_counter()
        with self.feed.lock:
            feed = {"mode": self.feed.mode, "clock": self.feed.clock, "matchId": self.feed.match_id, "selecting": self.selecting,
                    "ownTeam": self.feed.own_team, "gsiAge": round(now - self.feed.received, 2) if self.feed.packets else None}
            seen, visible, teams = dict(self.feed.seen), set(self.feed.visible), dict(self.feed.teams)
            truth = self.feed.truth[-1][1] if self.args.truth and self.feed.truth else None
        with self.lock:
            heroes, players = {h: dict(r) for h, r in self.heroes.items()}, dict(self.player_hero)
        age = lambda r: round(now - r["at"], 2)
        out = {}
        for hero in sorted(set(heroes) | set(seen) | set(teams)):
            h, s = heroes.get(hero) or {}, seen.get(hero)
            st, it, team = h.get("status"), h.get("items"), teams.get(hero)
            out[hero] = {
                "team": team, "ally": None if feed["ownTeam"] is None or team is None else team == feed["ownTeam"],
                "status": st and {"level": st["level"], "alive": st["alive"], "health": pair(st["health"]),
                                  "mana": pair(st["mana"]), "clock": st["clock"], "age": age(st)},
                "items": it and {"slots": [it[s] for s in SLOTS], **({"alt": it["alt"]} if it["alt"] else {}),
                                 "clock": it["clock"], "age": age(it)},
                "seen": s and {"x": s["x"], "y": s["y"], **({"others": s["others"]} if "others" in s else {}),
                               "visible": hero in visible, "clock": s["clock"], "age": age(s)},
                **({"truth": truth[hero]} if truth and hero in truth else {}),
            }
        return {**feed, "players": {str(p): h for p, h in sorted(players.items())}, "heroes": out}

    def write_state(self, snap: dict):
        path = self.args.out / "state.json"
        tmp = path.with_suffix(".tmp")
        tmp.write_text(json.dumps(snap, ensure_ascii=False, indent=1), encoding="utf-8")
        os.replace(tmp, path)

    def record(self, snap: dict):
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
    if path.exists() and path.read_text(encoding="utf-8") == body:
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
    ap.add_argument("--save-misses", type=int, default=0, help="save this many HUD bands that could not be read")
    ap.add_argument("--no-stream", action="store_true", help="wait for every switch instead of one selection per frame")
    ap.add_argument("--items-every", type=float, default=3.0,
                    help="seconds between item reads of the same hero; each read holds the HUD ~0.15 s")
    args = ap.parse_args()
    sys.stdout.reconfigure(encoding="utf-8")
    if args.command == "setup":
        return setup(args)
    data = load_secrets(args.secrets, create=False)
    session = args.session or time.strftime("%Y%m%d-%H%M%S")
    if not re.fullmatch(r"[A-Za-z0-9_-]{1,64}", session):
        raise SystemExit("Session names use letters, digits, - and _")
    args.out = args.out / session
    args.out.mkdir(parents=True, exist_ok=True)
    from hud_recognizer import HudRecognizer
    hud = HudRecognizer(args.assets, args.calibration)
    feed = GsiFeed(args.gsi_port, data["gsiToken"], args.truth)
    if args.select == "console":
        selector = lambda window: ConsoleSelect(data["consolePassword"], args.console_port)
    else:
        def selector(window):
            keys = KeySelect(window, args.keys)
            if not keys.ready() and not keys.front():
                print("Dota is not in the foreground: watching only until it is.", flush=True)
            return keys
    scanner = Scanner(args, hud, feed, selector)
    worker = threading.Thread(target=scanner.recognize)
    worker.start()
    try:
        scanner.run(args.duration)
    except KeyboardInterrupt:
        scanner.work.put(None)
    finally:
        worker.join()
        snap = scanner.snapshot()
        scanner.record(snap)
        scanner.write_state(snap)
        scanner.capture.close()
        feed.shutdown()
        summary = json.dumps(scanner.summary(), ensure_ascii=False)
        (args.out / "summary.json").write_text(summary, encoding="utf-8")
        print(summary, flush=True)


if __name__ == "__main__":
    main()
