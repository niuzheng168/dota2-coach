import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const __dirname = path.dirname(fileURLToPath(import.meta.url));
const DICT_PATH = path.resolve(__dirname, '../data/dota2-dictionary.json');

export interface TacticalTermEntry {
  category: string;
  en: string;
  enAliases: string[];
  zh: string;
  zhAliases: string[];
  note?: string;
}

export interface HeroAbilityEntry {
  key: string;
  en: string;
  zh: string;
  isInnate: boolean;
  isShard: boolean;
  isScepter: boolean;
  enAliases: string[];
  zhSlang: string[];
}

export interface HeroEntry {
  id: number;
  key: string;
  en: string;
  zh: string;
  enAliases: string[];
  zhSlang: string[];
  abilities: HeroAbilityEntry[];
}

export interface ItemEntry {
  id: number;
  key: string;
  en: string;
  zh: string;
  neutralTier: number;
  isEnhancement: boolean;
  enAliases: string[];
  zhSlang: string[];
}

export interface Dota2Dictionary {
  generatedAt: string;
  source: string;
  counts: {
    tacticalTerms: number;
    heroes: number;
    abilities: number;
    shopItems: number;
    neutralAndEnhancements: number;
  };
  tacticalTerms: TacticalTermEntry[];
  heroes: HeroEntry[];
  shopItems: ItemEntry[];
  neutralItems: ItemEntry[];
}

export const DOTA2_DICTIONARY: Dota2Dictionary = JSON.parse(
  fs.readFileSync(DICT_PATH, 'utf8'),
) as Dota2Dictionary;

// Backward-compatible summary list for compact UI badges / quick overview
export interface GlossaryEntry {
  en: string;
  zh: string;
  aliases?: string[];
  category: 'laning' | 'objectives' | 'items' | 'teamfight' | 'roles' | 'heroes' | 'abilities';
}

export const DOTA2_GLOSSARY: GlossaryEntry[] = DOTA2_DICTIONARY.tacticalTerms.map((t) => {
  let category: GlossaryEntry['category'] = 'teamfight';
  if (t.category.includes('Laning')) category = 'laning';
  else if (t.category.includes('Objectives') || t.category.includes('Vision')) category = 'objectives';
  else if (t.category.includes('Roles')) category = 'roles';
  return {
    en: t.en,
    zh: t.zh,
    aliases: [...t.enAliases, ...t.zhAliases],
    category,
  };
});

// Explicit high-confusion nickname disambiguation notes
const HERO_DISAMBIGUATION_NOTES: Record<string, string> = {
  npc_dota_hero_phantom_lancer: 'WARNING: Chinese "猴子" ALWAYS means Phantom Lancer (PL / 幻影长矛手), NEVER Monkey King!',
  npc_dota_hero_monkey_king: 'Chinese nickname is "大圣 / 齐天大圣 / MK" (whereas "猴子" = Phantom Lancer!)',
  npc_dota_hero_earthshaker: '"小牛 / 牛头" = Earthshaker (ES), NOT Elder Titan ("大牛") or Spirit Breaker ("白牛")',
  npc_dota_hero_elder_titan: '"大牛" = Elder Titan (ET), NOT Earthshaker ("小牛")',
  npc_dota_hero_spirit_breaker: '"白牛" = Spirit Breaker (SB / Bara)',
  npc_dota_hero_slark: '"小鱼 / 小鱼人" = Slark, NOT Slardar ("大鱼")',
  npc_dota_hero_slardar: '"大鱼 / 大鱼人" = Slardar, NOT Slark ("小鱼")',
  npc_dota_hero_naga_siren: '"小娜迦" = Naga Siren, NOT Medusa ("大娜迦 / 一姐")',
  npc_dota_hero_medusa: '"大娜迦 / 一姐" = Medusa, NOT Naga Siren ("小娜迦")',
  npc_dota_hero_drow_ranger: '"小黑" = Drow Ranger',
  npc_dota_hero_obsidian_destroyer: '"黑鸟" = Outworld Destroyer (OD); "黑鸟关" = Astral Imprisonment',
  npc_dota_hero_dark_seer: '"黑贤 / 兔子" = Dark Seer (DS)',
  npc_dota_hero_storm_spirit: '"蓝猫" = Storm Spirit',
  npc_dota_hero_ember_spirit: '"火猫" = Ember Spirit',
  npc_dota_hero_earth_spirit: '"土猫" = Earth Spirit',
  npc_dota_hero_void_spirit: '"紫猫" = Void Spirit',
  npc_dota_hero_enchantress: '"小鹿" = Enchantress, NOT Leshrac ("老鹿")',
  npc_dota_hero_leshrac: '"老鹿" = Leshrac, NOT Enchantress ("小鹿")',
  npc_dota_hero_life_stealer: '"小狗" = Lifestealer (LS / Naix)',
  npc_dota_hero_shadow_demon: '"毒狗" = Shadow Demon (SD); "毒狗关" = Disruption',
  npc_dota_hero_arc_warden: '"电狗" = Arc Warden',
  npc_dota_hero_clinkz: '"小骷髅 / 骨弓" = Clinkz, NOT Wraith King ("骷髅王")',
  npc_dota_hero_skeleton_king: '"骷髅王" = Wraith King (WK)',
};

