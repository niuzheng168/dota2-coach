import fs from 'node:fs';
import { createHash } from 'node:crypto';
import { parseKeyValues, kvObject } from './lib/valve-kv.ts';
import { COMBAT } from '../src/tactics/combat-data.ts';
const root='.tmp/client-audit/raw-game/scripts/npc/';
const pins=JSON.parse(fs.readFileSync('data/dota2-jungle-data-hashes.json','utf8')).sha256;
const sources:Record<string,string>={};
function kv(name:string) {
  const text=fs.readFileSync(root+name,'utf8').replace(/\r\n/g,'\n');
  const sha=createHash('sha256').update(text).digest('hex');
  if(sha!==pins[name])throw new Error(`Unverified simulator source ${name}`);
  sources[name]=sha;return parseKeyValues(text);
}
const units=kvObject(kv('npc_units.txt').DOTAUnits),unitBase=kvObject(units.npc_dota_units_base);
const heroBase=kvObject(kvObject(kv('heroes/npc_dota_hero_base.txt').DOTAHeroes).npc_dota_hero_base);
const luna=kvObject(kvObject(kv('heroes/npc_dota_hero_luna.txt').DOTAHeroes).npc_dota_hero_luna);
function spec(raw:any) {
  const n=(k:string)=>{const value=Number(raw[k]);if(!Number.isFinite(value))throw new Error(`Missing ${k}`);return value;};
  return {health:n('StatusHealth'),mana:n('StatusMana'),healthRegen:n('StatusHealthRegen'),manaRegen:n('StatusManaRegen'),
    armor:n('ArmorPhysical'),magicResistance:n('MagicalResistance')/100,damageMin:n('AttackDamageMin'),damageMax:n('AttackDamageMax'),
    attackTime:n('AttackRate'),attackSpeed:n('BaseAttackSpeed'),attackPoint:n('AttackAnimationPoint'),
    attackRange:n('AttackRange'),acquisitionRange:n('AttackAcquisitionRange'),
    projectileSpeed:raw.AttackCapabilities==='DOTA_UNIT_CAP_RANGED_ATTACK'?n('ProjectileSpeed'):0,
    moveSpeed:n('MovementSpeed'),turnRate:n('MovementTurnRate'),goldMin:n('BountyGoldMin'),goldMax:n('BountyGoldMax'),
    hullName:raw.BoundsHullName};
}
const definitions=Object.fromEntries(Object.entries(units).filter(([name])=>
  name.startsWith('npc_dota_neutral_')&&!name.endsWith('_default')||
  /^npc_dota_creep_(goodguys|badguys)_(melee|ranged)$/.test(name)||/^npc_dota_(goodguys|badguys)_tower/.test(name))
  .map(([name,u])=>[name,spec({...unitBase,...kvObject(u)})]).filter(([,s])=>(s as any).health>0));
const output={version:1,patch:COMBAT.patch,serverBuild:6975,sources,units:definitions,
  heroes:{npc_dota_hero_luna:{...spec({...heroBase,...luna}),...COMBAT.heroes.npc_dota_hero_luna,
    baseHealth:Number(heroBase.StatusHealth),baseMana:Number(heroBase.StatusMana),label:'Luna',portrait:null}},
  note:'Source stats, not a complete Dota rules engine. Small/regular unit hull radii and combat-class multipliers are explicit simulator approximations.'};
const text=JSON.stringify(output)+'\n';
if(process.argv.includes('--check')){if(fs.readFileSync('data/dota2-simulator-definitions.json','utf8')!==text)throw new Error('Definitions changed');}
else fs.writeFileSync('data/dota2-simulator-definitions.json',text);
console.log(`Verified simulator definitions: ${Object.keys(definitions).length} units; Luna registered.`);
