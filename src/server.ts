import crypto from 'node:crypto';
import fs from 'node:fs/promises';
import http from 'node:http';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import WebSocket, { WebSocketServer } from 'ws';
import {
  generateDota2GsiConfigFile,
  parseDota2GsiPayload,
  recognizeDraftFromTopBarImage,
} from './auto-draft.ts';
import type { RoomGsiState } from './auto-draft.ts';
import { DEFAULT_FOUNDRY_CONFIG } from './azure-auth.ts';
import { DOTA2_DICTIONARY, DOTA2_GLOSSARY } from './dota2-glossary.ts';
import {
  createTranslationEngine,
  isValidTtsStreamMode,
  isValidVoiceLiveAsrModel,
} from './engines/index.ts';
import {
  DEFAULT_VOICELIVE_EN_VOICE,
  DEFAULT_VOICELIVE_ZH_VOICE,
} from './engines/voice-live-engine.ts';
import type {
  EngineKind,
  ParticipantRole,
  TranslationEngine,
  TtsOptionsUpdate,
  TtsStreamMode,
  TurnMetrics,
  VoiceLiveAsrModel,
} from './engines/index.ts';
import { AzureTtsPoolClient } from './tts-client.ts';

const __dirname = path.dirname(fileURLToPath(import.meta.url));
const PUBLIC_DIR = path.resolve(__dirname, '../public');
const DOCS_DICT_MD_PATH = path.resolve(__dirname, '../docs/dota2-bilingual-dictionary.md');
const PORT = Number(process.env.PORT || 3210);

const TTS_VOICE_CATALOG = {
  zh: [
    { id: 'zh-CN-XiaoxiaoMultilingualNeural', label: '晓晓 Multilingual (推荐 · Cascaded/Realtime同音色)' },
    { id: 'zh-CN-XiaoxiaoNeural', label: '晓晓 Neural (标准极速女声)' },
    { id: 'zh-CN-YunxiNeural', label: '云希 Neural (标准极速男声)' },
    { id: 'zh-CN-Yunyi:DragonHDFlashLatestNeural', label: '云逸 DragonHD Flash (超拟真男声)' },
    { id: 'zh-CN-Xiaochen:DragonHDFlashLatestNeural', label: '晓辰 DragonHD Flash (超拟真女声)' },
    { id: 'openai:marin', label: 'OpenAI Marin (仅 GPT-Realtime 原生音色对照)' },
    { id: 'openai:cedar', label: 'OpenAI Cedar (仅 GPT-Realtime 原生音色对照)' },
  ],
  en: [
    { id: 'en-US-Andrew:DragonHDLatestNeural', label: 'Andrew DragonHD (推荐 · Cascaded/Realtime同音色)' },
    { id: 'en-US-Ava:DragonHDLatestNeural', label: 'Ava DragonHD (极速超拟真女声)' },
    { id: 'en-US-AndrewMultilingualNeural', label: 'Andrew Multilingual (多语种男声)' },
    { id: 'en-US-EmmaMultilingualNeural', label: 'Emma Multilingual (多语种女声)' },
    { id: 'en-US-AndrewNeural', label: 'Andrew Neural (标准男声)' },
    { id: 'openai:cedar', label: 'OpenAI Cedar (仅 GPT-Realtime 原生音色对照)' },
    { id: 'openai:marin', label: 'OpenAI Marin (仅 GPT-Realtime 原生音色对照)' },
  ],
};

interface ClientConn {
  id: string;
  name: string;
  role: ParticipantRole;
  ws: WebSocket;
  selfMonitor: boolean;
  engine: TranslationEngine | null;
  startingEngine: Promise<void> | null;
}