// Conservative ASR homophone & misrecognition fixes for unmistakable multi-char DOTA2 phrases
const ZH_ASR_FIXES: Array<[RegExp, string]> = [
  [/堆也|对野|队野/g, '堆野'],
  [/屯也|囤也/g, '屯野'],
  [/堆远骨|屯远骨|刷远骨/g, '屯远古'],
  [/反演|翻眼|返眼/g, '反眼'],
  [/分身府|分身釜|分身扶/g, '分身斧'],
  [/黑黄杖|黑黄棒/g, '黑皇杖'],
  [/肉杉|肉山坑儿/g, '肉山'],
  [/不朽顿/g, '不朽盾'],
  [/痛苦魔坊/g, '痛苦魔方'],
  [/灵瓜一闪/g, '灵呱一闪'],
  [/金孤棒/g, '金箍棒'],
  [/缚灵锁/g, '缚灵索'],
  [/紫院/g, '紫怨'],
];

export function normalizeDota2AsrText(text: string): string {
  let out = text;
  for (const [pattern, replacement] of ZH_ASR_FIXES) {
    out = out.replace(pattern, replacement);
  }
  return out;
}

interface SearchableEntity {
  id: string;
  priority: number;
  summary: string;
  cjkTriggers: string[];
  asciiRegexes: RegExp[];
}

function escapeRegex(s: string): string {
  return s.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
}

function buildAsciiRegex(term: string): RegExp | null {
  const clean = term.trim();
  if (!clean || clean.length < 2) return null;
  // Short 2-3 char uppercase abbreviations (PL, MK, BKB, OD, ES, RP, CS, HG, TP, DD)
  if (/^[A-Z0-9&'-]{2,3}$/.test(clean)) {
    // Avoid ambiguous common English words like "IT", "IN", "AT", "ON", "OR", "AS", "SO", "NO", "GO", "DO", "UP"
    const stop2 = new Set(['IN', 'IT', 'AT', 'ON', 'OR', 'AS', 'SO', 'NO', 'GO', 'DO', 'UP', 'TO', 'BE', 'BY', 'IF']);
    if (stop2.has(clean)) return null;
    // Match uppercase exact OR case-insensitive for very distinctive DOTA2 tags
    const caseInsensitiveOk = new Set([
      'bkb', 'mkb', 'tp', 'cd', 'hg', 'cs', 'pl', 'mk', 'od', 'pa', 'ta', 'tb', 'wk', 'ck', 'dk',
      'dp', 'ds', 'am', 'cm', 'sf', 'sk', 'wd', 'wr', 'ww', 'lc', 'ld', 'ls', 'np', 'ns', 'nyx',
      'qop', 'kotl', 'sny', 'kns', 'ynk', 'ac', 'bf', 'pt', 'mom', 'oov', 'ooc',
    ]);
    const flags = caseInsensitiveOk.has(clean.toLowerCase()) ? 'i' : '';
    return new RegExp(`\\b${escapeRegex(clean)}\\b`, flags);
  }
  if (clean.length < 3) return null;
  return new RegExp(`\\b${escapeRegex(clean)}\\b`, 'i');
}

function splitSlashTerms(raw: string): string[] {
  return raw
    .split(/[/|]/)
    .map((x) => x.replace(/（[^）]*）|\([^)]*\)/g, '').trim())
    .filter(Boolean);
}

