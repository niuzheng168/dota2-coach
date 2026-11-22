/** node scripts/client/camp-742.ts --help  |  Pure module: src/tactics/camp-742.ts */
import fs from 'node:fs';
import { estimateCamp742, neutral742 } from '../../src/tactics/camp-742.ts';
import type { Attacker742, CampInput742 } from '../../src/tactics/camp-742.ts';
import catalog from '../../data/farming-742-camp-catalog.json' with { type: 'json' };

const [cmd, arg, timeArg] = process.argv.slice(2);
const print = (v: unknown) => console.log(JSON.stringify(v, null, 2));
const core = { patch: '7.42', clock: 610, scope: 'full-unstacked', policy: 'uninterrupted-attacks',
  units: ['centaur_khan', 'centaur_outrunner', 'centaur_outrunner'] } as const;
const common = { damage: [143, 151] as [number, number], attackSpeed: 180, weapon: 'bfury', fervor: 3, rage: 2 } as const;
const examples: Record<string, { attacker: Attacker742; formation?: CampInput742['formation'] }> = {
  luna: { attacker: { hero: 'luna', damage: [110, 116], attackSpeed: 180, weapon: 'none', glaives: 3 }, formation: { glaive: 'all', hopSeconds: 0 } },
  'troll-melee': { attacker: { hero: 'troll_warlord', mode: 'melee', ...common }, formation: { cleave: 'all' } },
  'troll-ranged': { attacker: { hero: 'troll_warlord', mode: 'ranged', ...common } },
  generic: { attacker: { hero: 'generic', mode: 'melee', damage: [105, 109], attackSpeed: 155, bat: 1.6, weapon: 'quelling_blade' } },
};
if (cmd === '--example' || cmd === '--input') {
  if (!Object.hasOwn(examples, arg ?? '')) throw Error('Use luna | troll-melee | troll-ranged | generic');
  const input: CampInput742 = { ...core, units: [...core.units], ...examples[arg] };
  print(cmd === '--input' ? input : { input, result: estimateCamp742(input) });
} else if (cmd === '--file' || cmd === '--stdin') {
  print(estimateCamp742(JSON.parse(fs.readFileSync(cmd === '--stdin' ? 0 : arg, 'utf8').replace(/^\uFEFF/, ''))));
} else if (cmd === '--unit') {
  const unit = neutral742(arg ?? '');
  print({ name: arg, baseDefinition: unit, naturallyObservedThrough20: catalog.compositions.some(c => c.units.includes((arg ?? '').replace(/^npc_dota_neutral_/, ''))),
    ...(timeArg === undefined ? {} : { tierQuote: estimateCamp742({ patch: '7.42', clock: Number(timeArg), scope: 'listed-units', units: [arg] }) }) });
} else if (cmd === '--catalog') {
  print(catalog.compositions.map(({ units, observations, matchCount, spawnerTypes }) => ({ units, observations, matchCount, spawnerTypes,
    ...(arg === undefined ? {} : { bounty: estimateCamp742({ patch: '7.42', clock: Number(arg), scope: 'full-unstacked', units }).bounty }) })));
} else if (cmd === '--evidence') {
  const data = JSON.parse(fs.readFileSync(new URL('../../data/farming-742-camp-comparison.json', import.meta.url), 'utf8'));
  print(arg ? data.episodes.filter((e: any) => e.matchId === Number(arg)) : data.summary);
} else {
  console.log('Offline 7.42 camp scenarios (not live ETA). Run from repository root.\n' +
    '--example NAME | --input NAME   luna | troll-melee | troll-ranged | generic\n' +
    '--file INPUT.json | --stdin     quote a known roster; omit attacker for bounty only\n' +
    '--unit UNIT [CLOCK] | --catalog [CLOCK]   raw unit / observed rosters; optional gross rolls at game seconds\n' +
    '--evidence [MATCH_ID]           strict replay comparison summary / labeled episodes');
  if (cmd && cmd !== '--help') process.exitCode = 2;
}
