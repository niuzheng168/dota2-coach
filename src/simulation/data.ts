import fs from 'node:fs';
import { COMBAT } from '../tactics/combat-data.ts';
import { GridNavigation } from '../tactics/map-data.ts';
import type { MapData, Point } from '../tactics/types.ts';

export interface UnitSpec {
  health:number;mana:number;healthRegen:number;manaRegen:number;armor:number;magicResistance:number;
  damageMin:number;damageMax:number;attackTime:number;attackSpeed:number;attackPoint:number;attackRange:number;
  acquisitionRange:number;projectileSpeed:number;moveSpeed:number;turnRate:number;goldMin:number;goldMax:number;hullName:string;
}
export interface HeroSpec extends UnitSpec {
  baseHealth:number;baseMana:number;attributes:number[];gains:number[];primary:'str'|'agi'|'int'|'all';label:string;
}
export const DEFINITIONS=JSON.parse(fs.readFileSync(new URL('../../data/dota2-simulator-definitions.json',import.meta.url),'utf8')) as {
  version:number;patch:string;serverBuild:number;units:Record<string,UnitSpec>;heroes:Record<string,HeroSpec>
};
export const CLIENT_MAP=JSON.parse(fs.readFileSync(new URL('../../data/dota2-client-map.json',import.meta.url),'utf8'));
export const REFERENCE=JSON.parse(fs.readFileSync(new URL('../../data/dota2-map-reference.json',import.meta.url),'utf8'));
if(DEFINITIONS.serverBuild!==CLIENT_MAP.serverBuild)throw new Error('Simulator map/units build mismatch');
// Runtime GridNav point queries do not contain standing tower hulls. Add the
// measured 144-unit tower hull plus the 24-unit hero footprint for navigation.
// This is a conservative initial obstacle grid, not swept-hull engine physics.
const blockedRows=CLIENT_MAP.grid.rows.map((row:string,y:number)=>[...row].map((cell,x)=>{
  const p={x:CLIENT_MAP.grid.origin.x+(x+.5)*64,y:CLIENT_MAP.grid.origin.y+(y+.5)*64};
  return CLIENT_MAP.towers.some((t:any)=>Math.hypot(t.position[0]-p.x,t.position[1]-p.y)<t.hull+24)?'0':cell;
}).join(''));
export const SIM_GRID={...CLIENT_MAP.grid,rows:blockedRows};
export const NAV=new GridNavigation({...REFERENCE,grid:SIM_GRID} as MapData);
export const FRAME_SECONDS=0.2;
export const NEUTRAL_SPAWN_TICKS=300;
export const MAX_TICKS=3000; // Ten minutes, all 200ms frames addressable.
export const ITEM_LABELS:Record<string,string>={
  item_power_treads:'动力鞋',item_mask_of_madness:'疯狂面具',item_greater_crit:'代达罗斯之殇',item_lesser_crit:'水晶剑',
  item_platemail:'板甲',item_cloak:'抗魔斗篷',item_gloves:'加速手套',item_blades_of_attack:'攻击之爪',
  item_ogre_axe:'食人魔之斧',item_blade_of_alacrity:'欢欣之刃',item_staff_of_wizardry:'魔力法杖',item_bracer:'护腕',
};
export const itemValue=(name:string,key:string)=>COMBAT.items[name]?.[key]?.[0]||0;
export function isWalkable(p:Point):boolean {
  const g=SIM_GRID,x=Math.floor((p.x-g.origin.x)/g.cellSize),y=Math.floor((p.y-g.origin.y)/g.cellSize);
  return g.rows[y]?.[x]==='1';
}
export function nearestWalkable(p:Point,rings=6):Point|null {
  if(isWalkable(p))return {x:p.x,y:p.y};
  const g=SIM_GRID,cx=Math.floor((p.x-g.origin.x)/64),cy=Math.floor((p.y-g.origin.y)/64);
  for(let r=0;r<=rings;r++){
    const candidates:Point[]=[];
    for(let y=cy-r;y<=cy+r;y++)for(let x=cx-r;x<=cx+r;x++)if(Math.max(Math.abs(x-cx),Math.abs(y-cy))===r&&g.rows[y]?.[x]==='1')
      candidates.push({x:g.origin.x+(x+.5)*64,y:g.origin.y+(y+.5)*64});
    if(candidates.length)return candidates.sort((a,b)=>Math.hypot(a.x-p.x,a.y-p.y)-Math.hypot(b.x-p.x,b.y-p.y))[0];
  }return null;
}
export const LIMITATIONS=[
  '固定 200ms 确定性教学模拟，不是完整 Dota 客户端；不提供实战 GPM 或路线最优保证。',
  '营地、树点、塔坐标与六条兵线源自已核验客户端；使用初始树状态，不模拟自然长树和水流加速。',
  '小兵自动寻敌，塔优先当前范围内敌兵；复杂仇恨、反补、夜间视野、闪避、高低坡、反补/反伤等尚未复刻。',
  '普攻前摇、攻击间隔、弹道和防御参与结算；转向/单位间挤碰、护甲类别与建筑保护是教学简化。',
  'Luna 月刃级数随场景等级预设，月光为即时伤害测试；未复刻完整技能升级树、月光眩晕和全部技能。',
  '导航初始网格加入站立防御塔碰撞体；塔被摧毁后该导航障碍仍保留至重置，尚未重建动态网格。',
  '可自由配置最多六件装备，不扣购置金币；击杀金币与装备编辑独立。英雄等级由场景指定，不自动升级/复活。',
  '暴击为按种子独立随机抽样（非 Dota 伪随机）；仅列出的装备及其展示属性生效，同名装备不可重复。',
  '野怪普通组合按种子生成；水生进化/技能未完整模拟。历史回放不进入本模拟状态。',
];
