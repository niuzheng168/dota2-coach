import { DEFINITIONS, ITEM_LABELS, itemValue } from './data.ts';
import { clamp } from '../tactics/rules.ts';
export interface Loadout {items:string[];treads:'str'|'agi'|'int'}
export interface HeroAttributes {
  strength:number;agility:number;intelligence:number;maxHealth:number;maxMana:number;damageMin:number;damageMax:number;
  armor:number;magicResistance:number;attackSpeed:number;attackInterval:number;attackPoint:number;attackRange:number;
  moveSpeed:number;projectileSpeed:number;healthRegen:number;manaRegen:number;critChance:number;critMultiplier:number;lifesteal:number;
  critSources:{chance:number;multiplier:number}[];
}
export function validateLoadout(value:unknown):Loadout {
  const v=value as Loadout;
  if(!v||!Array.isArray(v.items)||v.items.length>6||v.items.some(n=>typeof n!=='string'||!Object.hasOwn(ITEM_LABELS,n))||
    new Set(v.items).size!==v.items.length||!['str','agi','int'].includes(v.treads))throw new Error('装备须为目录内至多六件、不重复；指定假腿属性');
  return {items:[...v.items],treads:v.treads};
}
export function heroAttributes(name:string,level:number,loadout:Loadout,maskActive=false):HeroAttributes {
  const h=DEFINITIONS.heroes[name];
  if(!h||!Number.isInteger(level)||level<1||level>30)throw new Error('未注册英雄或等级无效');
  const items=validateLoadout(loadout).items;
  let [str,agi,int]=h.attributes.map((n,i)=>n+h.gains[i]*(level-1));
  let damage=0,armor=0,as=0,boots=0,health=0,hpRegen=0,mpRegen=0,magicFactor=1,lifesteal=0;
  const critSources:HeroAttributes['critSources']=[];
  for(const name of items){
    const v=(k:string)=>itemValue(name,k);
    str+=v('bonus_strength')+v('bonus_all_stats');agi+=v('bonus_agility')+v('bonus_all_stats');
    int+=v('bonus_intellect')+v('bonus_intelligence')+v('bonus_all_stats');
    damage+=v('bonus_damage');armor+=v('bonus_armor');as+=v('bonus_attack_speed');health+=v('bonus_health');
    hpRegen+=v('bonus_health_regen');mpRegen+=v('bonus_mana_regen');magicFactor*=1-v('bonus_magical_armor')/100;
    if(name==='item_power_treads'){
      boots=Math.max(boots,v('bonus_movement_speed_ranged'));
      if(loadout.treads==='str')str+=v('bonus_stat');else if(loadout.treads==='agi')agi+=v('bonus_stat');else int+=v('bonus_stat');
    }
    if(name==='item_mask_of_madness')lifesteal+=v('lifesteal_percent')/100;
    if(v('crit_chance'))critSources.push({chance:v('crit_chance')/100,multiplier:v('crit_multiplier')/100});
  }
  const active=maskActive&&items.includes('item_mask_of_madness');
  if(active){as+=itemValue('item_mask_of_madness','berserk_bonus_attack_speed');armor-=itemValue('item_mask_of_madness','berserk_armor_reduction');}
  const primary=h.primary==='all'?(str+agi+int)*.45:h.primary==='str'?str:h.primary==='agi'?agi:int;
  if(name==='npc_dota_hero_luna')damage+=level*2;
  const attackSpeed=clamp(h.attackSpeed+agi+as,20,700);
  return {strength:str,agility:agi,intelligence:int,maxHealth:h.baseHealth+str*22+health,maxMana:h.baseMana+int*12,
    damageMin:h.damageMin+primary+damage,damageMax:h.damageMax+primary+damage,armor:h.armor+agi/6+armor,
    magicResistance:1-(1-clamp(h.magicResistance+int*.001,0,1))*magicFactor,
    attackSpeed,attackInterval:h.attackTime*100/attackSpeed,attackPoint:h.attackPoint*100/attackSpeed,attackRange:h.attackRange,
    moveSpeed:clamp((h.moveSpeed+boots)*(active?1+itemValue('item_mask_of_madness','berserk_bonus_movement_speed')/100:1),100,550),
    projectileSpeed:h.projectileSpeed,healthRegen:h.healthRegen+str*.1+hpRegen,manaRegen:h.manaRegen+int*.05+mpRegen,
    critChance:1-critSources.reduce((s,c)=>s*(1-c.chance),1),critMultiplier:Math.max(1,...critSources.map(c=>c.multiplier)),
    lifesteal,critSources:critSources.sort((a,b)=>b.multiplier-a.multiplier)};
}
