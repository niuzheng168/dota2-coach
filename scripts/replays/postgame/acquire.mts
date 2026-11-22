// One explicit Download action = one metadata GET, then at most one archive GET.
// Do not invoke OpenDota's parse service, enumerate matches, or guess salts.
import { pathToFileURL } from 'node:url';
import { downloadReplay, replayUrl } from '../download.ts';

export async function acquire(id: number, directory: string) {
  if (!Number.isSafeInteger(id) || id <= 0) throw new Error('Invalid match ID');
  const response = await fetch(`https://api.opendota.com/api/matches/${id}`, {
    credentials: 'omit', redirect: 'error', signal: AbortSignal.timeout(30_000),
  });
  if (!response.ok || !response.body) throw new Error(`Public metadata unavailable (OpenDota HTTP ${response.status})`);
  const limit = 8 * 1024 * 1024;
  if (Number(response.headers.get('content-length')) > limit) {
    await response.body.cancel();
    throw new Error('Metadata too large');
  }
  const chunks: Buffer[] = [];
  let size = 0;
  for await (const chunk of response.body) {
    size += chunk.byteLength;
    if (size > limit) throw new Error('Metadata too large');
    chunks.push(Buffer.from(chunk));
  }
  const bytes = Buffer.concat(chunks);
  let m;
  try { m = JSON.parse(bytes.toString('utf8')); } catch { throw new Error('Invalid public metadata JSON'); }
  if (!m || m.match_id !== id || typeof m.radiant_win !== 'boolean' ||
      !Number.isSafeInteger(m.duration) || m.duration <= 0 ||
      !Number.isSafeInteger(m.start_time) || m.start_time <= 0 || m.start_time + m.duration > Date.now() / 1000)
    throw new Error('Metadata does not confirm this completed match');
  const url = replayUrl(m.replay_url, id);
  // Refuse noncanonical input (URL() would silently strip whitespace/default ports or normalize "..").
  if (m.replay_url !== url.href || !new RegExp(`^https?://replay[0-9]{1,5}\\.valve\\.net/570/${id}_[0-9]{1,20}\\.dem\\.bz2$`).test(url.href))
    throw new Error('Unexpected public replay URL');
  console.error(JSON.stringify({ type: 'progress', stage: 'archive', transport: url.protocol.slice(0, -1) }));
  return downloadReplay(id, directory, bytes); // shares host/path gate, no redirects, byte/time caps, hash + magic
}

if (process.argv[1] && import.meta.url === pathToFileURL(process.argv[1]).href) {
  try { await acquire(Number(process.argv[2]), process.argv[3]); }
  catch (e) {
    console.error(JSON.stringify({ type: 'error', stage: 'download', message: String((e as Error).message).slice(0, 240) }));
    process.exitCode = 3;
  }
}
