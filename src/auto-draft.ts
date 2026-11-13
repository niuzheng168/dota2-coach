import { DEFAULT_FOUNDRY_CONFIG, tokenManager } from './azure-auth.ts';
import type { FoundryConfig } from './azure-auth.ts';
import { DOTA2_DICTIONARY } from './dota2-glossary.ts';

// Build O(1) lookup tables across Valve internal GSI keys, numeric hero IDs, and EN/ZH names
const HERO_BY_ID = new Map<number, string>();
const HERO_BY_NORM_TOKEN = new Map<string, string>();
const CANONICAL_HERO_LIST = DOTA2_DICTIONARY.heroes.map((h) => h.en);

function normHeroToken(s: string): string {
  return s
    .trim()
    .toLowerCase()
    .replace(/['’.\-_]+/g, ' ')
    .replace(/\s+/g, ' ');
}

for (const h of DOTA2_DICTIONARY.heroes) {
  HERO_BY_ID.set(h.id, h.en);
  // Valve full GSI key: e.g. "npc_dota_hero_obsidian_destroyer"
  HERO_BY_NORM_TOKEN.set(h.key.toLowerCase(), h.en);
  HERO_BY_NORM_TOKEN.set(normHeroToken(h.key), h.en);
  // Valve short draft class: e.g. "obsidian_destroyer", "skeleton_king", "zuus", "wisp", "furion"
  const shortKey = h.key.replace(/^npc_dota_hero_/i, '');
  HERO_BY_NORM_TOKEN.set(shortKey.toLowerCase(), h.en);
  HERO_BY_NORM_TOKEN.set(normHeroToken(shortKey), h.en);
  // Canonical EN & ZH
  HERO_BY_NORM_TOKEN.set(h.en.toLowerCase(), h.en);
  HERO_BY_NORM_TOKEN.set(normHeroToken(h.en), h.en);
  HERO_BY_NORM_TOKEN.set(h.zh.trim(), h.en);

  for (const alias of h.enAliases ?? []) {
    if (alias.trim().length >= 2) {
      HERO_BY_NORM_TOKEN.set(normHeroToken(alias), h.en);
    }
  }
  for (const slang of h.zhSlang ?? []) {
    if (slang.trim()) {
      HERO_BY_NORM_TOKEN.set(slang.trim().toLowerCase(), h.en);
    }
  }
}

export function resolveCanonicalHero(raw: unknown): string | null {
  if (typeof raw === 'number' && Number.isFinite(raw) && raw > 0) {
    return HERO_BY_ID.get(raw) ?? null;
  }
  if (typeof raw !== 'string') return null;
  const trimmed = raw.trim();
  if (!trimmed || trimmed === '0') return null;

  if (/^\d+$/.test(trimmed)) {
    const id = Number(trimmed);
    if (id > 0) return HERO_BY_ID.get(id) ?? null;
  }

  return (
    HERO_BY_NORM_TOKEN.get(trimmed.toLowerCase()) ??
    HERO_BY_NORM_TOKEN.get(normHeroToken(trimmed)) ??
    null
  );
}

// ============================================================================
// Option 2: Official Valve DOTA2 Game State Integration (GSI)
// ============================================================================

export interface RoomGsiState {
  matchId: string | null;
  gameState: string | null;
  lastHeartbeatAt: number | null;
  lastSourceMode: 'spectator_or_coach' | 'player' | 'idle';
}

export interface GsiParseResult {
  matchId: string | null;
  gameState: string | null;
  clockTime: number | null;
  resetHeroes: boolean;
  detectedHeroes: string[];
  sourceMode: 'spectator_or_coach' | 'player' | 'idle';
}

const DRAFT_ENTRY_STATES = new Set([
  'DOTA_GAMERULES_STATE_WAIT_FOR_PLAYERS_TO_LOAD',
  'DOTA_GAMERULES_STATE_HERO_SELECTION',
]);

const NON_ACTIVE_MATCH_STATES = new Set([
  'DOTA_GAMERULES_STATE_INIT',
  'DOTA_GAMERULES_STATE_POST_GAME',
  'DOTA_GAMERULES_STATE_DISCONNECT',
]);

export function parseDota2GsiPayload(
  payload: Record<string, any>,
  prev: RoomGsiState,
): GsiParseResult {
  const mapObj = payload?.map && typeof payload.map === 'object' ? payload.map : null;
  const rawMatchId = mapObj?.matchid !== undefined ? String(mapObj.matchid).trim() : '';
  const matchId = rawMatchId && rawMatchId !== '0' ? rawMatchId : prev.matchId;
  const gameState = typeof mapObj?.game_state === 'string' ? mapObj.game_state : prev.gameState;
  const clockTime = typeof mapObj?.clock_time === 'number' ? mapObj.clock_time : null;

  // Detect new game start -> automatically clear previous game's heroes!
  let resetHeroes = false;
  if (rawMatchId && rawMatchId !== '0' && prev.matchId && rawMatchId !== prev.matchId) {
    resetHeroes = true;
  } else if (
    gameState &&
    DRAFT_ENTRY_STATES.has(gameState) &&
    prev.gameState &&
    !DRAFT_ENTRY_STATES.has(prev.gameState) &&
    NON_ACTIVE_MATCH_STATES.has(prev.gameState)
  ) {
    resetHeroes = true;
  }

  const found: string[] = [];
  const pushHero = (candidate: unknown) => {
    const canonical = resolveCanonicalHero(candidate);
    if (canonical && !found.includes(canonical) && found.length < 10) {
      found.push(canonical);
    }
  };

  let isMultiHeroPayload = false;

  // 1. Draft block (Coach slot / Captain's Mode / Spectator during BP)
  const draft = payload?.draft;
  if (draft && typeof draft === 'object') {
    for (const teamKey of ['team2', 'team3']) {
      const teamDraft = draft[teamKey];
      if (teamDraft && typeof teamDraft === 'object') {
        isMultiHeroPayload = true;
        for (let i = 0; i < 10; i++) {
          if (`pick${i}_class` in teamDraft) pushHero(teamDraft[`pick${i}_class`]);
          if (`pick${i}_id` in teamDraft) pushHero(teamDraft[`pick${i}_id`]);
        }
      }
    }
  }

  // 2. Hero block — either Spectator/Coach nested `team2`/`team3` OR single-player `{ name, id }`
  const hero = payload?.hero;
  if (hero && typeof hero === 'object') {
    if (hero.team2 || hero.team3) {
      isMultiHeroPayload = true;
      for (const teamKey of ['team2', 'team3']) {
        const teamHeroes = hero[teamKey];
        if (teamHeroes && typeof teamHeroes === 'object') {
          for (const slot of Object.values(teamHeroes) as any[]) {
            if (slot && typeof slot === 'object') {
              pushHero(slot.name ?? slot.id);
            }
          }
        }
      }
    } else if (hero.name || hero.id) {
      pushHero(hero.name ?? hero.id);
    }
  }

  const sourceMode: GsiParseResult['sourceMode'] = isMultiHeroPayload
    ? 'spectator_or_coach'
    : found.length > 0
      ? 'player'
      : prev.lastSourceMode || 'idle';

  return {
    matchId,
    gameState,
    clockTime,
    resetHeroes,
    detectedHeroes: found,
    sourceMode,
  };
}

export function generateDota2GsiConfigFile(serverOrigin: string, roomId: string, token: string): string {
  const safeRoom = encodeURIComponent(roomId.trim() || 'roshan-pit');
  const uri = `${serverOrigin.replace(/\/+$/, '')}/api/gsi?room=${safeRoom}`;
  return `"DOTA2 Bilingual Voice Coach Integration"
{
    "uri"           "${uri}"
    "timeout"       "5.0"
    "buffer"        "0.5"
    "throttle"      "1.0"
    "heartbeat"     "15.0"
    "auth"
    {
        "room"          "${safeRoom}"
        "token"         "${token}"
    }
    "data"
    {
        "provider"      "1"
        "map"           "1"
        "player"        "1"
        "hero"          "1"
        "draft"         "1"
    }
}
`;
}

// ============================================================================
// Option 3: Browser Window Top-Bar Vision Scanner (powered by gpt-6-luna)
// ============================================================================

export interface VisionDraftResult {
  phase: 'draft' | 'in_game' | 'menu_or_other';
  radiant: string[];
  dire: string[];
  heroes: string[];
  model: string;
  latencyMs: number;
}

const VISION_SYSTEM_PROMPT = `You are a DOTA 2 HUD & Draft top-bar hero portrait recognizer.
Identify the picked DOTA 2 heroes visible in the Radiant (5 slots) and Dire (5 slots) top bar.
Layout note:
- In a single horizontal strip, Radiant's 5 slots are on the LEFT of the center clock/VS and Dire's 5 slots are on the RIGHT.
- In a 2-row stacked crop of the top bar, the TOP half is the left side (Radiant 5 slots + clock) and the BOTTOM half is the right side (clock + Dire 5 slots).
Rules:
1. Return ONLY heroes whose portraits are clearly locked/picked in the Radiant and Dire slots.
2. Ignore banned heroes (red slash / greyed-out ban row), empty slots, or main-menu screens.
3. If the image is NOT in DOTA 2 hero selection, strategy/versus, or an active match HUD, set phase = "menu_or_other" and return empty arrays.
4. Every hero name MUST be chosen strictly from this canonical DOTA 2 hero list:
${CANONICAL_HERO_LIST.join(', ')}`;

const VISION_JSON_SCHEMA = {
  type: 'json_schema',
  name: 'dota2_topbar_draft',
  strict: true,
  schema: {
    type: 'object',
    additionalProperties: false,
    required: ['phase', 'radiant', 'dire'],
    properties: {
      phase: {
        type: 'string',
        enum: ['draft', 'in_game', 'menu_or_other'],
      },
      radiant: {
        type: 'array',
        items: { type: 'string' },
      },
      dire: {
        type: 'array',
        items: { type: 'string' },
      },
    },
  },
};

export async function recognizeDraftFromTopBarImage(
  imageDataUrl: string,
  config: FoundryConfig = DEFAULT_FOUNDRY_CONFIG,
): Promise<VisionDraftResult> {
  if (!imageDataUrl.startsWith('data:image/')) {
    throw new Error('Expected a data:image/* base64 URL');
  }

  const model = config.deployments.visionLlm;
  const authHeaders = await tokenManager.getAuthHeaders();
  const t0 = performance.now();

  const body: Record<string, unknown> = {
    model,
    input: [
      {
        role: 'developer',
        content: [{ type: 'input_text', text: VISION_SYSTEM_PROMPT }],
      },
      {
        role: 'user',
        content: [
          {
            type: 'input_text',
            text: 'Extract the picked Radiant and Dire heroes from this DOTA 2 top bar frame.',
          },
          {
            type: 'input_image',
            image_url: imageDataUrl,
            detail: 'high',
          },
        ],
      },
    ],
    text: {
      format: VISION_JSON_SCHEMA,
    },
  };

  if (model.startsWith('gpt-6')) {
    body.reasoning = { effort: 'none' };
  } else if (model.startsWith('gpt-5')) {
    body.reasoning = { effort: 'minimal' };
  }

  const resp = await fetch(`https://${config.openAiHost}/openai/v1/responses`, {
    method: 'POST',
    headers: {
      ...authHeaders,
      'Content-Type': 'application/json',
    },
    body: JSON.stringify(body),
  });

  if (!resp.ok) {
    const errBody = await resp.text().catch(() => '');
    throw new Error(`Vision API HTTP ${resp.status}: ${errBody.slice(0, 260)}`);
  }

  const data: any = await resp.json();
  const latencyMs = Math.max(1, Math.round(performance.now() - t0));

  let rawText = typeof data.output_text === 'string' ? data.output_text : '';
  if (!rawText && Array.isArray(data.output)) {
    for (const item of data.output) {
      for (const c of item?.content ?? []) {
        if (typeof c?.text === 'string') {
          rawText += c.text;
        }
      }
    }
  }

  const parsed = JSON.parse(rawText || '{"phase":"menu_or_other","radiant":[],"dire":[]}');
  const radiant: string[] = [];
  const dire: string[] = [];
  const all: string[] = [];

  for (const name of Array.isArray(parsed.radiant) ? parsed.radiant : []) {
    const canonical = resolveCanonicalHero(name);
    if (canonical && !all.includes(canonical) && radiant.length < 5) {
      radiant.push(canonical);
      all.push(canonical);
    }
  }
  for (const name of Array.isArray(parsed.dire) ? parsed.dire : []) {
    const canonical = resolveCanonicalHero(name);
    if (canonical && !all.includes(canonical) && dire.length < 5) {
      dire.push(canonical);
      all.push(canonical);
    }
  }

  const phase =
    parsed.phase === 'draft' || parsed.phase === 'in_game' ? parsed.phase : 'menu_or_other';

  return {
    phase,
    radiant,
    dire,
    heroes: all.slice(0, 10),
    model,
    latencyMs,
  };
}
