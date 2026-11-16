import fs from 'node:fs/promises';
import net from 'node:net';
import path from 'node:path';
import { createHash } from 'node:crypto';
import { randomBytes } from 'node:crypto';
import { spawn, execFileSync } from 'node:child_process';
import { validateEngineMapCapture } from '../../src/replays/source2-map.ts';

// Start and stop our own dedicated process. Never attach to a user's game.
const mechanics = process.argv.includes('--mechanics');
const scriptName = mechanics ? 'coach_mechanics_audit_6975' : 'coach_map_audit_6975';
const install = path.resolve(process.argv[2] || 'C:/Steam/steamapps/common/dota 2 beta');
const scriptPath = path.join(install, 'game/dota/scripts/vscripts', `${scriptName}.lua`);
const source = await fs.readFile(mechanics ? 'scripts/client/engine-mechanics-probe.lua' : 'scripts/client/engine-map-probe.lua');
const scriptHash = createHash('sha256').update(source).digest('hex');
const out = path.resolve(process.argv[3] || (mechanics ? '.tmp/client-audit/runtime-mechanics.json' : '.tmp/client-audit/runtime-map.json'));
const inf = await fs.readFile(path.join(install, 'game/dota/steam.inf'), 'utf8');
if (!/^ServerVersion=6975\s*$/m.test(inf)) throw new Error('Unreviewed client build; do not mix runtime fixtures');
const mapFile=path.join(install,'game/dota/maps/dota.vpk');
const mapSha256=createHash('sha256').update(await fs.readFile(mapFile)).digest('hex');
if (await fs.stat(out).then(() => true, () => false)) throw new Error('Runtime evidence already exists; preserve it');
if (process.platform !== 'win32') throw new Error('This runner currently validates Windows process ownership only');
const running = execFileSync('powershell.exe', ['-NoProfile', '-Command',
  '(Get-Process dota2 -ErrorAction SilentlyContinue | Measure-Object).Count'], { encoding: 'utf8', windowsHide: true }).trim();