interface RoomState {
  roomId: string;
  engineKind: EngineKind;
  englishOriginalVoiceRelay: boolean;
  matchHeroes: string[];
  ttsZhVoice: string;
  ttsEnVoice: string;
  ttsStreamMode: TtsStreamMode;
  voiceLiveAsrModel: VoiceLiveAsrModel;
  gsiState: RoomGsiState;
  lastVisionPhase: 'draft' | 'in_game' | 'menu_or_other';
  clients: Map<string, ClientConn>;
  metricsHistory: TurnMetrics[];
  demoEngines: Map<ParticipantRole, TranslationEngine>;
}

const rooms = new Map<string, RoomState>();
const demoTtsPool = new AzureTtsPoolClient();

function getOrCreateRoom(roomId: string): RoomState {
  let room = rooms.get(roomId);
  if (!room) {
    room = {
      roomId,
      engineKind: 'cascade',
      englishOriginalVoiceRelay: true,
      matchHeroes: [],
      ttsZhVoice: DEFAULT_VOICELIVE_ZH_VOICE,
      ttsEnVoice: DEFAULT_VOICELIVE_EN_VOICE,
      ttsStreamMode: 'v2-text-flush',
      voiceLiveAsrModel: 'azure-speech',
      gsiState: {
        matchId: null,
        gameState: null,
        lastHeartbeatAt: null,
        lastSourceMode: 'idle',
      },
      lastVisionPhase: 'menu_or_other',
      clients: new Map(),
      metricsHistory: [],
      demoEngines: new Map(),
    };
    rooms.set(roomId, room);
  }
  return room;
}

function broadcastJson(room: RoomState, payload: unknown): void {
  const msg = JSON.stringify(payload);
  for (const client of room.clients.values()) {
    if (client.ws.readyState === WebSocket.OPEN) {
      client.ws.send(msg);
    }
  }
}

/**
 * Binary audio frame header (2 bytes) sent from server -> browser:
 *   byte 0: 0x01 = audio from coach (translated to ZH for student),
 *           0x02 = audio from student (translated/relayed to EN for coach)
 *   byte 1: reserved (0x00)
 *   bytes 2..N: raw 24kHz 16-bit signed LE mono PCM
 */
function routeTranslatedAudio(
  room: RoomState,
  sourceRole: ParticipantRole,
  sourceClientId: string | null,
  pcm24kMono: Buffer,
): void {
  const header = Buffer.from([sourceRole === 'coach' ? 0x01 : 0x02, 0x00]);
  const packet = Buffer.concat([header, pcm24kMono]);
  const targetRole: ParticipantRole = sourceRole === 'coach' ? 'student' : 'coach';

  for (const client of room.clients.values()) {
    if (client.ws.readyState !== WebSocket.OPEN) continue;
    const isIntendedListener = client.role === targetRole;
    const isSelfMonitor = client.id === sourceClientId && client.selfMonitor;
    // Also deliver to solo tester if no listener of targetRole is connected and source is demo
    const isDemoBroadcast = sourceClientId === null;

    if (isIntendedListener || isSelfMonitor || isDemoBroadcast) {
      client.ws.send(packet, { binary: true });
    }
  }
}

function buildEngineCallbacks(
  room: RoomState,
  role: ParticipantRole,
  speakerName: string,
  sourceClientId: string | null,
) {
  return {
    onSourcePartial: (text: string) => {
      broadcastJson(room, {
        type: 'subtitle.source.partial',
        role,
        speaker: speakerName,
        text,
      });
    },
    onSourceFinal: (turnId: string, text: string) => {
      broadcastJson(room, {
        type: 'subtitle.source.final',
        turnId,
        role,
        speaker: speakerName,
        text,
      });
    },
    onTargetPartial: (turnId: string, text: string) => {
      broadcastJson(room, {
        type: 'subtitle.target.partial',
        turnId,
        role,
        speaker: speakerName,
        text,
      });
    },
    onTargetFinal: (turnId: string, text: string) => {
      broadcastJson(room, {
        type: 'subtitle.target.final',
        turnId,
        role,
        speaker: speakerName,
        text,
      });
    },
    onTargetAudio: (_turnId: string, pcm24kMono: Buffer) => {
      routeTranslatedAudio(room, role, sourceClientId, pcm24kMono);
    },
    onTurnMetrics: (metrics: TurnMetrics) => {
      room.metricsHistory.push(metrics);
      if (room.metricsHistory.length > 50) {
        room.metricsHistory.shift();
      }
      broadcastJson(room, {
        type: 'turn.metrics',
        speaker: speakerName,
        metrics,
      });
    },
    onStatus: (status: string) => {
      broadcastJson(room, {
        type: 'engine.status',
        role,
        speaker: speakerName,
        status,
      });
    },
    onError: (message: string) => {
      broadcastJson(room, {
        type: 'engine.error',
        role,
        speaker: speakerName,
        message,
      });
    },
  };
}

