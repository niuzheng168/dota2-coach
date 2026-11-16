import fs from 'node:fs/promises';
import path from 'node:path';
import { createHash } from 'node:crypto';

export function replayUrl(value: unknown, id: number): URL {
  if (typeof value !== 'string') throw new Error('OpenDota has no replay URL; do not guess a replay salt');
  const url = new URL(value);
  if (!['http:', 'https:'].includes(url.protocol) || url.username || url.password || url.port ||
      !/^replay\d+\.valve\.net$/.test(url.hostname) || url.search || url.hash ||
      !new RegExp(`^/570/${id}_\\d+\\.dem\\.bz2$`).test(url.pathname))
    throw new Error('Unexpected replay origin/path');
  return url;
}
export async function downloadReplay(id: number, directory = '.tmp/replays', metadataBytes?: Buffer) {
  if (!Number.isSafeInteger(id) || id <= 0) throw new Error('Invalid match ID');
  const root = path.resolve(directory);
  await fs.mkdir(root, { recursive: true });
  const apiUrl = `https://api.opendota.com/api/matches/${id}`;
  const signal = AbortSignal.timeout(300_000);
  let bytes = metadataBytes;
  if (!bytes) {
    const metadataResponse = await fetch(apiUrl, { redirect: 'error', signal });
    if (!metadataResponse.ok) throw new Error(`OpenDota HTTP ${metadataResponse.status}`);
    bytes = await boundedBytes(metadataResponse, 8 * 1024 * 1024);
  }
  if (bytes.length > 8 * 1024 * 1024) throw new Error('Metadata too large');
  const match = JSON.parse(bytes.toString('utf8'));
  if (match.match_id !== id) throw new Error('Match ID mismatch');
  const url = replayUrl(match.replay_url, id);
  const archive = path.join(root, `${id}.archive`), temporary = archive + '.partial';
  // Never overwrite a prior archive/manifest, including failed partial downloads.
  for (const f of [archive, temporary, path.join(root, `${id}.acquisition.json`)]) {
    if (await fs.stat(f).then(() => true, () => false)) throw new Error(`Evidence already exists: ${path.basename(f)}`);
  }
  const r = await fetch(url, { signal, redirect: 'error' });
  if (!r.ok || !r.body) throw new Error(`Valve replay HTTP ${r.status}`);
  const limit = 512 * 1024 * 1024;
  if (Number(r.headers.get('content-length')) > limit) { await r.body.cancel(); throw new Error('Archive too large'); }
  const handle = await fs.open(temporary, 'wx'), hash = createHash('sha256');
  let size = 0, magic = Buffer.alloc(0);
  try {
    for await (const chunk of r.body) {
      size += chunk.length;
      if (size > limit) throw new Error('Archive exceeds 512 MiB');
      if (magic.length < 4) magic = Buffer.concat([magic, chunk]).subarray(0, 4);
      hash.update(chunk);
      await handle.writeFile(chunk);
    }
  } finally { await handle.close(); }
  if (magic.subarray(0, 3).toString() !== 'BZh' && magic.toString('hex') !== '28b52ffd')
    throw new Error('Archive is neither bzip2 nor Zstandard');
  await fs.rename(temporary, archive);
  await fs.writeFile(path.join(root, `${id}.opendota.json`), bytes, { flag: 'wx' });
  const evidence = {
    matchId: id, metadataUrl: apiUrl, replayUrl: url.href, downloadedAt: new Date().toISOString(),
    archiveBytes: size, archiveSha256: hash.digest('hex'), metadataSha256: createHash('sha256').update(bytes).digest('hex'),
    compression: magic.subarray(0, 3).toString() === 'BZh' ? 'bzip2' : 'zstandard',
    transport: url.protocol === 'http:' ? 'Valve HTTP (not authenticated)' : 'HTTPS',
    note: 'Local hashes prove reproducibility, not authenticity of the HTTP transport.',
  };
  await fs.writeFile(path.join(root, `${id}.acquisition.json`), JSON.stringify(evidence, null, 2) + '\n', { flag: 'wx' });
  return evidence;
}
async function boundedBytes(r: Response, limit: number): Promise<Buffer> {
  if (!r.body) throw new Error('Empty response');
  const chunks: Buffer[] = []; let size = 0;
  for await (const chunk of r.body) {
    size += chunk.length; if (size > limit) throw new Error('Metadata too large');
    chunks.push(Buffer.from(chunk));
  }
  return Buffer.concat(chunks);
}
if (process.argv[1] && path.resolve(process.argv[1]) === path.resolve(import.meta.filename)) {
  console.log(JSON.stringify(await downloadReplay(Number(process.argv[2]), process.argv[3]), null, 2));
}