const SEARCH_INDEX: SearchableEntity[] = (() => {
  const list: SearchableEntity[] = [];

  // 1. Heroes
  for (const h of DOTA2_DICTIONARY.heroes) {
    const cjk = new Set<string>();
    const ascii: RegExp[] = [];

    if (h.zh.length >= 2) cjk.add(h.zh);
    for (const s of h.zhSlang) {
      if (/[\u3400-\u9FFF]/.test(s) && s.length >= 2) cjk.add(s);
      else {
        const rx = buildAsciiRegex(s);
        if (rx) ascii.push(rx);
      }
    }
    const enRx = buildAsciiRegex(h.en);
    if (enRx) ascii.push(enRx);
    for (const a of h.enAliases) {
      const rx = buildAsciiRegex(a);
      if (rx) ascii.push(rx);
    }

    const note = HERO_DISAMBIGUATION_NOTES[h.key] ? ` [${HERO_DISAMBIGUATION_NOTES[h.key]}]` : '';
    const enAliasStr = h.enAliases.length ? ` (${h.enAliases.join('/')})` : '';
    const zhAliasStr = h.zhSlang.length ? ` (${h.zhSlang.join('/')})` : '';
    const keyAbilities = h.abilities
      .map((ab) => `${ab.en}=${ab.zh}${ab.zhSlang.length ? `/${ab.zhSlang[0]}` : ''}`)
      .join(', ');

    list.push({
      id: `hero:${h.key}`,
      priority: 1,
      summary: `Hero: ${h.en}${enAliasStr} <=> ${h.zh}${zhAliasStr}${note} | Spells: ${keyAbilities}`,
      cjkTriggers: Array.from(cjk),
      asciiRegexes: ascii,
    });

    // 2. Hero Abilities (all 734)
    for (const ab of h.abilities) {
      const abCjk = new Set<string>();
      const abAscii: RegExp[] = [];
      if (ab.zh.length >= 2) abCjk.add(ab.zh);
      for (const s of ab.zhSlang) {
        if (/[\u3400-\u9FFF]/.test(s) && s.length >= 2) abCjk.add(s);
      }
      // Avoid ultra-generic single English words like "Grow", "Toss", "Hook", "Hex" firing without context?
      // Actually including a 1-line hint for "Toss <=> 投掷 (Tiny)" is harmless and genuinely helpful!
      const rx = buildAsciiRegex(ab.en);
      if (rx) abAscii.push(rx);
      for (const a of ab.enAliases) {
        const arx = buildAsciiRegex(a);
        if (arx) abAscii.push(arx);
      }

      const slangNote =
        ab.enAliases.length || ab.zhSlang.length
          ? ` (aliases: ${[...ab.enAliases, ...ab.zhSlang].join('/')})`
          : '';
      list.push({
        id: `ability:${ab.key}`,
        priority: 2,
        summary: `Ability (${h.en}/${h.zh}): ${ab.en} <=> ${ab.zh}${slangNote}`,
        cjkTriggers: Array.from(abCjk),
        asciiRegexes: abAscii,
      });
    }
  }

  // 3. Items (Shop + Neutral)
  for (const it of [...DOTA2_DICTIONARY.shopItems, ...DOTA2_DICTIONARY.neutralItems]) {
    const cjk = new Set<string>();
    const ascii: RegExp[] = [];
    if (it.zh.length >= 2) cjk.add(it.zh);
    for (const s of it.zhSlang) {
      if (/[\u3400-\u9FFF]/.test(s) && s.length >= 1) {
        // Allow distinctive 1-char item slang only when unambiguous ("粉" = Dust, "雾" = Smoke, "盾" = Aegis)
        if (s.length >= 2 || ['粉', '雾', '盾'].includes(s)) cjk.add(s);
      } else {
        const rx = buildAsciiRegex(s);
        if (rx) ascii.push(rx);
      }
    }
    const rx = buildAsciiRegex(it.en);
    if (rx) ascii.push(rx);
    for (const a of it.enAliases) {
      const arx = buildAsciiRegex(a);
      if (arx) ascii.push(arx);
    }

    const enAliasStr = it.enAliases.length ? ` (${it.enAliases.join('/')})` : '';
    const zhAliasStr = it.zhSlang.length ? ` (${it.zhSlang.join('/')})` : '';
    list.push({
      id: `item:${it.key}`,
      priority: 2,
      summary: `Item: ${it.en}${enAliasStr} <=> ${it.zh}${zhAliasStr}`,
      cjkTriggers: Array.from(cjk),
      asciiRegexes: ascii,
    });
  }

  // 4. Tactical Terms
  DOTA2_DICTIONARY.tacticalTerms.forEach((t, idx) => {
    const cjk = new Set<string>();
    const ascii: RegExp[] = [];
    for (const part of [...splitSlashTerms(t.zh), ...t.zhAliases]) {
      if (/[\u3400-\u9FFF]/.test(part) && part.length >= 2) cjk.add(part);
    }
    for (const part of [...splitSlashTerms(t.en), ...t.enAliases]) {
      const rx = buildAsciiRegex(part);
      if (rx) ascii.push(rx);
    }
    const note = t.note ? ` [${t.note}]` : '';
    list.push({
      id: `tactic:${idx}`,
      priority: 1,
      summary: `Term: ${t.en} <=> ${t.zh}${note}`,
      cjkTriggers: Array.from(cjk),
      asciiRegexes: ascii,
    });
  });

  return list;
})();

