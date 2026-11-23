import { pathToFileURL } from 'node:url';
import { replayArea } from '../../src/replays/storage.ts';
import { downloadReplay as acquire } from './postgame/pinned_v2/download.ts';

export { replayUrl } from './postgame/pinned_v2/download.ts';
export function downloadReplay(id: number, directory = replayArea(), metadataBytes?: Buffer) {
  return acquire(id, directory, metadataBytes);
}

if (process.argv[1] && import.meta.url === pathToFileURL(process.argv[1]).href)
  console.log(JSON.stringify(await downloadReplay(Number(process.argv[2]), process.argv[3]), null, 2));