async function ensureClientEngine(room: RoomState, client: ClientConn): Promise<TranslationEngine> {
  if (client.engine && client.engine.kind === room.engineKind) {
    return client.engine;
  }
  if (client.startingEngine) {
    await client.startingEngine;
    if (client.engine) return client.engine;
  }

  const prev = client.engine;
  client.engine = null;
  if (prev) {
    await prev.stop().catch(() => {});
  }

  const next = createTranslationEngine(
    room.engineKind,
    {
      role: client.role,
      englishOriginalVoiceRelay: room.englishOriginalVoiceRelay,
      matchHeroes: room.matchHeroes,
      ttsZhVoice: room.ttsZhVoice,
      ttsEnVoice: room.ttsEnVoice,
      ttsStreamMode: room.ttsStreamMode,
      voiceLiveAsrModel: room.voiceLiveAsrModel,
    },
    buildEngineCallbacks(room, client.role, client.name, client.id),
  );

  client.startingEngine = next
    .start()
    .then(() => {
      client.engine = next;
    })
    .finally(() => {
      client.startingEngine = null;
    });

  await client.startingEngine;
  return next;
}

function broadcastRoomRoster(room: RoomState): void {
  const participants = Array.from(room.clients.values()).map((c) => ({
    id: c.id,
    name: c.name,
    role: c.role,
    selfMonitor: c.selfMonitor,
  }));
  broadcastJson(room, {
    type: 'room.state',
    roomId: room.roomId,
    engineKind: room.engineKind,
    englishOriginalVoiceRelay: room.englishOriginalVoiceRelay,
    matchHeroes: room.matchHeroes,
    ttsZhVoice: room.ttsZhVoice,
    ttsEnVoice: room.ttsEnVoice,
    ttsStreamMode: room.ttsStreamMode,
    voiceLiveAsrModel: room.voiceLiveAsrModel,
    gsiState: room.gsiState,
    participants,
    recentMetrics: room.metricsHistory.slice(-15),
  });
}

async function updateRoomMatchHeroes(room: RoomState, heroes: string[]): Promise<void> {
  room.matchHeroes = heroes.slice(0, 12).map((h) => String(h).trim()).filter(Boolean);
  broadcastRoomRoster(room);

  await Promise.all([
    ...Array.from(room.clients.values()).map(async (client) => {
      if (client.engine?.updateMatchHeroes) {
        await client.engine.updateMatchHeroes(room.matchHeroes);
      }
    }),
    ...Array.from(room.demoEngines.values()).map(async (eng) => {
      if (eng.updateMatchHeroes) {
        await eng.updateMatchHeroes(room.matchHeroes);
      }
    }),
  ]);
}