/**
 * Sub-millisecond scanner over all 127 heroes, 734 abilities, 402 items, and 43 tactical groups.
 * Returns concise high-precision glossary hints matched specifically to `text`.
 */
export function matchDota2Terms(text: string, maxMatches = 18): string[] {
  const normalized = normalizeDota2AsrText(text);
  if (!normalized.trim()) return [];

  const matched: SearchableEntity[] = [];
  for (const ent of SEARCH_INDEX) {
    let hit = false;
    for (const cjk of ent.cjkTriggers) {
      if (normalized.includes(cjk)) {
        hit = true;
        break;
      }
    }
    if (!hit) {
      for (const rx of ent.asciiRegexes) {
        if (rx.test(normalized)) {
          hit = true;
          break;
        }
      }
    }
    if (hit) {
      matched.push(ent);
    }
  }

  matched.sort((a, b) => a.priority - b.priority);
  return matched.slice(0, maxMatches).map((m) => `- ${m.summary}`);
}

const CRITICAL_DISAMBIGUATION_BLOCK = [
  'CRITICAL DOTA2 NICKNAME & SLANG DISAMBIGUATION (100% MANDATORY — NEVER VIOLATE):',
  '1. "猴子" <=> Phantom Lancer (PL / 幻影长矛手). NEVER translate Chinese "猴子" as Monkey King! "大圣 / 齐天大圣" <=> Monkey King (MK).',
  '2. "小牛 / 牛头" <=> Earthshaker (ES); "大牛" <=> Elder Titan (ET); "白牛" <=> Spirit Breaker (SB / Bara).',
  '3. "小鱼 / 小鱼人" <=> Slark; "大鱼 / 大鱼人" <=> Slardar; "小娜迦" <=> Naga Siren; "大娜迦 / 一姐" <=> Medusa.',
  '4. "蓝猫" <=> Storm Spirit; "火猫" <=> Ember Spirit; "土猫" <=> Earth Spirit; "紫猫" <=> Void Spirit.',
  '5. "小黑" <=> Drow Ranger; "黑鸟" <=> Outworld Destroyer (OD, "黑鸟关" = Astral Imprisonment); "黑贤" <=> Dark Seer (DS).',
  '6. "小鹿" <=> Enchantress; "老鹿" <=> Leshrac; "小骷髅 / 骨弓" <=> Clinkz; "骷髅王" <=> Wraith King (WK); "骨法" <=> Pugna.',
  '7. "小狗" <=> Lifestealer (LS); "毒狗" <=> Shadow Demon (SD, "毒狗关" = Disruption); "电狗" <=> Arc Warden; "毒龙" <=> Viper.',
  '8. "堆野 / 屯野" <=> stack camp / stack neutral camp (NEVER say "叠营地"); "堆远古 / 屯远古" <=> stack ancients (NEVER say "叠古树").',
  '9. "反眼 / 排眼" <=> deward; "真眼" <=> Sentry Ward (sentry); "假眼" <=> Observer Ward (obs); "高台眼" <=> cliff ward; "带粉 / 撒粉" <=> carry dust / pop dust; "开雾 / 破雾" <=> pop smoke / break smoke.',
  '10. Item Slang: "BKB / 黑皇" <=> BKB; "跳刀" <=> Blink Dagger; "分身斧" <=> Manta Style; "羊刀" <=> Scythe of Vyse (Hex); "紫怨 / 血棘" <=> Orchid / Bloodthorn; "否决" <=> Nullifier; "推推 / 大推推" <=> Force Staff / Hurricane Pike; "吹风 / 大吹风" <=> Eul\'s Scepter / Wind Waker; "微光" <=> Glimmer Cape; "绿杖" <=> Ghost Scepter; "莲花" <=> Lotus Orb; "林肯" <=> Linken\'s Sphere; "盘子" <=> Aeon Disk; "A杖 / 魔晶" <=> Aghs (Scepter) / Shard; "大晕锤" <=> Abyssal Blade; "冰眼" <=> Eye of Skadi; "撒旦" <=> Satanic; "金箍棒" <=> MKB; "大炮" <=> Daedalus; "缚灵索" <=> Gleipnir; "大骨灰" <=> Spirit Vessel.',
].join('\n');

