import fs from 'node:fs/promises';

// Recordings of scripts/vision/hero_scan.py: one folder per session with a timeline.jsonl whose first
// line is a header and every further line a snapshot of the match as the spectator knew it.
export const HERO_SCAN_ROOT = new URL('../../.tmp/hero-scan/', import.meta.url);
const SESSION = /^[A-Za-z0-9_-]{1,64}$/;
const HEADER_BYTES = 512 * 1024;
const CHUNK_BYTES = 16 * 1024 * 1024;

export function validSession(id: string) { return SESSION.test(id); }

async function firstLine(file: URL) {
  const handle = await fs.open(file, 'r');
  try {
    const buffer = Buffer.alloc(HEADER_BYTES);
    const { bytesRead } = await handle.read(buffer, 0, HEADER_BYTES, 0);
    const end = buffer.subarray(0, bytesRead).indexOf(10);
    return end < 0 ? null : JSON.parse(buffer.subarray(0, end).toString('utf8'));
  } finally { await handle.close(); }
}

export async function listScanSessions(root: URL = HERO_SCAN_ROOT) {
  const entries = await fs.readdir(root, { withFileTypes: true }).catch(() => []);
  const sessions = [];
  for (const entry of entries) {
    if (!entry.isDirectory() || !validSession(entry.name)) continue;
    const file = new URL(`${entry.name}/timeline.jsonl`, root);
    const stat = await fs.stat(file).catch(() => null);
    if (!stat) continue;
    const header = await firstLine(file).catch(() => null);
    const summary = await fs.readFile(new URL(`${entry.name}/summary.json`, root), 'utf8').then(JSON.parse, () => null);
    sessions.push({ id: entry.name, bytes: stat.size, modified: stat.mtime.toISOString(),
      started: header?.started ?? null, truth: header?.truth === true, finished: summary !== null, summary });
  }
  return sessions.sort((a, b) => b.modified.localeCompare(a.modified));
}

/** Complete lines from byte `offset` on (at most 16 MiB), so a live session can be followed by offset. */
export async function readScanTimeline(id: string, offset: number, root: URL = HERO_SCAN_ROOT) {
  if (!validSession(id)) throw new Error('无效的扫描会话名');
  if (!Number.isSafeInteger(offset) || offset < 0) throw new Error('无效的读取位置');
  const handle = await fs.open(new URL(`${id}/timeline.jsonl`, root), 'r');
  try {
    const size = (await handle.stat()).size;
    if (offset > size) return { header: null, snapshots: [], next: 0, size, restarted: true };
    const length = Math.min(size - offset, CHUNK_BYTES);
    const buffer = Buffer.alloc(length);
    const { bytesRead } = await handle.read(buffer, 0, length, offset);
    const complete = buffer.subarray(0, bytesRead).lastIndexOf(10) + 1; // a line still being written stays for later
    let header = null;
    const snapshots = [];
    for (const line of buffer.subarray(0, complete).toString('utf8').split('\n')) {
      if (!line) continue;
      const value = JSON.parse(line);
      if (value.type === 'header') header = value;
      else if (value.type === 'snapshot') snapshots.push(value);
    }
    return { header, snapshots, next: offset + complete, size, restarted: false };
  } finally { await handle.close(); }
}