async function updateRoomTtsOptions(room: RoomState, opts: TtsOptionsUpdate): Promise<void> {
  if (opts.ttsZhVoice && TTS_VOICE_CATALOG.zh.some((v) => v.id === opts.ttsZhVoice)) {
    room.ttsZhVoice = opts.ttsZhVoice;
  }
  if (opts.ttsEnVoice && TTS_VOICE_CATALOG.en.some((v) => v.id === opts.ttsEnVoice)) {
    room.ttsEnVoice = opts.ttsEnVoice;
  }
  if (isValidTtsStreamMode(opts.ttsStreamMode)) {
    room.ttsStreamMode = opts.ttsStreamMode;
  }
  if (isValidVoiceLiveAsrModel(opts.voiceLiveAsrModel)) {
    room.voiceLiveAsrModel = opts.voiceLiveAsrModel;
  }
  broadcastRoomRoster(room);

  const payload: TtsOptionsUpdate = {
    ttsZhVoice: room.ttsZhVoice,
    ttsEnVoice: room.ttsEnVoice,
    ttsStreamMode: room.ttsStreamMode,
    voiceLiveAsrModel: room.voiceLiveAsrModel,
  };
  await Promise.all([
    ...Array.from(room.clients.values()).map(async (client) => {
      if (client.engine?.updateTtsOptions) {
        await client.engine.updateTtsOptions(payload);
      }
    }),
    ...Array.from(room.demoEngines.values()).map(async (eng) => {
      if (eng.updateTtsOptions) {
        await eng.updateTtsOptions(payload);
      }
    }),
  ]);
}

async function switchRoomEngine(
  room: RoomState,
  newKind: EngineKind,
  englishOriginalVoiceRelay?: boolean,
): Promise<void> {
  room.engineKind = newKind;
  if (typeof englishOriginalVoiceRelay === 'boolean') {
    room.englishOriginalVoiceRelay = englishOriginalVoiceRelay;
  }

  // Stop any cached demo engines so next demo uses the newly selected engine
  for (const eng of room.demoEngines.values()) {
    await eng.stop().catch(() => {});
  }
  room.demoEngines.clear();

  broadcastRoomRoster(room);

  await Promise.all(
    Array.from(room.clients.values()).map(async (client) => {
      const old = client.engine;
      client.engine = null;
      if (old) await old.stop().catch(() => {});
      await ensureClientEngine(room, client).catch((err) => {
        broadcastJson(room, {
          type: 'engine.error',
          role: client.role,
          speaker: client.name,
          message: err instanceof Error ? err.message : String(err),
        });
      });
    }),
  );
}

async function runDemoUtterance(
  room: RoomState,
  role: ParticipantRole,
  text: string,
  lang: 'en-US' | 'zh-CN',
): Promise<void> {
  let demoEngine = room.demoEngines.get(role);
  if (!demoEngine || demoEngine.kind !== room.engineKind) {
    if (demoEngine) await demoEngine.stop().catch(() => {});
    const speakerLabel = role === 'coach' ? 'Demo Coach (EN)' : 'Demo Student (ZH/EN)';
    demoEngine = createTranslationEngine(
      room.engineKind,
      {
        role,
        englishOriginalVoiceRelay: room.englishOriginalVoiceRelay,
        matchHeroes: room.matchHeroes,
        ttsZhVoice: room.ttsZhVoice,
        ttsEnVoice: room.ttsEnVoice,
        ttsStreamMode: room.ttsStreamMode,
        voiceLiveAsrModel: room.voiceLiveAsrModel,
      },
      buildEngineCallbacks(room, role, speakerLabel, null),
    );
    await demoEngine.start();
    room.demoEngines.set(role, demoEngine);
  }

  const voice =
    lang === 'en-US'
      ? role === 'coach'
        ? 'en-US-AndrewMultilingualNeural'
        : 'en-US-BrianMultilingualNeural'
      : 'zh-CN-YunyiMultilingualNeural';

  const synthChunks: Buffer[] = [];
  await demoTtsPool.synthesize({
    text,
    voice,
    lang,
    rate: '+0%',
    onAudioChunk: (c) => synthChunks.push(c),
  });
  const pcm = Buffer.concat(synthChunks);

  // Stream into engine in real-time 60ms frames
  const frameSize = 2880;
  for (let offset = 0; offset < pcm.length; offset += frameSize) {
    demoEngine.appendAudio(pcm.subarray(offset, Math.min(offset + frameSize, pcm.length)));
    await new Promise((r) => setTimeout(r, 60));
  }
  demoEngine.markSpeechEnd();
}