// Precompute static compact Tier-1 reference once at startup (~3.8k tokens):
// Covers all 43 tactical groups, all 127 heroes + nicknames, all ~100 slang/core items, and all ~75 iconic abilities,
// while Tier-2 `matchDota2Terms()` dynamically injects any of the 734 abilities & 402 items per utterance!
const COMPACT_FULL_DOMAIN_REFERENCE: string = (() => {
  const tacticalLines = DOTA2_DICTIONARY.tacticalTerms.map(
    (t) => `- ${t.en} <=> ${t.zh}${t.zhAliases.length ? ` (${t.zhAliases.slice(0, 3).join('/')})` : ''}`,
  );

  const heroLines = DOTA2_DICTIONARY.heroes.map((h) => {
    const enNick = h.enAliases.length ? `(${h.enAliases.join('/')})` : '';
    const zhNick = h.zhSlang.length ? `/${h.zhSlang.join('/')}` : '';
    return `${h.en}${enNick}=${h.zh}${zhNick}`;
  });

  const iconicAbilities: string[] = [];
  for (const h of DOTA2_DICTIONARY.heroes) {
    for (const ab of h.abilities) {
      if (ab.enAliases.length > 0 || ab.zhSlang.length > 0 || h.id >= 138) {
        const aliases = [...ab.enAliases, ...ab.zhSlang];
        iconicAbilities.push(
          `${h.en}:${ab.en}=${ab.zh}${aliases.length ? `(${aliases.join('/')})` : ''}`,
        );
      }
    }
  }

  const coreItems = DOTA2_DICTIONARY.shopItems
    .filter((it) => it.enAliases.length > 0 || it.zhSlang.length > 0)
    .map((it) => {
      const enNick = it.enAliases.length ? `(${it.enAliases.join('/')})` : '';
      const zhNick = it.zhSlang.length ? `/${it.zhSlang.join('/')}` : '';
      return `${it.en}${enNick}=${it.zh}${zhNick}`;
    });

  return [
    '=== TACTICAL & COACHING CALLOUTS ===',
    ...tacticalLines,
    '',
    `=== ALL ${DOTA2_DICTIONARY.heroes.length} HEROES & NICKNAMES ===`,
    heroLines.join(' | '),
    '',
    `=== CORE ITEMS & SLANG (${coreItems.length}) ===`,
    coreItems.join(' | '),
    '',
    `=== ICONIC & NEW-HERO ABILITIES (${iconicAbilities.length}) ===`,
    iconicAbilities.join(' | '),
  ].join('\n');
})();