if (running !== '0') throw new Error('A Dota process already exists; do not interfere with the user game');
await new Promise<void>((resolve, reject) => {
  const test = net.createServer(); test.once('error', reject);
  test.listen(29009, '127.0.0.1', () => test.close(() => resolve()));
});
await fs.writeFile(scriptPath, source, { flag: 'wx' });
const executable = path.join(install, 'game/bin/win64/dota2.exe');
const child = spawn(executable, ['-dedicated', '-insecure', '-console', '-ip', '127.0.0.1',
  '-port', '29015', '-netconport', '29009', '+sv_lan', '1', '+sv_password', randomBytes(24).toString('hex'),
  '+sv_hibernate_when_empty', '0', '+dota_quit_after_game', '0', '+map', 'dota'],
{ cwd: path.dirname(executable), windowsHide: true, stdio: 'ignore' });
let processError: Error | null = null; child.once('error', e => { processError = e; });
const captured: any[] = [], diagnostics: string[] = [];
try {
  for (let i = 0; i < 60; i++) {
    if (processError || child.exitCode !== null) throw processError || new Error('Dedicated process exited during startup');
    const ready = await new Promise<boolean>(resolve => {
      const s = net.createConnection({ host: '127.0.0.1', port: 29009 });
      s.once('connect', () => { s.destroy(); resolve(true); });
      s.once('error', () => resolve(false)); s.setTimeout(500, () => { s.destroy(); resolve(false); });
    });
    if (ready) break;
    if (i === 59) throw new Error('Dedicated console did not become ready');
    await new Promise(r => setTimeout(r, 1000));
  }
  const records = await new Promise<any[]>((resolve, reject) => {
    const records = captured;
    const socket = net.createConnection({ host: '127.0.0.1', port: 29009 });
    let buffer = '', started = false;
    const deadline = setTimeout(() => finish(new Error('Runtime map capture timed out')), 120_000);
    function finish(error?: Error) {
      clearTimeout(deadline); clearInterval(poll); socket.destroy();
      error ? reject(error) : resolve(records);
    }
    const load = () => socket.write(`sv_cheats 1\nsv_hibernate_when_empty 0\nscript_reload_code ${scriptName}\n`);
    const poll = setInterval(() => { if (!started && socket.readyState === 'open') load(); }, 2000);
    socket.on('connect', load);
    socket.on('error', finish);
    socket.on('close', () => { if (!records.some(r => r.kind === 'end')) finish(new Error('Incomplete engine capture')); });
    socket.on('data', b => {
      buffer += b.toString('utf8');
      if (buffer.length > 2 * 1024 * 1024) return finish(new Error('Console line limit exceeded'));
      const lines = buffer.split('\n'); buffer = lines.pop()!;
      try {
        for (const line of lines) {
          const at = line.indexOf('[COACH_MAP_AUDIT]');
          if (at < 0) {
            if (/Script Runtime Error|stack traceback|attempt to |scripts\/vscripts\/coach_|bad argument/i.test(line) && diagnostics.length < 30)
              diagnostics.push(line.slice(0, 500));
            continue; // Discard unrelated console/account information.
          }
          const record = JSON.parse(line.slice(at + '[COACH_MAP_AUDIT]'.length));
          if (record.kind === 'begin') {
            if (started || record.data.map !== 'dota') throw new Error('Duplicate/wrong engine map');
            started = true;
          }
          if (!started) throw new Error('Missing begin marker');
          records.push(record);
          if (records.length > 10_000) throw new Error('Record limit');
          if (record.kind === 'end') return finish();
        }
      } catch (e) { finish(e as Error); }
    });
  });
  const header = records[0]?.data, rows = records.filter(r => r.kind === 'row').map(r => r.data);
  if (!mechanics && (rows.length !== header.height || rows.some((r, i) => r.index !== i || r.heights.length !== header.width)))
    throw new Error('Incomplete/out-of-order terrain rows');
  const result = { scriptSha256: scriptHash, ...header, rows, trees: records.filter(r => r.kind === 'tree').map(r => r.data),
    client:{serverBuild:6975,mapPackageSha256:mapSha256,steamInfSha256:createHash('sha256').update(inf).digest('hex')},
    entities: records.filter(r => r.kind === 'entity').map(r => r.data),
    samples: records.filter(r => r.kind === 'sample').map(r => r.data), completed: records.at(-1).data,
    isolation: 'Owned password-protected dedicated process; insecure/LAN, console connection via loopback; engine UDP may bind wildcard, not claimed as OS network sandbox.' };
  if (mechanics && result.completed.completed !== true) throw new Error(`Mechanics probe failed: ${result.completed.error || 'incomplete'}`);
  if(!mechanics)validateEngineMapCapture(result);
  if(createHash('sha256').update(await fs.readFile(mapFile)).digest('hex')!==mapSha256)throw new Error('Map package changed during capture');
  await fs.writeFile(out, JSON.stringify(result), { flag: 'wx' });
  console.log(JSON.stringify({ map: header.map, rows: rows.length, trees: result.trees.length, entities: result.entities.length }));
} catch (error) {
  await fs.writeFile(out+'.failed.json',JSON.stringify({error:error instanceof Error?error.message:'Failed',
    records:captured,diagnostics,scriptSha256:scriptHash}),{flag:'wx'}).catch(()=>{});
  throw error;
} finally {
  // The only process terminated here is the exact child we launched.
  child.kill();
  if (child.exitCode === null) await new Promise<void>(r => { child.once('exit', () => r()); setTimeout(r, 3000).unref(); });
  // Never delete an unrelated or subsequently changed game file.
  const current = await fs.readFile(scriptPath).catch(() => null);
  if (current && createHash('sha256').update(current).digest('hex') === scriptHash) await fs.unlink(scriptPath);
  else console.error('Audit script changed externally; left untouched for manual review');
}
