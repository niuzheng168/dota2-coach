// Test-only preload. No production switch or user-provided host, never call the original fetch.
import { createReadStream, statSync } from 'node:fs';
import { Readable } from 'node:stream';

const base = new URL('../../../../.tmp/farm742/', import.meta.url);
const files = new Map([
  ['https://api.opendota.com/api/matches/9097559249', '9097559249.opendota.json'],
  ['http://replay442.valve.net/570/9097559249_600743976.dem.bz2', '9097559249.archive'],
]);
globalThis.fetch = async (url, init) => {
  const file = files.get(String(url));
  if (!file || init?.redirect !== 'error') throw new Error('Unexpected test transport request');
  files.delete(String(url));
  const source = new URL(file, base);
  return new Response(Readable.toWeb(createReadStream(source)) as ReadableStream<Uint8Array>, {
    headers: { 'Content-Length': String(statSync(source).size) },
  });
};