export function formatMatchHeroesSection(matchHeroes?: string[]): string {
  if (!matchHeroes || matchHeroes.length === 0) return '';
  const wanted = new Set(matchHeroes.map((s) => s.trim().toLowerCase()).filter(Boolean));
  const found = DOTA2_DICTIONARY.heroes.filter(
    (h) =>
      wanted.has(h.key.toLowerCase()) ||
      wanted.has(h.en.toLowerCase()) ||
      wanted.has(h.zh.toLowerCase()) ||
      h.enAliases.some((a) => wanted.has(a.toLowerCase())) ||
      h.zhSlang.some((s) => wanted.has(s.toLowerCase())),
  );
  if (found.length === 0) return '';

  const lines = [
    '=== ACTIVE HEROES IN THIS MATCH (HIGHEST PRIORITY CONTEXT) ===',
    ...found.map((h) => {
      const enNick = h.enAliases.length ? ` (${h.enAliases.join('/')})` : '';
      const zhNick = h.zhSlang.length ? ` (${h.zhSlang.join('/')})` : '';
      const note = HERO_DISAMBIGUATION_NOTES[h.key] ? ` — ${HERO_DISAMBIGUATION_NOTES[h.key]}` : '';
      const spells = h.abilities
        .map((ab) => {
          const aliases = [...ab.enAliases, ...ab.zhSlang];
          return `${ab.en} <=> ${ab.zh}${aliases.length ? ` (${aliases.join('/')})` : ''}`;
        })
        .join('; ');
      return `* ${h.en}${enNick} <=> ${h.zh}${zhNick}${note}\n  Abilities: ${spells}`;
    }),
    '',
  ];
  return lines.join('\n');
}

export function formatGlossaryForPrompt(): string {
  return COMPACT_FULL_DOMAIN_REFERENCE;
}

/**
 * Builds a prioritized ASR `phrase_list` for Azure Voice Live (`azure-speech` / `mai-transcribe-2`),
 * pinning active match heroes & their abilities first, followed by high-frequency DOTA2 objectives, items, and slang.
 */
export function buildDota2AsrPhraseList(
  role: 'coach' | 'student',
  matchHeroes?: string[],
  maxPhrases = 180,
): string[] {
  const seen = new Set<string>();
  const add = (raw: string) => {
    const clean = raw.trim();
    if (!clean || clean.length > 36 || clean.includes('/')) return;
    const key = clean.toLowerCase();
    if (!seen.has(key) && seen.size < maxPhrases) {
      seen.add(key);
      phrases.push(clean);
    }
  };
  const phrases: string[] = [];

  // 1. Active match heroes + all their abilities (highest priority during game)
  if (matchHeroes && matchHeroes.length > 0) {
    const wanted = new Set(matchHeroes.map((s) => s.trim().toLowerCase()).filter(Boolean));
    for (const h of DOTA2_DICTIONARY.heroes) {
      const isMatch =
        wanted.has(h.key.toLowerCase()) ||
        wanted.has(h.en.toLowerCase()) ||
        wanted.has(h.zh.toLowerCase()) ||
        h.enAliases.some((a) => wanted.has(a.toLowerCase())) ||
        h.zhSlang.some((s) => wanted.has(s.toLowerCase()));
      if (!isMatch) continue;
      if (role === 'coach') {
        add(h.en);
        h.enAliases.forEach(add);
        h.abilities.forEach((ab) => {
          add(ab.en);
          ab.enAliases.forEach(add);
        });
      } else {
        add(h.zh);
        h.zhSlang.forEach(add);
        add(h.en);
        h.abilities.forEach((ab) => {
          add(ab.zh);
          ab.zhSlang.forEach(add);
        });
      }
    }
  }

  // 2. High-frequency DOTA2 tactical callouts & objectives
  const coreCoachTerms = [
    'Roshan', 'Aegis', 'Tormentor', 'Wisdom Rune', 'Lotus Pool', 'Smoke of Deceit', 'pop smoke',
    'triangle', 'ancients', 'stack ancients', 'deward', 'high ground', 'buyback', 'BKB', 'Blink Dagger',
    'Manta Style', 'Scythe of Vyse', 'Nullifier', 'Gleipnir', 'Orchid', 'Bloodthorn', 'Force Staff',
    'Hurricane Pike', 'Glimmer Cape', 'Lotus Orb', 'Aeon Disk', 'Black Hole', 'Chronosphere', 'Rubick',
    'Largo', 'Croak of Genius', 'Kez', 'Ringmaster',
  ];
  const coreStudentTerms = [
    '肉山', '打盾', '不朽盾', '痛苦魔方', '魔方', '智慧神符', '经验符', '莲花池', '开雾', '破雾', '诡计之雾',
    '三角区', '远古野', '堆野', '屯远古', '拉野', '反眼', '排眼', '真眼', '假眼', '高台眼', '撒粉', '带粉',
    '上高', '高地', '买活', 'BKB', '黑皇杖', '跳刀', '分身斧', '羊刀', '紫怨', '血棘', '否决', '缚灵索',
    '推推棒', '吹风', '微光', '绿杖', '莲花', '林肯', '盘子', 'A杖', '魔晶', '大晕锤', '冰眼', '撒旦',
    '拉比克', '谜团', '黑洞', '大圣', '猴子', '小牛', '大牛', '黑鸟', '小鱼', '朗戈', '灵呱一闪', '凯',
  ];
  for (const t of role === 'coach' ? coreCoachTerms : coreStudentTerms) {
    add(t);
  }

  // 3. Fill remaining budget with hero names
  for (const h of DOTA2_DICTIONARY.heroes) {
    if (seen.size >= maxPhrases) break;
    add(role === 'coach' ? h.en : h.zh);
  }

  return phrases;
}

