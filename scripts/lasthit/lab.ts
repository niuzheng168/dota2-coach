// Last-hit lab runner: our OWN local Dota (-insecure, LAN only) playing the coach_lasthit custom game (scripts/lasthit/addon,
// copied into game/dota_addons for the run and removed afterwards). Every [COACH_LASTHIT] record goes to a JSONL trace for
// the timing evaluation (DotaCoach.TwoPc.Tests --last-hit-eval). Never attaches to a user's game: refuses to start while
// any Dota runs, quits only the game it started, restores video.txt.
//   node scripts/lasthit/lab.ts OUT.jsonl [--hero npc_dota_hero_luna] [--level 1] [--rounds 60] [--timescale 4] [--seed 1]
//     [--size 1920x1080] [--install DIR]
// --loop: the product end to end, in real time. The game client (Release build) reads the Dota window and sends every number
// turning red, by both rules, to this script; a simulated player reacts after N(--reaction 0.2, 0.04) s and the custom game's
// coach_attack command (server side, this sandbox only) orders the hero's attack. Odd rounds follow the look-ahead rule
// (--lead, --latency, --singles, --patient), even rounds the this-frame rule; the engine judges every attack.
// --frames DIR: record mode in real time while the game client saves pictures of the Dota window into DIR (LabFrames), for
// training a model on creep attacks; OUT.sync.jsonl pairs server times with this machine's QPC clock (the pictures' clock).
import fs from 'node:fs/promises';
import { createWriteStream } from 'node:fs';
import dgram from 'node:dgram';
import net from 'node:net';
import path from 'node:path';
import { randomBytes } from 'node:crypto';
import { spawn, execFileSync, type ChildProcess } from 'node:child_process';

const args = process.argv.slice(2);
const option = (name: string, fallback: string) => { const i = args.indexOf(name); return i >= 0 && i + 1 < args.length ? args[i + 1] : fallback; };
if (!args[0] || args[0].startsWith('--')) throw new Error('Usage: node scripts/lasthit/lab.ts OUT.jsonl [--loop] [--hero NAME] [--level N] [--rounds N] [--timescale X]');
const out = path.resolve(args[0]);
const loop = args.includes('--loop');
const frames = args.includes('--frames') ? path.resolve(option('--frames', '')) : null;
const hero = option('--hero', 'npc_dota_hero_luna');
const level = Number(option('--level', '1')), rounds = Number(option('--rounds', '60'));
const timescale = loop || frames ? 1 : Number(option('--timescale', '4'));
const seed = Number(option('--seed', '1'));
const reaction = Number(option('--reaction', '0.2')), lead = option('--lead', '0.15'), latency = option('--latency', '0.08');
const [width, height] = option('--size', '1920x1080').split('x').map(Number);
if (!/^npc_dota_hero_[a-z0-9_]{1,40}$/.test(hero) || !(level >= 1 && level <= 30) || !(rounds >= 1 && rounds <= 1000) ||
    !(timescale >= 1 && timescale <= 10) || !Number.isInteger(seed) || !(width >= 640 && height >= 480) || !(reaction >= 0 && reaction < 1) ||
    !/^\d*\.?\d+$/.test(lead) || !/^\d*\.?\d+$/.test(latency) || loop && frames)
  throw new Error('Bad --hero, --level, --rounds, --timescale, --seed, --size, --reaction, --lead or --latency (or --loop with --frames)');
const install = path.resolve(option('--install', 'C:/Steam/steamapps/common/dota 2 beta'));
const exe = path.join(install, 'game/bin/win64/dota2.exe');
const addon = path.join(install, 'game/dota_addons/coach_lasthit');
const userdata = path.resolve(install, '../../../userdata');
const gameClient = path.resolve(import.meta.dirname, '../../desktop/DotaCoach.GameClient/bin/Release/net10.0-windows/DotaCoach.GameClient.exe');
const exists = (p: string) => fs.stat(p).then(() => true, () => false);
const sleep = (ms: number) => new Promise(r => setTimeout(r, ms));
const qpc = () => Number(process.hrtime.bigint()) / 1e9;
const base = out.replace(/\.jsonl$/, '');