const MIME_TYPES: Record<string, string> = {
  '.html': 'text/html; charset=utf-8',
  '.js': 'application/javascript; charset=utf-8',
  '.css': 'text/css; charset=utf-8',
  '.json': 'application/json; charset=utf-8',
  '.svg': 'image/svg+xml',
};

const server = http.createServer(async (req, res) => {
  try {
    const reqUrl = new URL(req.url || '/', `http://${req.headers.host || 'localhost'}`);

    const isReadMethod = req.method === 'GET' || req.method === 'HEAD';

    if (isReadMethod && reqUrl.pathname === '/api/config') {
      let benchmark = null;
      let domainAccuracy = null;
      try {
        const raw = await fs.readFile(
          path.resolve(__dirname, '../benchmark-results/latest.json'),
          'utf8',
        );
        benchmark = JSON.parse(raw);
      } catch {
        // benchmark file optional
      }
      try {
        const rawAcc = await fs.readFile(
          path.resolve(__dirname, '../benchmark-results/domain-accuracy.json'),
          'utf8',
        );
        domainAccuracy = JSON.parse(rawAcc);
      } catch {
        // domain accuracy file optional
      }

      res.writeHead(200, { 'Content-Type': 'application/json; charset=utf-8' });
      res.end(
        req.method === 'HEAD'
          ? undefined
          : JSON.stringify({
              foundryAccount: DEFAULT_FOUNDRY_CONFIG.accountName,
              region: DEFAULT_FOUNDRY_CONFIG.region,
              deployments: DEFAULT_FOUNDRY_CONFIG.deployments,
              ttsVoiceCatalog: TTS_VOICE_CATALOG,
              glossary: DOTA2_GLOSSARY,
              dictionaryCounts: DOTA2_DICTIONARY.counts,
              benchmark,
              domainAccuracy,
            }),
      );
      return;
    }

    if (isReadMethod && reqUrl.pathname === '/api/dictionary') {
      res.writeHead(200, { 'Content-Type': 'application/json; charset=utf-8' });
      res.end(req.method === 'HEAD' ? undefined : JSON.stringify(DOTA2_DICTIONARY));
      return;
    }

    if (isReadMethod && reqUrl.pathname === '/api/dictionary.md') {
      const md = await fs.readFile(DOCS_DICT_MD_PATH, 'utf8');
      res.writeHead(200, { 'Content-Type': 'text/markdown; charset=utf-8' });
      res.end(req.method === 'HEAD' ? undefined : md);
      return;
    }

    if (req.method === 'POST' && reqUrl.pathname === '/api/demo-speak') {
      let body = '';
      for await (const chunk of req) body += chunk;
      const parsed = JSON.parse(body || '{}');
      const roomId = String(parsed.roomId || 'roshan-pit');
      const role: ParticipantRole = parsed.role === 'student' ? 'student' : 'coach';
      const text = String(parsed.text || '').trim();
      const lang: 'en-US' | 'zh-CN' = parsed.lang === 'zh-CN' ? 'zh-CN' : 'en-US';
      if (!text) {
        res.writeHead(400, { 'Content-Type': 'application/json' });
        res.end(JSON.stringify({ error: 'Missing text' }));
        return;
      }
      const room = getOrCreateRoom(roomId);
      runDemoUtterance(room, role, text, lang).catch((err) => {
        broadcastJson(room, {
          type: 'engine.error',
          role,
          speaker: 'Demo',
          message: err instanceof Error ? err.message : String(err),
        });
      });
      res.writeHead(202, { 'Content-Type': 'application/json' });
      res.end(JSON.stringify({ ok: true }));
      return;
    }

    // Option 2a: Download ready-to-drop Valve DOTA2 GSI .cfg file
    if (isReadMethod && reqUrl.pathname === '/api/gsi-config') {
      const roomId = (reqUrl.searchParams.get('room') || 'roshan-pit').slice(0, 48);
      const proto = String(req.headers['x-forwarded-proto'] || 'http').split(',')[0].trim();
      const host = req.headers.host || `localhost:${PORT}`;
      const cfgText = generateDota2GsiConfigFile(`${proto}://${host}`, roomId);
      res.writeHead(200, {
        'Content-Type': 'text/plain; charset=utf-8',
        'Content-Disposition': 'attachment; filename="gamestate_integration_dota2_coach.cfg"',
      });
      res.end(req.method === 'HEAD' ? undefined : cfgText);
      return;
    }

    // Option 2b: Receive official Valve DOTA2 Game State Integration (GSI) JSON POSTs
    if (req.method === 'POST' && reqUrl.pathname === '/api/gsi') {
      let body = '';
      let byteLen = 0;
      for await (const chunk of req) {
        byteLen += chunk.length;
        if (byteLen > 512 * 1024) break;
        body += chunk;
      }
      let parsed: Record<string, any> = {};
      try {
        parsed = JSON.parse(body || '{}');
      } catch {
        res.writeHead(400, { 'Content-Type': 'application/json' });
        res.end(JSON.stringify({ error: 'Invalid GSI JSON' }));
        return;
      }

      const roomId = (
        reqUrl.searchParams.get('room') ||
        parsed?.auth?.room ||
        parsed?.auth?.token ||
        'roshan-pit'
      )
        .toString()
        .slice(0, 48);
      const room = getOrCreateRoom(roomId);
      const gsi = parseDota2GsiPayload(parsed, room.gsiState);

      room.gsiState = {
        matchId: gsi.matchId,
        gameState: gsi.gameState,
        lastHeartbeatAt: Date.now(),
        lastSourceMode: gsi.sourceMode,
      };

      let nextHeroes = gsi.resetHeroes ? [] : [...room.matchHeroes];
      if (gsi.sourceMode === 'spectator_or_coach' && gsi.detectedHeroes.length > 0) {
        if (gsi.resetHeroes || gsi.detectedHeroes.length >= nextHeroes.length) {
          nextHeroes = gsi.detectedHeroes.slice(0, 10);
        } else {
          for (const h of gsi.detectedHeroes) {
            if (!nextHeroes.includes(h) && nextHeroes.length < 10) nextHeroes.push(h);
          }
        }
      } else if (gsi.detectedHeroes.length > 0) {
        // Player GSI reports own hero — merge without wiping heroes discovered via Vision or Coach GSI
        for (const h of gsi.detectedHeroes) {
          if (!nextHeroes.includes(h) && nextHeroes.length < 10) nextHeroes.push(h);
        }
      }

      const changed = JSON.stringify(nextHeroes) !== JSON.stringify(room.matchHeroes);
      if (changed) {
        await updateRoomMatchHeroes(room, nextHeroes);
      }

      broadcastJson(room, {
        type: 'gsi.status',
        gsiState: room.gsiState,
        clockTime: gsi.clockTime,
        resetHeroes: gsi.resetHeroes,
        heroesUpdated: changed,
      });

      res.writeHead(200, { 'Content-Type': 'application/json' });
      res.end(
        JSON.stringify({
          ok: true,
          roomId,
          matchHeroes: room.matchHeroes,
          heroesUpdated: changed,
          gameState: gsi.gameState,
        }),
      );
      return;
    }

    // Option 3: Browser DOTA2 window top-bar vision scan powered by gpt-6-luna
    if (req.method === 'POST' && reqUrl.pathname === '/api/vision-draft') {
      let body = '';
      let byteLen = 0;
      for await (const chunk of req) {
        byteLen += chunk.length;
        if (byteLen > 8 * 1024 * 1024) {
          res.writeHead(413, { 'Content-Type': 'application/json' });
          res.end(JSON.stringify({ error: 'Image payload too large' }));
          return;
        }
        body += chunk;
      }

      let parsed: any = {};
      try {
        parsed = JSON.parse(body || '{}');
      } catch {
        res.writeHead(400, { 'Content-Type': 'application/json' });
        res.end(JSON.stringify({ error: 'Invalid JSON' }));
        return;
      }

      const roomId = String(parsed.roomId || 'roshan-pit').slice(0, 48);
      const imageDataUrl = String(parsed.imageDataUrl || '');
      if (!imageDataUrl.startsWith('data:image/')) {
        res.writeHead(400, { 'Content-Type': 'application/json' });
        res.end(JSON.stringify({ error: 'Missing data:image/* payload' }));
        return;
      }

      const room = getOrCreateRoom(roomId);
      try {
        const vision = await recognizeDraftFromTopBarImage(imageDataUrl);
        const prevPhase = room.lastVisionPhase;
        room.lastVisionPhase = vision.phase;

        let nextHeroes = [...room.matchHeroes];
        if (vision.heroes.length > 0) {
          // If transitioning from a finished full game (in_game -> draft) with fewer heroes, reset for new game!
          const isNewDraftAfterGame =
            prevPhase === 'in_game' &&
            vision.phase === 'draft' &&
            room.matchHeroes.length >= 6 &&
            vision.heroes.length <= 5;

          if (isNewDraftAfterGame || parsed.replace === true || vision.heroes.length >= nextHeroes.length) {
            nextHeroes = vision.heroes.slice(0, 10);
          } else {
            for (const h of vision.heroes) {
              if (!nextHeroes.includes(h) && nextHeroes.length < 10) {
                nextHeroes.push(h);
              }
            }
          }
        }

        const changed = JSON.stringify(nextHeroes) !== JSON.stringify(room.matchHeroes);
        if (changed) {
          await updateRoomMatchHeroes(room, nextHeroes);
        }

        const statusPayload = {
          type: 'vision.status',
          phase: vision.phase,
          radiant: vision.radiant,
          dire: vision.dire,
          heroes: room.matchHeroes,
          heroesUpdated: changed,
          model: vision.model,
          latencyMs: vision.latencyMs,
          updatedAt: Date.now(),
        };
        broadcastJson(room, statusPayload);

        res.writeHead(200, { 'Content-Type': 'application/json; charset=utf-8' });
        res.end(JSON.stringify({ ok: true, ...statusPayload }));
      } catch (err) {
        const message = err instanceof Error ? err.message : String(err);
        res.writeHead(500, { 'Content-Type': 'application/json; charset=utf-8' });
        res.end(JSON.stringify({ ok: false, error: message }));
      }
      return;
    }

    // Serve static files from public/
    const safePath = reqUrl.pathname === '/' ? '/index.html' : reqUrl.pathname;
    const filePath = path.resolve(PUBLIC_DIR, `.${safePath}`);
    if (!filePath.startsWith(PUBLIC_DIR)) {
      res.writeHead(403);
      res.end('Forbidden');
      return;
    }
    const content = await fs.readFile(filePath);
    const ext = path.extname(filePath);
    res.writeHead(200, { 'Content-Type': MIME_TYPES[ext] || 'application/octet-stream' });
    res.end(content);
  } catch {
    res.writeHead(404, { 'Content-Type': 'text/plain; charset=utf-8' });
    res.end('Not Found');
  }
});