/**
 * Returns true if the transcript contains NO Chinese characters and looks like pure English.
 * Used for fast-path English pass-through when the Chinese student speaks basic English.
 */
export function isPureEnglishUtterance(text: string): boolean {
  const trimmed = text.trim();
  if (!trimmed) return false;
  const hasCJK = /[\u3400-\u9FFF\uF900-\uFAFF]/.test(trimmed);
  const hasLatin = /[A-Za-z]/.test(trimmed);
  return !hasCJK && hasLatin;
}

export type TranslationDirection = 'coach-en-to-zh' | 'student-zh-or-en-to-en';

export interface PromptBuildOptions {
  matchHeroes?: string[];
}

export function buildInterpreterSystemPrompt(
  direction: TranslationDirection,
  options?: PromptBuildOptions,
): string {
  const matchHeroBlock = formatMatchHeroesSection(options?.matchHeroes);

  if (direction === 'coach-en-to-zh') {
    return [
      'You are an ultra-low-latency simultaneous voice interpreter for a live DOTA2 coaching session.',
      'SPEAKER: English-speaking DOTA2 Coach.',
      'LISTENER: Chinese-speaking DOTA2 Student.',
      '',
      'STRICT INTERPRETER RULES:',
      '1. Output ONLY the spoken Simplified Chinese translation. NEVER answer questions, NEVER give coaching advice yourself, NEVER explain, NEVER add quotes or prefixes.',
      '2. QUESTION RULE: If the coach asks a question (e.g., "Do you have BKB?", "Should we Rosh now?"), translate the question into Chinese ("你有BKB吗？", "我们现在打肉山吗？") — DO NOT answer it!',
      '3. AUTHENTIC CN DOTA2 CALLOUTS: Prefer concise, idiomatic Chinese high-MMR player terminology over stiff literal translations (e.g., "stack ancients" -> "屯远古/堆远古", "deward their triangle cliff" -> "把他们三角区高台眼反了", "kite his BKB" -> "拉扯一下他BKB", "PL has Manta" -> "猴子分身斧好了", "Astral Slark" -> "黑鸟关一下小鱼"). Keep universal gamer terms natural (BKB, TP, CD, A杖, Gank, Miss).',
      '4. ASR PHONETIC RECOVERY: Spoken ASR input may have minor homophones (e.g., "Ages" -> "Aegis", "Tour mentor" -> "Tormentor", "crow of genius" -> "Croak of Genius"). Infer the intended DOTA2 term from context.',
      '5. Keep phrasing crisp and punchy for real-time voice playback.',
      '',
      CRITICAL_DISAMBIGUATION_BLOCK,
      '',
      ...(matchHeroBlock ? [matchHeroBlock] : []),
      COMPACT_FULL_DOMAIN_REFERENCE,
      '',
      'FINAL REMINDER (RECENCY CHECK):',
      '- Output ONLY the Simplified Chinese translation (never answer questions!).',
      '- Obey nickname disambiguations: PL="猴子" (MK="大圣"), Earthshaker="小牛" (ET="大牛", SB="白牛"), Slark="小鱼" (Slardar="大鱼"), OD="黑鸟" (Drow="小黑", DS="黑贤"), stack camp="堆野/屯野", stack ancients="屯远古/堆远古", deward="反眼/排眼".',
    ].join('\n');
  }

  return [
    'You are an ultra-low-latency simultaneous voice interpreter for a live DOTA2 coaching session.',
    'SPEAKER: Chinese DOTA2 Student (speaks Mandarin Chinese, mixed Chinese+English DOTA2 slang, OR basic English).',
    'LISTENER: English-speaking DOTA2 Coach.',
    '',
    'STRICT INTERPRETER RULES:',
    '1. Output ONLY the spoken English utterance for the coach. NEVER answer questions, NEVER coach, NEVER add commentary or quotes.',
    '2. ENGLISH PASS-THROUGH RULE: If the speaker says a sentence in English (even basic or slightly broken gamer English like "I no TP, wait 10 second" or "Can we go Rosh now?"), repeat/pass through their English words verbatim without paraphrasing.',
    '3. AUTHENTIC EN DOTA2 CALLOUTS: Translate Chinese or mixed Chinese-English into crisp, idiomatic English high-MMR DOTA2 callouts (e.g., "帮我堆波远古顺便把高台眼反了" -> "Stack ancients for me and deward the cliff ward", "猴子分身斧好了，大圣没大" -> "PL has Manta, Monkey King has no ult", "黑鸟关一下小鱼别让他开BKB" -> "OD Astral Slark, don\'t let him pop BKB").',
    '4. QUESTION RULE: If the speaker asks the coach a question (e.g., "这把我要先出BKB还是分身斧？"), translate the question ("Should I go BKB or Manta first this game?") — DO NOT answer it!',
    '5. ASR HOMOPHONE RECOVERY: Spoken Chinese ASR may contain homophones (e.g., "堆也/对野" -> "堆野", "反演/翻眼" -> "反眼", "分身府" -> "分身斧", "肉杉" -> "肉山"). Always interpret using DOTA2 context.',
    '',
    CRITICAL_DISAMBIGUATION_BLOCK,
    '',
    ...(matchHeroBlock ? [matchHeroBlock] : []),
    COMPACT_FULL_DOMAIN_REFERENCE,
    '',
    'FINAL REMINDER (RECENCY CHECK):',
    '- Output ONLY the English translation (or verbatim English pass-through if speaker spoke English). NEVER answer questions!',
    '- Obey nickname disambiguations: "猴子" = Phantom Lancer (PL) [NEVER Monkey King!], "大圣" = Monkey King (MK), "小牛" = Earthshaker, "大牛" = Elder Titan, "白牛" = Spirit Breaker, "小鱼" = Slark, "大鱼" = Slardar, "黑鸟" = Outworld Destroyer (OD), "小黑" = Drow Ranger, "黑贤" = Dark Seer, "堆野/屯野" = stack camp, "堆远古/屯远古" = stack ancients, "反眼/排眼" = deward.',
  ].join('\n');
}

/**
 * Builds the user turn content for text/Cascade LLM calls while keeping the system prompt 100% static
 * (preserving >98% Azure OpenAI prompt cache hits!) and injecting exact per-utterance matched terms.
 */
export function buildUserTurnWithMatchedGlossary(sourceText: string): {
  normalizedText: string;
  matchedTerms: string[];
  userPrompt: string;
} {
  const normalizedText = normalizeDota2AsrText(sourceText.trim());
  const matchedTerms = matchDota2Terms(normalizedText);
  if (matchedTerms.length === 0) {
    return { normalizedText, matchedTerms, userPrompt: normalizedText };
  }
  const userPrompt = [
    normalizedText,
    '',
    '[Matched DOTA2 Glossary Hints — apply strictly if relevant, do NOT output these notes:]',
    ...matchedTerms,
  ].join('\n');
  return { normalizedText, matchedTerms, userPrompt };
}