const running = () => execFileSync('tasklist', ['/FI', 'IMAGENAME eq dota2.exe', '/NH'], { encoding: 'utf8', windowsHide: true }).includes('dota2.exe');
if (process.platform !== 'win32') throw new Error('Windows only');
if (running()) throw new Error('Dota is running. The lab starts its own Dota only; close the game first.');
if (await exists(out)) throw new Error(`${out} exists; keep it`);
if (await exists(addon)) throw new Error(`${addon} exists (an earlier run left it?); check and remove it first`);
if ((loop || frames) && !await exists(gameClient)) throw new Error(`Build the game client first: ${gameClient}`);
if (frames && await exists(frames)) throw new Error(`${frames} exists; keep it`);

/** Line-oriented client for the game's -netconport console. */
class Console {
  private buffer = '';
  private listeners = new Set<(line: string) => void>();
  readonly socket: net.Socket;
  constructor(socket: net.Socket) {
    this.socket = socket;
    socket.setEncoding('utf8');
    socket.on('data', (chunk: string) => {
      this.buffer += chunk;
      const lines = this.buffer.split('\n'); this.buffer = lines.pop()!;
      for (const line of lines) for (const listen of this.listeners) listen(line.replace(/\r$/, ''));
    });
    socket.on('error', () => {});
  }
  static connect(port: number, password: string) {
    return new Promise<Console>((resolve, reject) => {
      const socket = net.createConnection({ host: '127.0.0.1', port });
      socket.once('connect', () => { const c = new Console(socket); c.send(`PASS ${password}`); resolve(c); });
      socket.once('error', reject);
    });
  }
  send(...commands: string[]) { for (const c of commands) this.socket.write(c + '\n'); }
  on(listen: (line: string) => void) { this.listeners.add(listen); return () => this.listeners.delete(listen); }
  wait(pattern: RegExp, ms: number) {
    return new Promise<string | null>(resolve => {
      const timer = setTimeout(() => { off(); resolve(null); }, ms);
      const off = this.on(line => { if (pattern.test(line)) { clearTimeout(timer); off(); resolve(line); } });
    });
  }
  async answers() {
    const marker = 'LASTHIT_LAB_' + randomBytes(4).toString('hex');
    const seen = this.wait(new RegExp(`^${marker}$`), 2000);
    this.send(`echo ${marker}`);
    return (await seen) !== null;
  }
}

const freePort = () => new Promise<number>((resolve, reject) => {
  const s = net.createServer(); s.once('error', reject);
  s.listen(0, '127.0.0.1', () => { const port = (s.address() as net.AddressInfo).port; s.close(() => resolve(port)); });
});

const videoFiles: [string, Buffer][] = [];
for (const user of await fs.readdir(userdata).catch(() => [] as string[])) {
  const file = path.join(userdata, user, '570/local/cfg/video.txt');
  const data = await fs.readFile(file).catch(() => null);
  if (data) videoFiles.push([file, data]);
}

// The custom game, with this run's settings.
await fs.cp(path.join(import.meta.dirname, 'addon'), addon, { recursive: true, errorOnExist: true, force: false });
const marker = randomBytes(8).toString('hex');
await fs.writeFile(path.join(addon, 'coach_lasthit_owned.txt'), marker, { flag: 'wx' });
await fs.writeFile(path.join(addon, 'scripts/vscripts/coach_config.lua'),
  `COACH_CONFIG = { hero = "${hero}", level = ${level}, mode = "${loop ? 'loop' : 'record'}", rounds = ${rounds}, seed = ${seed}, vision = ${frames ? 'true' : 'false'} }\n`, { flag: 'wx' });