const wss = new WebSocketServer({ server, path: '/ws' });

wss.on('connection', (ws, req) => {
  const reqUrl = new URL(req.url || '/ws', `http://${req.headers.host || 'localhost'}`);
  const roomId = (reqUrl.searchParams.get('room') || 'roshan-pit').slice(0, 48);
  const role: ParticipantRole = reqUrl.searchParams.get('role') === 'coach' ? 'coach' : 'student';
  const name = (
    reqUrl.searchParams.get('name') || (role === 'coach' ? 'Coach (EN)' : 'Player (ZH)')
  ).slice(0, 32);
  const selfMonitor = reqUrl.searchParams.get('selfMonitor') === '1';

  const room = getOrCreateRoom(roomId);
  const client: ClientConn = {
    id: crypto.randomUUID().slice(0, 8),
    name,
    role,
    ws,
    selfMonitor,
    engine: null,
    startingEngine: null,
  };

  room.clients.set(client.id, client);
  broadcastRoomRoster(room);

  // Eagerly warm up outbound translation engine so first utterance has zero cold-start lag
  ensureClientEngine(room, client).catch((err) => {
    ws.send(
      JSON.stringify({
        type: 'engine.error',
        role: client.role,
        speaker: client.name,
        message: err instanceof Error ? err.message : String(err),
      }),
    );
  });

  ws.on('message', (data: WebSocket.RawData, isBinary: boolean) => {
    if (isBinary) {
      const pcm = Buffer.isBuffer(data) ? data : Buffer.from(data as ArrayBuffer);
      if (client.engine) {
        client.engine.appendAudio(pcm);
      }
      return;
    }

    let msg: any;
    try {
      msg = JSON.parse(data.toString());
    } catch {
      return;
    }

    switch (msg.type) {
      case 'speech.end': {
        client.engine?.markSpeechEnd();
        break;
      }

      case 'text.send': {
        const text = String(msg.text || '').trim();
        if (text) {
          ensureClientEngine(room, client)
            .then((eng) => eng.translateText?.(text))
            .catch((err) => {
              ws.send(
                JSON.stringify({
                  type: 'engine.error',
                  role: client.role,
                  speaker: client.name,
                  message: err instanceof Error ? err.message : String(err),
                }),
              );
            });
        }
        break;
      }

      case 'room.set_engine': {
        const kind = msg.engineKind as EngineKind;
        if (kind === 'cascade' || kind === 'gpt-realtime' || kind === 'gpt-live') {
          switchRoomEngine(room, kind, msg.englishOriginalVoiceRelay).catch((err) => {
            broadcastJson(room, {
              type: 'engine.error',
              role: client.role,
              speaker: client.name,
              message: err instanceof Error ? err.message : String(err),
            });
          });
        }
        break;
      }

      case 'room.set_match_heroes': {
        const heroes = Array.isArray(msg.matchHeroes) ? msg.matchHeroes : [];
        updateRoomMatchHeroes(room, heroes).catch((err) => {
          broadcastJson(room, {
            type: 'engine.error',
            role: client.role,
            speaker: client.name,
            message: err instanceof Error ? err.message : String(err),
          });
        });
        break;
      }

      case 'room.set_tts_options': {
        updateRoomTtsOptions(room, {
          ttsZhVoice: typeof msg.ttsZhVoice === 'string' ? msg.ttsZhVoice : undefined,
          ttsEnVoice: typeof msg.ttsEnVoice === 'string' ? msg.ttsEnVoice : undefined,
          ttsStreamMode: isValidTtsStreamMode(msg.ttsStreamMode) ? msg.ttsStreamMode : undefined,
          voiceLiveAsrModel: isValidVoiceLiveAsrModel(msg.voiceLiveAsrModel)
            ? msg.voiceLiveAsrModel
            : undefined,
        }).catch((err) => {
          broadcastJson(room, {
            type: 'engine.error',
            role: client.role,
            speaker: client.name,
            message: err instanceof Error ? err.message : String(err),
          });
        });
        break;
      }

      case 'client.set_self_monitor': {
        client.selfMonitor = Boolean(msg.selfMonitor);
        broadcastRoomRoster(room);
        break;
      }
    }
  });

  ws.on('close', () => {
    room.clients.delete(client.id);
    if (client.engine) {
      client.engine.stop().catch(() => {});
      client.engine = null;
    }
    if (room.clients.size === 0) {
      for (const eng of room.demoEngines.values()) {
        eng.stop().catch(() => {});
      }
      room.demoEngines.clear();
    } else {
      broadcastRoomRoster(room);
    }
  });
});

server.listen(PORT, '0.0.0.0', () => {
  console.log(`DOTA2 Bilingual Voice Coach Room listening on http://localhost:${PORT}`);
});
