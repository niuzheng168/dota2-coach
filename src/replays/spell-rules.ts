/** Public base definitions. These are not an opponent's build, effective panel or damage to this target. */
import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import fs from 'node:fs';
import path from 'node:path';

export const digestJson=(v:unknown)=>createHash('sha256').update(JSON.stringify(v)).digest('hex');
export type NumericRule={base:number[];conditional?:Record<string,string>};
export type SpellRule={slot:number|null;damageType:string|null;behavior:string[];gates:string[];values:Record<string,NumericRule>};
export type Rules={format:'valve-spell-rules-v1';patch:string;build:number;commit:string;steamInfSha256:string;
  heroesTree:string;itemsBlob:string;heroes:Record<string,{blob:string;spells:Record<string,SpellRule>}>;
  items:Record<string,SpellRule>};
export function rulesFile(root:string,patch:string,build:number){
  assert(/^\d+\.\d+[a-z]?$/.test(patch)&&Number.isSafeInteger(build)&&build>0);
  return path.join(root,'spell-rules',patch,`${build}.json`);
}
export function loadRules(root:string,patch:string,build:number):Rules|null{
  const f=rulesFile(root,patch,build);if(!fs.existsSync(f))return null;
  assert(fs.statSync(f).size<16*1024**2,'Oversized spell rules');
  const s=JSON.parse(fs.readFileSync(f,'utf8')),r:Rules=s.rules;
  assert(r?.format==='valve-spell-rules-v1'&&r.patch===patch&&r.build===build&&digestJson(r)===s.sha256&&
    /^[a-f0-9]{64}$/.test(r.steamInfSha256)&&[r.commit,r.heroesTree,r.itemsBlob].every(h=>/^[a-f0-9]{40}$/.test(h)),'Corrupt/mismatched build-specific spell rules');
  return r;
}
export const defensiveItems=['item_infused_raindrop','item_faerie_fire','item_magic_stick','item_magic_wand','item_holy_locket',
  'item_black_king_bar','item_hood_of_defiance','item_pipe','item_glimmer_cape','item_eternal_shroud','item_mage_slayer',
  'item_aeon_disk','item_sphere','item_lotus_orb','item_blade_mail','item_ghost','item_manta','item_force_staff',
  'item_hurricane_pike','item_blink','item_wind_waker','item_cyclone','item_satanic','item_crimson_guard','item_vanguard','item_armlet'];