const password = randomBytes(24).toString('hex'), port = await freePort();
const started = new Date();
const child = spawn(exe, ['-insecure', '-novid', '-console', '-windowed', '-noborder', '-w', String(width), '-h', String(height),
  ...(loop ? ['-gamestateintegration'] : []),
  '-netconport', String(port), '-netconpassword', password, '+sv_lan', '1', '+sv_password', password, '+password', password],
{ cwd: path.dirname(exe), windowsHide: false, stdio: 'ignore', env: { ...process.env, SteamAppId: '570', SteamGameId: '570' } });
child.on('error', () => {});
const trace = createWriteStream(out, { flags: 'wx' });
const log = createWriteStream(base + '.log', { flags: 'wx' });
const cueLog = loop ? createWriteStream(base + '.cues.jsonl', { flags: 'wx' }) : null;
const syncLog = frames ? createWriteStream(base + '.sync.jsonl', { flags: 'wx' }) : null;
let console_: Console | null = null, client: ChildProcess | null = null, udp: dgram.Socket | null = null;
const counts: Record<string, number> = {};
try {
  // The first process can hand over to a relaunched one: reconnect until a console answers twice, 15 s apart.
  for (let i = 0; i < 90 && !console_; i++) {
    await sleep(2000);
    const c = await Console.connect(port, password).catch(() => null);
    if (c && await c.answers()) {
      await sleep(15000);
      if (await c.answers()) console_ = c; else c.socket.destroy();
    } else c?.socket.destroy();
  }
  if (!console_) throw new Error('The game console did not answer');
  const c = console_;
  // The whole console except the trace itself.
  c.on(line => {
    if (!line.includes('[COACH_LASTHIT]') || /\[COACH_LASTHIT\]\{"k":"(warn|error|activate|begin|end|cue)"/.test(line))
      log.write(line.replaceAll(password, '***') + '\n');
  });

  let resolveEnd!: () => void, rejectEnd!: (e: Error) => void;
  const ended = new Promise<void>((resolve, reject) => { resolveEnd = resolve; rejectEnd = reject; });
  let round = 0, lastFrame = -1;
  c.on(line => {
    const at = line.indexOf('[COACH_LASTHIT]');
    if (at < 0) return;
    const text = line.slice(at + '[COACH_LASTHIT]'.length);
    let record: { k: string; message?: string; n?: number };
    try { record = JSON.parse(text); } catch { log.write('unparsed: ' + text.slice(0, 300) + '\n'); counts.unparsed = (counts.unparsed ?? 0) + 1; return; }
    counts[record.k] = (counts[record.k] ?? 0) + 1;
    trace.write(text + '\n');
    // A server frame's first line as it arrives, on the pictures' clock; the least lag over many is the offset.
    if (syncLog && record.k === 'f') {
      const t = (record as { t?: number }).t ?? -1;
      if (t !== lastFrame) { lastFrame = t; syncLog.write(JSON.stringify({ t, q: qpc() }) + '\n'); }
    }
    if (record.k === 'round') round = record.n ?? round;
    if (record.k === 'error' && counts.error > 50) rejectEnd(new Error('Lab script keeps failing: ' + record.message));
    if (record.k === 'end') resolveEnd();
  });
  const activated = c.wait(/\[COACH_LASTHIT\]\{"k":"activate"/, 240_000);
  c.send('sv_cheats 1', 'dota_launch_custom_game coach_lasthit dota');
  if (!await activated) throw new Error('The custom game did not start (see the .log)');
  if (frames) {
    // From the first round on: the pictures start once the game client finds the Dota window.
    client = spawn(gameClient, ['--creep-hp-log', base + '.hp.jsonl', '--frames', frames], { stdio: 'ignore' });
    c.send('engine_no_focus_sleep 0');
  }
  const begun = c.wait(/\[COACH_LASTHIT\]\{"k":"begin"/, 180_000);
  // Cheats for host_timescale and dota_creeps_no_spawning; the setup and strategy phases pass by themselves.
  for (let i = 0; i < 6; i++) { await sleep(5000); c.send('sv_cheats 1', 'dota_creeps_no_spawning 1'); }
  if (!await begun) throw new Error('The arena did not begin (see the .log)');
  if (loop) {
    udp = dgram.createSocket('udp4');
    const cuePort = await new Promise<number>(resolve => udp!.bind(0, '127.0.0.1', () => resolve(udp!.address().port)));
    udp.on('message', data => {
      let cue: { At: number; Rule: string; Unit: string; X: number; Y: number; Hp: number };
      try { cue = JSON.parse(data.toString('utf8')); } catch { return; }
      const rule = round % 2 === 1 ? 'ahead' : 'frame';
      const react = Math.max(.08, reaction + .04 * Math.sqrt(-2 * Math.log(1 - Math.random())) * Math.cos(2 * Math.PI * Math.random()));
      const act = cue.Rule === rule && /^npc_dota_[a-z_]+$/.test(cue.Unit);
      // qpc(): the clock of the game client's capture times (QueryPerformanceCounter since boot), to measure the screen's lag.
      cueLog!.write(JSON.stringify({ received: qpc(), round, rule, act, react: act ? react : null, ...cue }) + '\n');
      if (act) setTimeout(() => {
        cueLog!.write(JSON.stringify({ sent: qpc(), tag: `${rule}-${round}`, At: cue.At, Hp: cue.Hp }) + '\n');
        c.send(`coach_attack ${cue.X.toFixed(0)} ${cue.Y.toFixed(0)} ${cue.Unit} ${rule}-${round} ${cue.Hp}`);
      }, react * 1000);
    });
    client = spawn(gameClient, ['--creep-hp-log', base + '.hp.jsonl', '--lead', lead, '--latency', latency,
      ...(args.includes('--singles') ? ['--singles'] : []), ...(args.includes('--patient') ? ['--patient'] : []), '--cues', String(cuePort)], { stdio: 'ignore' });
    // The game client reads the Dota window even behind other windows; keep Dota rendering at full rate there.
    c.send('engine_no_focus_sleep 0');
  }
  c.send(`host_timescale ${timescale}`);
  const deadline = setTimeout(() => rejectEnd(new Error('Lab timed out')), rounds * 60_000 / timescale + 300_000);
  const progress = setInterval(() => console.log(JSON.stringify(counts)), 30_000);
  try { await ended; } finally { clearTimeout(deadline); clearInterval(progress); }
  console.log(JSON.stringify({ trace: out, loop, frames, hero, level, rounds, timescale, seed, counts }));
} finally {
  client?.kill();
  udp?.close();
  console_?.send('host_timescale 1', 'disconnect', 'quit');
  for (let i = 0; i < 40 && running(); i++) await sleep(1000);
  if (running()) {
    // Only Dota processes created after our launch: the game this lab started (it may have relaunched itself).
    execFileSync('powershell.exe', ['-NoProfile', '-Command',
      `Get-CimInstance Win32_Process -Filter "name='dota2.exe'" | Where-Object { $_.CreationDate -ge [datetime]'${started.toISOString()}' } | ` +
      'ForEach-Object { Stop-Process -Id $_.ProcessId -Force }'], { windowsHide: true });
    for (let i = 0; i < 10 && running(); i++) await sleep(1000);
  }
  console_?.socket.destroy();
  if (await fs.readFile(path.join(addon, 'coach_lasthit_owned.txt'), 'utf8').catch(() => null) === marker)
    await fs.rm(addon, { recursive: true, force: true });
  for (const [file, data] of videoFiles) {
    const now = await fs.readFile(file).catch(() => null);
    if (now && !now.equals(data)) await fs.writeFile(file, data);
  }
  trace.end(); log.end(); cueLog?.end(); syncLog?.end();
}
