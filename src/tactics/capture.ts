import fs from 'node:fs/promises';
import path from 'node:path';

export function sanitizeGsiCapture(value: unknown, inPlayer = false): unknown {
  if (Array.isArray(value)) return value.map(v => sanitizeGsiCapture(v, inPlayer));
  if (!value || typeof value !== 'object') return value;
  return Object.fromEntries(Object.entries(value).filter(([key]) =>
    !['auth', 'token', 'steamid', 'steam_id', 'accountid'].includes(key.toLowerCase()) &&
    !(inPlayer && ['name', 'player_name'].includes(key.toLowerCase())))
    .map(([key, val]) => [key, sanitizeGsiCapture(val, inPlayer || key === 'player')]));
}
export class GsiCapture {
  private pending = 0;
  private queue: Promise<void> = Promise.resolve();
  readonly directory: string | undefined;
  constructor(directory = process.env.GSI_CAPTURE_DIR) { this.directory = directory; }
  record(roomId: string, payload: Record<string, any>, now: number): void {
    if (!this.directory || this.pending >= 16) return;
    // Do not record spectator/coach hidden information for this feature.
    if (['hero', 'player', 'abilities', 'items', 'draft'].some(k => payload[k]?.team2 || payload[k]?.team3)) return;
    const line = JSON.stringify({ receivedAt: now, payload: sanitizeGsiCapture(payload) }) + '\n';
    const file = path.join(this.directory, `${roomId.replace(/[^a-zA-Z0-9-]/g, '_')}-${new Date(now).toISOString().slice(0, 10)}.jsonl`);
    this.pending++;
    this.queue = this.queue.then(async () => {
      await fs.mkdir(this.directory!, { recursive: true, mode: 0o700 });
      const bytes = await fs.stat(file).then(s => s.size, () => 0);
      if (bytes + Buffer.byteLength(line) > 32 * 1024 * 1024) return; // bounded daily per-room capture
      await fs.appendFile(file, line, { mode: 0o600 });
    }).catch(() => { console.warn('[tactics] GSI capture failed'); }).finally(() => { this.pending--; });
  }
  async flush(): Promise<void> { await this.queue; }
}
