/** Local offline CLI. Outputs exact input endpoints, accepted polyline and ETA
 * or an explicit refusal. Camp mode is centre-to-centre, not attack-to-attack. */
import fs from 'node:fs';
import { estimateWalk742 } from '../../src/tactics/terrain-742.ts';

const [mode, ...argv] = process.argv.slice(2);
const coord = (x: number, y: number) => ({ x, y });
let from, to, speed;
if (mode === '--camp' && argv.length === 3) {
  const d = JSON.parse(fs.readFileSync(new URL('../../data/farming-742-terrain.json', import.meta.url), 'utf8'));
  const point = (id: string) => {
    const c = d.camps.find((p: { id: string }) => p.id === id);
    if (!c) throw new Error(`Unknown 7.42 camp: ${id}`);
    return coord(c.origin[0], c.origin[1]);
  };
  from = point(argv[0]); to = point(argv[1]); speed = Number(argv[2]);
} else if (mode === '--xy' && argv.length === 5) {
  const [x, y, u, v, s] = argv.map(Number); from = coord(x, y); to = coord(u, v); speed = s;
} else throw new Error('Usage: --camp good_14 good_20 370 | --xy fromX fromY toX toY effectiveMoveSpeed');
const result = estimateWalk742(from, to, speed);
console.log(JSON.stringify({ from, to, ...result }, null, 2));
if (!result.ok) process.exitCode = 2;
