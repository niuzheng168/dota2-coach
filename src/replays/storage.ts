/** Work data only: never resolve executables, credentials or published assets from the replay share. */
import { mkdirSync } from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

type Area = 'replays' | 'farm742' | 'farm742-more' | 'farm742-camps' | 'farm742-terrain' | 'map-reference' | 'daily-study';
export function replayArea(area: Area = 'replays', root = process.env.DOTA_COACH_REPLAY_WORK_ROOT): string {
  const base = root ?? (process.platform === 'win32' ? 'Z:\\dota2coach\\data' : fileURLToPath(new URL('../../.tmp', import.meta.url)));
  if (!path.isAbsolute(base) || (process.platform === 'win32' && /^[\\/](?![\\/])/.test(base)))
    throw new Error('DOTA_COACH_REPLAY_WORK_ROOT must be an absolute work directory');
  return path.join(base, area);
}

// Set scratch for this child only, including libraries that consult the OS environment.
export function replayChildEnv(directory: string): NodeJS.ProcessEnv {
  const tmp = path.resolve(directory, 'tmp');
  mkdirSync(tmp, { recursive: true });
  return { ...process.env, TEMP: tmp, TMP: tmp, TMPDIR: tmp, PYTHONDONTWRITEBYTECODE: '1' };
}
