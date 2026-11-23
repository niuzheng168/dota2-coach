/** Reproduce numeric data from immutable Valve game-file mirrors; optionally verify the installed VPK. */
import assert from 'node:assert/strict';
import { execFileSync } from 'node:child_process';
import { createHash } from 'node:crypto';
import fs from 'node:fs';
import path from 'node:path';
import { replayArea, replayChildEnv } from '../../src/replays/storage.ts';
import { parseKeyValues, kvObject, numbers } from '../lib/valve-kv.ts';
import type { KV } from '../lib/valve-kv.ts';

const work = replayArea('farm742-camps'), dest = 'data/farming-742-camp-units.json';
const builds = [
  [6983, '7bf93721e36b447fa6d4128be329a006628d8d94', 'd5b66d82a6e6885670a4c4b8ea8d46e9fab3d3bc5a7e3fa04d9ffda25aa7e40a'],
  [6985, 'ff61ff289521d66a629f19fe502d3920ce08557d', '13654f32f11b961fff1f191637fc57a315e5b64c62b4e17bd1783548931b0f4e'],
  [6986, '547c99cf2c81c2c6a4e34a16fc635b682d5838d3', '7b86b99bec3630eff0a2698ddb956bc2b688943b1679ad0feeda13232d7e8601'],
  [6988, '8c9915dadb50c83c5427846d554fb3da84c18d7e', 'e5cd5ec3806caa98463f48062d4168f8d66399d1f2b80c397b591855dd5d53a7'],
  [6989, '76ab89451c1384c2e106b676258c6e9f72787e11', 'e5a14b290a771c54148039dd4ca7d2e23c730cbd4a08b972c6e01cea24b03843'],
] as const;
const files = {
  'scripts/npc/npc_units.txt': '1a8e4f01480630cfb9aa1daa2eb1bc348d7793c0ae4ced6869f1e552b63d6596',
  'scripts/npc/npc_abilities.txt': 'e9f8111f0d11df939ac0246ad58e10b4df9fa4cf7e05d75e004199975ba7f7c4',
  'scripts/npc/items.txt': '3aa970ea2b1e5fa025731a98748329b4ebfb3a40db63bf63c7b928f5110345af',
  'scripts/npc/heroes/npc_dota_hero_base.txt': 'e30554bae64770783695fd868fb1c4bed8f33b1e7340bb09a41bbc2b1eeecf86',
  'scripts/npc/heroes/npc_dota_hero_luna.txt': '82ebaf8770330f0f3ddfd2b25924d53f26e3679272529cbc32ae8342b07b46a8',
  'scripts/npc/heroes/npc_dota_hero_troll_warlord.txt': '8d9a261be8cb387c5ef29e19506facf39003b0c62a9c79c0f6c765a2d4400611',
  'resource/localization/abilities_english.txt': 'ab9538148306540fbf4282e7e184380b516ee0954dd0e465adb449e87025388d',
};
const upstream = 'https://raw.githubusercontent.com/SteamDatabase/GameTracking-Dota2/{commit}/game/dota/';
const sha = (v: string | Buffer) => createHash('sha256').update(v).digest('hex');
const cache = (build: number, p: string) => path.join(work, 'upstream', `${build}`, p);
for (const [build, commit, infSha] of builds) {
  for (const [p, expected] of [['steam.inf', infSha], ...Object.entries(files).map(([p, h]) => ['pak01_dir/' + p, h])]) {
    const file = cache(build, p);
    if (!fs.existsSync(file) && process.argv.includes('--fetch')) {
      const buf = execFileSync('curl', ['--fail', '--silent', '--show-error', '--location', '--retry', '2',
        '--max-time', '40', upstream.replace('{commit}', commit) + p], { maxBuffer: 12_000_000 });
      assert.equal(sha(buf), expected, p); fs.mkdirSync(path.dirname(file), { recursive: true }); fs.writeFileSync(file, buf);
    }
    const bytes = fs.readFileSync(file);
    assert.equal(sha(bytes), expected, `${build} ${p} -- wrong bytes`);
    if (p === 'steam.inf') for (const key of ['ClientVersion', 'ServerVersion']) {
      assert(bytes.toString('utf8').split('\n').includes(`${key}=${build}`));
    }
  }
}
const install = process.argv.find(x => x.startsWith('--installed='))?.slice('--installed='.length);
if (install) {
  const cli = process.argv.find(x => x.startsWith('--cli='))?.slice(6) ?? '.tmp/client-audit/vrf-21.1/Source2Viewer-CLI.exe';
  assert.equal(sha(fs.readFileSync(cli)), '50502077543856a3f760eecf53b073dcdea5073c193f0274c2088b5a2a8f4d13');
  const verify = (f: string, digest: string) => assert.equal(sha(fs.readFileSync(f, 'utf8').replace(/\r\n/g, '\n')), digest, f);
  const inf = path.join(install, 'game/dota/steam.inf');
  verify(inf, builds[4][2]);
  // A fresh extraction directory prevents stale files passing after an incomplete extraction.
  fs.mkdirSync(work, { recursive: true });
  const raw = fs.mkdtempSync(path.join(work, 'installed-'));
  execFileSync(cli, ['-i', path.join(install, 'game/dota/pak01_dir.vpk'), '-f', Object.keys(files).join(','), '-o', raw, '-q'], { env: replayChildEnv(work) });
  for (const [name, digest] of Object.entries(files)) verify(path.join(raw, name), digest);
  verify(inf, builds[4][2]);
  console.log('Installed 6989: steam.inf and seven VPK members match (only CRLF→LF).');
}
const kv = (p: string, root: string) => kvObject(parseKeyValues(fs.readFileSync(cache(6989, 'pak01_dir/scripts/npc/' + p), 'utf8'))[root]);
const number = (d: KV, k: string) => {
  const v = numbers(d[k]); assert.equal(v.length, 1, `Missing/ambiguous numeric field ${k}`); return v[0];
};
const allUnits = kv('npc_units.txt', 'DOTAUnits'), base = kvObject(allUnits.npc_dota_units_base);
const units = Object.fromEntries(Object.entries(allUnits).filter(([name, val]) =>
  (name.startsWith('npc_dota_neutral_') || name === 'npc_dota_dark_troll_warlord_skeleton_warrior') &&
  kvObject(val).BaseClass === 'npc_dota_creep_neutral' && typeof kvObject(val).StatusHealth === 'string'
).map(([name, val]) => {
  const u = { ...base, ...kvObject(val) }, n = (k: string) => number(u, k);
  const abilityNames = Object.entries(u).filter(([k, v]) => /^Ability\d+$/.test(k) && typeof v === 'string' && v).map(([, v]) => v as string);
  assert(n('StatusHealth') > 0 && n('BountyGoldMax') >= n('BountyGoldMin') && n('BountyGoldMin') >= 0);
  return [name, {
    hp: n('StatusHealth'), regen: n('StatusHealthRegen'), armor: n('ArmorPhysical'), magicResistPct: n('MagicalResistance'),
    gold: [n('BountyGoldMin'), n('BountyGoldMax')], xp: n('BountyXP'),
    attack: [n('AttackDamageMin'), n('AttackDamageMax')], bat: n('AttackRate'), baseAttackSpeed: n('BaseAttackSpeed'),
    ancient: n('IsAncient') === 1, abilities: abilityNames,
  }];
}));
const hero = (name: string) => kvObject(kv(`heroes/npc_dota_hero_${name}.txt`, 'DOTAHeroes')[`npc_dota_hero_${name}`]);
const heroBase = hero('base'), luna = hero('luna'), troll = hero('troll_warlord');
const heroes = Object.fromEntries([['luna', luna], ['troll_warlord', troll]].map(([name, def]) => {
  const h = { ...heroBase, ...(def as KV) }, n = (k: string) => number(h, k);
  return [name, { id: n('HeroID'), baseAttack: [n('AttackDamageMin'), n('AttackDamageMax')], baseAttackSpeed: n('BaseAttackSpeed'),
    bat: n('AttackRate'), attackPoint: n('AttackAnimationPoint'), range: n('AttackRange'), projectileSpeed: n('ProjectileSpeed'),
    primary: h.AttributePrimary, agility: n('AttributeBaseAgility'), agilityGain: n('AttributeAgilityGain') }];
}));
const allAbilities = kv('npc_abilities.txt', 'DOTAAbilities'), allItems = kv('items.txt', 'DOTAAbilities');
function values(source: KV, name: string) {
  const val = kvObject(source[name]).AbilityValues;
  assert(val && typeof val === 'object', `Missing AbilityValues: ${name}`); return val;
}
const select = (src: KV, names: string[]) => Object.fromEntries(names.map(k => [k, values(src, k)]));
const mechanics = {
  ...select(allAbilities, ['neutral_upgrade', 'neutral_gold_upgrade_ability', 'mud_golem_rock_destroy',
    'granite_golem_hp_aura', 'enraged_wildkin_toughness_aura', 'black_dragon_dragonhide_aura', 'satyr_hellcaller_unholy_aura']),
  ...select(kvObject(luna.AbilityDefinitions), ['luna_moon_glaive', 'luna_lunar_blessing']),
  ...select(kvObject(troll.AbilityDefinitions), ['troll_warlord_switch_stance', 'troll_warlord_fervor', 'troll_warlord_berserkers_rage']),
  ...select(allItems, ['item_quelling_blade', 'item_bfury', 'item_mask_of_madness', 'item_power_treads', 'item_wraith_band',
    'item_gloves', 'item_yasha', 'item_manta', 'item_sange_and_yasha', 'item_enhancement_alert', 'item_enhancement_brawny',
    'item_enhancement_tough', 'item_enhancement_quickened', 'item_weighted_dice', 'item_possessed_mask', 'item_stonefeather_satchel',
    'item_metamorphic_mandible']),
};
const data = { schema: 1, patch: '7.42', installedBuild: 6989, upstream,
  builds: builds.map(([build, commit, steamInfSha256]) => ({ build, commit, steamInfSha256 })),
  commonFileSha256: files, note: 'All seven file bytes identical on the five fixed commits; installed build 6989 matches after CRLF to LF only. Unit definitions are NOT a spawn table.',
  heroes, mechanics, units };
const json = JSON.stringify(data, null, 2) + '\n';
if (process.argv.includes('--check')) assert.equal(fs.readFileSync(dest, 'utf8'), json);
else fs.writeFileSync(dest, json);
console.log(`${Object.keys(units).length} raw neutral unit definitions; 5 builds; ${dest}`);
