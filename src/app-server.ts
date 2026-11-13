import crypto from 'node:crypto';
import fs from 'node:fs/promises';
import http from 'node:http';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import WebSocket, { WebSocketServer } from 'ws';
import { AccessStore, AppError, secretsEqual, SESSION_MAX_AGE_SECONDS } from './access-store.ts';
import type { PublicUser, StoredRoom } from './access-store.ts';
import { generateDota2GsiConfigFile, parseDota2GsiPayload, recognizeDraftFromTopBarImage } from './auto-draft.ts';
import type { RoomGsiState } from './auto-draft.ts';
import { DOTA2_DICTIONARY } from './dota2-glossary.ts';
import { createTranslationEngine } from './engines/index.ts';
import type { EngineConnectionState, ParticipantRole, TranslationEngine, TurnMetrics, TtsStreamMode } from './engines/types.ts';
import { DEFAULT_ROOM_SETTINGS, TTS_VOICE_CATALOG, validateRoomSettings } from './room-settings.ts';
import type { RoomSettings } from './room-settings.ts';

const ROOT = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const COOKIE_NAME = 'dota2_session';

interface ClientConn {
  id: string;
  name: string;
  role: ParticipantRole;
  ws: WebSocket;
  sessionToken: string;
  invitation?: string;
  selfMonitor: boolean;
  engine: TranslationEngine | null;
  startingEngine: Promise<void> | null;
  engineState: EngineConnectionState;
  engineGeneration: number;
  startupRetries: number;
  retryTimer: NodeJS.Timeout | null;
  inputFrames: number;
  forwardedFrames: number;
  lastInputAckAt: number;
  lastInputLogAt: number;
  lastCaptureLogAt: number;
}

interface RoomState extends RoomSettings {
  roomId: string;
  record: StoredRoom;
  englishOriginalVoiceRelay: boolean;
  ttsStreamMode: TtsStreamMode;
  matchHeroes: string[];
  gsiState: RoomGsiState;
  lastVisionPhase: 'draft' | 'in_game' | 'menu_or_other';
  clients: Map<string, ClientConn>;
  metricsHistory: TurnMetrics[];
}

function sessionCookie(req: http.IncomingMessage): string | undefined {
  return req.headers.cookie?.split(';').map(part => part.trim())
    .find(part => part.startsWith(`${COOKIE_NAME}=`))?.slice(COOKIE_NAME.length + 1);
}

function cookie(value: string, maxAge = SESSION_MAX_AGE_SECONDS): string {
  return `${COOKIE_NAME}=${value}; Path=/; HttpOnly; SameSite=Lax; Max-Age=${maxAge}` +
    (process.env.COOKIE_SECURE === '1' ? '; Secure' : '');
}

function checkOrigin(req: http.IncomingMessage): void {
  if (req.headers['sec-fetch-site'] === 'cross-site') throw new AppError(403, '不允许跨站请求');
  if (req.headers.origin) {
    let origin: URL;
    try { origin = new URL(req.headers.origin); } catch { throw new AppError(403, '无效请求来源'); }
    if (!['http:', 'https:'].includes(origin.protocol) || origin.host !== req.headers.host) {
      throw new AppError(403, '不允许跨站请求');
    }
  }
}

async function readJson(req: http.IncomingMessage, limit = 16 * 1024): Promise<Record<string, any>> {
  if (!req.headers['content-type']?.toLowerCase().startsWith('application/json')) {
    throw new AppError(415, '需要 application/json 请求');
  }
  const chunks: Buffer[] = [];
  let bytes = 0;
  for await (const chunk of req) {
    bytes += chunk.length;
    if (bytes > limit) throw new AppError(413, '请求内容过大');
    chunks.push(chunk);
  }
  try {
    const body = JSON.parse(Buffer.concat(chunks).toString('utf8'));
    if (!body || typeof body !== 'object' || Array.isArray(body)) throw new Error();
    return body;
  } catch {
    throw new AppError(400, '无效 JSON');
  }
}

function reply(res: http.ServerResponse, status: number, body: unknown): void {
  res.writeHead(status, { 'Content-Type': 'application/json; charset=utf-8' });
  res.end(JSON.stringify(body));
}

export function roomView(room: StoredRoom, user: PublicUser) {
  const isOwner = room.ownerUsername === user.username;
  return {
    roomId: room.roomId, name: room.name, ownerUsername: room.ownerUsername,
    settings: room.settings, createdAt: room.createdAt, isOwner,
    ...(isOwner ? { allowedUsernames: room.allowedUsernames, inviteToken: room.inviteToken } : {}),
  };
}

export function createCoachServer(
  store: AccessStore,
  engineFactory: typeof createTranslationEngine = createTranslationEngine,
) {
  const rooms = new Map<string, RoomState>();
  const loginAttempts = new Map<string, { count: number; resetAt: number }>();

  function runtimeRoom(record: StoredRoom): RoomState {
    let room = rooms.get(record.roomId);
    if (!room) {
      room = {
        ...record.settings, record, roomId: record.roomId,
        englishOriginalVoiceRelay: true, ttsStreamMode: 'v2-text-flush',
        matchHeroes: [], clients: new Map(), metricsHistory: [],
        lastVisionPhase: 'menu_or_other',
        gsiState: { matchId: null, gameState: null, lastHeartbeatAt: null, lastSourceMode: 'idle' },
      };
      rooms.set(record.roomId, room);
    }
    return room;
  }

  function authenticate(req: http.IncomingMessage) {
    const auth = store.getSession(sessionCookie(req));
    if (!auth) throw new AppError(401, '登录已失效，请重新登录');
    return auth;
  }

  function authorized(room: RoomState, client: ClientConn): boolean {
    const auth = store.getSession(client.sessionToken);
    if (!auth) return false;
    try { store.requireRoom(auth.user, room.roomId, client.invitation); return true; } catch { return false; }
  }

  function disconnect(room: RoomState, client: ClientConn, code: number, reason: string): void {
    if (!room.clients.has(client.id)) return;
    voiceDiagnostic(room, client, 'disconnected', { code });
    room.clients.delete(client.id);
    if (client.retryTimer) clearTimeout(client.retryTimer);
    client.retryTimer = null;
    client.ws.close(code, reason);
    if (client.engine) {
      client.engine.stop().catch(() => {});
      client.engine = null;
    }
  }

  function safeSend(ws: WebSocket, payload: unknown): void {
    if (ws.readyState === WebSocket.OPEN) ws.send(JSON.stringify(payload));
  }

  function voiceDiagnostic(room: RoomState, client: ClientConn, event: string, extra = {}) {
    // Metadata only: never audio, transcripts, account passwords, cookies or invite/API keys.
    console.info(JSON.stringify({
      at: new Date().toISOString(), event: `voice.${event}`, roomId: room.roomId,
      connectionId: client.id, role: client.role, engine: room.engineKind,
      state: client.engineState, inputFrames: client.inputFrames,
      forwardedFrames: client.forwardedFrames, ...extra,
    }));
  }

  function voiceErrorCode(message: string): string {
    if (message.includes('model_not_deployed')) return 'model_not_deployed';
    if (/timed? out/i.test(message)) return 'timeout';
    const http = /HTTP (\d{3})/.exec(message);
    if (http) return `http_${http[1]}`;
    if (/closed before/.test(message)) return 'closed_before_ready';
    if (/输入转录失败/.test(message)) return 'transcription_failed';
    return 'provider_error';
  }

  function setEngineState(room: RoomState, client: ClientConn, state: EngineConnectionState) {
    if (!room.clients.has(client.id)) return;
    const changed = client.engineState !== state;
    client.engineState = state;
    safeSend(client.ws, { type: 'engine.state', state });
    if (changed || state === 'connecting') voiceDiagnostic(room, client, 'engine_state');
  }

  function warmClientEngine(room: RoomState, client: ClientConn) {
    ensureClientEngine(room, client).catch(error => {
      if (!room.clients.has(client.id) || !authorized(room, client)) return;
      const message = error instanceof Error ? error.message : String(error);
      if (client.engineState !== 'failed') setEngineState(room, client, 'failed');
      voiceDiagnostic(room, client, 'startup_failed', { code: voiceErrorCode(message) });
      safeSend(client.ws, { type: 'engine.error', speaker: client.name, message });
      if (client.startupRetries >= 2 || client.retryTimer) return;
      const delay = 1000 * ++client.startupRetries;
      setEngineState(room, client, 'reconnecting');
      client.retryTimer = setTimeout(() => {
        client.retryTimer = null;
        if (room.clients.has(client.id) && authorized(room, client)) warmClientEngine(room, client);
      }, delay);
    });
  }

  function broadcastJson(room: RoomState, payload: unknown): void {
    const msg = JSON.stringify(payload);
    for (const client of room.clients.values()) {
      if (client.ws.readyState === WebSocket.OPEN && authorized(room, client)) {
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
    sourceClientId: string,
    pcm24kMono: Buffer,
  ): void {
    const header = Buffer.from([sourceRole === 'coach' ? 0x01 : 0x02, 0x00]);
    const packet = Buffer.concat([header, pcm24kMono]);
    const targetRole: ParticipantRole = sourceRole === 'coach' ? 'student' : 'coach';

    for (const client of room.clients.values()) {
      if (client.ws.readyState !== WebSocket.OPEN || !authorized(room, client)) continue;
      const isIntendedListener = client.role === targetRole;
      const isSelfMonitor = client.id === sourceClientId && client.selfMonitor;
      if (isIntendedListener || isSelfMonitor) {
        client.ws.send(packet, { binary: true });
      }
    }
  }

  function buildEngineCallbacks(
    room: RoomState,
    role: ParticipantRole,
    speakerName: string,
    sourceClientId: string,
    generation: number,
  ) {
    return {
      onConnectionState: (state: EngineConnectionState) => {
        const client = room.clients.get(sourceClientId);
        if (!client || client.engineGeneration !== generation) return;
        // Initial readiness is published only after ensureClientEngine assigns the engine.
        if (state === 'ready' && !client.engine) return;
        setEngineState(room, client, state);
      },
      onSourcePartial: (text: string) => {
        broadcastJson(room, {
          type: 'subtitle.source.partial',
          role,
          speaker: speakerName,
          text,
        });
      },
      onSourceFinal: (turnId: string, text: string) => {
        const client = room.clients.get(sourceClientId);
        if (client) voiceDiagnostic(room, client, 'source_final');
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
        const client = room.clients.get(sourceClientId);
        if (client) voiceDiagnostic(room, client, 'engine_error', { code: voiceErrorCode(message) });
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
    if (!authorized(room, client) || !room.clients.has(client.id)) throw new AppError(403, '已离开房间');
    if (client.engine) {
      if (client.engineState === 'ready') return client.engine;
      throw new AppError(503, '语音服务尚未恢复，请点击重新连接');
    }
    if (client.startingEngine) {
      await client.startingEngine;
      if (client.engine) return client.engine;
      throw new AppError(503, '引擎尚未就绪');
    }
    setEngineState(room, client, 'connecting');
    const generation = ++client.engineGeneration;
    const next = engineFactory(room.engineKind, {
      role: client.role, englishOriginalVoiceRelay: room.englishOriginalVoiceRelay,
      matchHeroes: room.matchHeroes, ttsZhVoice: room.ttsZhVoice,
      ttsEnVoice: room.ttsEnVoice, ttsStreamMode: room.ttsStreamMode,
      voiceLiveAsrModel: room.voiceLiveAsrModel,
    }, buildEngineCallbacks(room, client.role, client.name, client.id, generation));
    client.startingEngine = (async () => {
      try {
        await next.start();
        if (!authorized(room, client) || !room.clients.has(client.id) || client.ws.readyState !== WebSocket.OPEN) {
          throw new AppError(403, '已离开房间');
        }
        client.engine = next;
        if (client.retryTimer) clearTimeout(client.retryTimer);
        client.retryTimer = null;
        client.startupRetries = 0;
        setEngineState(room, client, 'ready');
      } catch (error) {
        await next.stop().catch(() => {});
        setEngineState(room, client, 'failed');
        throw error;
      } finally { client.startingEngine = null; }
    })();
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
      name: room.record.name,
      ownerUsername: room.record.ownerUsername,
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
    room.matchHeroes = heroes.slice(0, 10).map((h) => String(h).trim()).filter(Boolean);
    broadcastRoomRoster(room);

    await Promise.all([
      ...Array.from(room.clients.values()).map(async (client) => {
        if (client.engine?.updateMatchHeroes) {
          await client.engine.updateMatchHeroes(room.matchHeroes);
        }
      }),
    ]);
  }


  const server = http.createServer(async (req, res) => {
    res.setHeader('Cache-Control', 'no-store');
    res.setHeader('X-Content-Type-Options', 'nosniff');
    res.setHeader('Referrer-Policy', 'no-referrer');
    res.setHeader('X-Frame-Options', 'DENY');
    try {
      const reqUrl = new URL(req.url || '/', `http://${req.headers.host || 'localhost'}`);
      const isRead = req.method === 'GET' || req.method === 'HEAD';
      const route = reqUrl.pathname;

      if (route === '/api/auth/login' && req.method === 'POST') {
        checkOrigin(req);
        const body = await readJson(req);
        if (typeof body.username !== 'string' || typeof body.password !== 'string' ||
            body.username.length > 32 || body.password.length > 256) throw new AppError(400, '用户名或密码格式无效');
        const ip = req.socket.remoteAddress || 'unknown';
        const now = Date.now();
        for (const [key, value] of loginAttempts) if (value.resetAt <= now) loginAttempts.delete(key);
        const attempt = loginAttempts.get(ip) || { count: 0, resetAt: now + 15 * 60_000 };
        if (attempt.count >= 20 || loginAttempts.size >= 10000) {
          res.setHeader('Retry-After', '900');
          throw new AppError(429, '登录尝试过多，请 15 分钟后再试');
        }
        attempt.count++;
        loginAttempts.set(ip, attempt);
        const user = await store.verifyPassword(body.username.trim(), body.password);
        if (!user) throw new AppError(401, '用户名或密码错误');
        const previous = store.getSession(sessionCookie(req));
        if (previous) {
          await store.deleteSession(previous.session.idHash);
          sweepConnections();
        }
        const { sessionToken, session } = await store.createSession(user.username);
        res.setHeader('Set-Cookie', cookie(sessionToken));
        reply(res, 200, { user, csrfToken: session.csrfToken });
        return;
      }

      if (route === '/api/auth/session' && isRead) {
        const auth = store.getSession(sessionCookie(req));
        reply(res, 200, auth ? { user: auth.user, csrfToken: auth.session.csrfToken } : { user: null });
        return;
      }

      // DOTA2 itself has no browser session. Its room-specific secret is independent of invitations.
      if (route === '/api/gsi' && req.method === 'POST') {
        const parsed = await readJson(req, 512 * 1024);
        const roomId = String(reqUrl.searchParams.get('room') || parsed.auth?.room || '');
        const record = store.getRoom(roomId);
        if (!record || !secretsEqual(parsed.auth?.token, record.gsiToken)) throw new AppError(403, '无效 GSI 凭证');
        const room = runtimeRoom(record);
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



      if (route.startsWith('/api/')) {
        const auth = authenticate(req);
        if (!isRead) {
          checkOrigin(req);
          if (!secretsEqual(req.headers['x-csrf-token'], auth.session.csrfToken)) {
            throw new AppError(403, '请求验证失败，请刷新页面');
          }
        }

        if (route === '/api/auth/logout' && req.method === 'POST') {
          await store.deleteSession(auth.session.idHash);
          res.setHeader('Set-Cookie', cookie('', 0));
          sweepConnections();
          reply(res, 200, { ok: true });
          return;
        }
        if (route === '/api/config' && isRead) {
          reply(res, 200, { ttsVoiceCatalog: TTS_VOICE_CATALOG, defaults: DEFAULT_ROOM_SETTINGS });
          return;
        }
        if (route === '/api/users' && isRead) {
          if (!auth.user.canCreateRooms) throw new AppError(403, '没有访问用户列表的权限');
          reply(res, 200, { users: store.listUsers() });
          return;
        }
        if (route === '/api/rooms' && isRead) {
          reply(res, 200, { rooms: store.listOwnedRooms(auth.user.username).map(room => roomView(room, auth.user)) });
          return;
        }
        if (route === '/api/rooms' && req.method === 'POST') {
          if (!auth.user.canCreateRooms) throw new AppError(403, '你的账号没有创建房间的权限');
          const body = await readJson(req);
          if (typeof body.name !== 'string') throw new AppError(400, '请填写房间名称');
          const room = await store.createRoom(auth.user, body.name, validateRoomSettings(body.settings), body.allowedUsernames ?? []);
          reply(res, 201, { room: roomView(room, auth.user) });
          return;
        }
        const roomRoute = /^\/api\/rooms\/([a-f0-9-]+)(\/access)?$/.exec(route);
        if (roomRoute) {
          if (roomRoute[2] === '/access' && req.method === 'PATCH') {
            store.requireOwner(auth.user, roomRoute[1]);
            const body = await readJson(req);
            const room = await store.updateAccess(auth.user, roomRoute[1], body.allowedUsernames);
            sweepConnections();
            reply(res, 200, { room: roomView(room, auth.user) });
            return;
          }
          if (!roomRoute[2] && isRead) {
            const room = store.requireRoom(auth.user, roomRoute[1], req.headers['x-room-invite'] as string | undefined);
            reply(res, 200, { room: roomView(room, auth.user) });
            return;
          }
          throw new AppError(405, '房间同传设置创建后不可修改');
        }
        if (route === '/api/dictionary' && isRead) {
          reply(res, 200, DOTA2_DICTIONARY);
          return;
        }
        if (route === '/api/dictionary.md' && isRead) {
          res.writeHead(200, { 'Content-Type': 'text/markdown; charset=utf-8' });
          res.end(await fs.readFile(path.join(ROOT, 'docs/dota2-bilingual-dictionary.md')));
          return;
        }
        if (route === '/api/gsi-config' && isRead) {
          const roomId = reqUrl.searchParams.get('room') || '';
          const room = store.requireOwner(auth.user, roomId);
          const origin = process.env.PUBLIC_ORIGIN || `http://${req.headers.host}`;
          const cfg = generateDota2GsiConfigFile(origin, roomId, room.gsiToken);
          res.writeHead(200, {
            'Content-Type': 'text/plain; charset=utf-8',
            'Content-Disposition': 'attachment; filename="gamestate_integration_dota2_coach.cfg"',
          });
          res.end(cfg);
          return;
        }
        if (route === '/api/vision-draft' && req.method === 'POST') {
          const parsed = await readJson(req, 8 * 1024 * 1024);
          const record = store.requireOwner(auth.user, String(parsed.roomId || ''));
          const imageDataUrl = String(parsed.imageDataUrl || '');
          if (!imageDataUrl.startsWith('data:image/')) throw new AppError(400, '缺少图片');
          const room = runtimeRoom(record);
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



        throw new AppError(404, '接口不存在');
      }

      const staticFiles: Record<string, [string, string]> = {
        '/': ['index.html', 'text/html'],
        '/index.html': ['index.html', 'text/html'],
        '/app.js': ['app.js', 'application/javascript'],
        '/styles.css': ['styles.css', 'text/css'],
      };
      if (!isRead || !staticFiles[route]) throw new AppError(404, 'Not Found');
      const [filename, mime] = staticFiles[route];
      const content = await fs.readFile(path.join(ROOT, 'public', filename));
      res.writeHead(200, { 'Content-Type': `${mime}; charset=utf-8` });
      res.end(req.method === 'HEAD' ? undefined : content);
    } catch (error) {
      if (res.headersSent || res.destroyed) return;
      reply(res, error instanceof AppError ? error.status : 500,
        { error: error instanceof AppError ? error.message : '服务器内部错误' });
    }
  });

  const wss = new WebSocketServer({ noServer: true, maxPayload: 256 * 1024 });
  server.on('upgrade', (req, socket, head) => {
    try {
      checkOrigin(req);
      const url = new URL(req.url || '/', `http://${req.headers.host || 'localhost'}`);
      if (url.pathname !== '/ws') throw new AppError(404, 'Not Found');
      const auth = authenticate(req);
      const invitation = url.searchParams.get('invite') || undefined;
      const record = store.requireRoom(auth.user, url.searchParams.get('room') || '', invitation);
      const role = url.searchParams.get('role');
      if (role !== 'coach' && role !== 'student') throw new AppError(400, '请选择身份');
      wss.handleUpgrade(req, socket, head, ws => {
        const room = runtimeRoom(record);
        const client: ClientConn = {
          id: crypto.randomUUID(), name: auth.user.username, role, ws,
          sessionToken: sessionCookie(req)!, invitation,
          selfMonitor: url.searchParams.get('selfMonitor') === '1',
          engine: null, startingEngine: null,
          engineState: 'connecting', engineGeneration: 0, startupRetries: 0, retryTimer: null,
          inputFrames: 0, forwardedFrames: 0, lastInputAckAt: 0, lastInputLogAt: 0,
          lastCaptureLogAt: 0,
        };
        room.clients.set(client.id, client);
        broadcastRoomRoster(room);
        warmClientEngine(room, client);
        ws.on('error', () => disconnect(room, client, 1011, 'Connection error'));
        ws.on('close', () => {
          disconnect(room, client, 1000, 'Disconnected');
          broadcastRoomRoster(room);
        });
        ws.on('message', (data, binary) => {
          if (!authorized(room, client)) { disconnect(room, client, 4003, 'Access denied'); return; }
          if (binary) {
            const pcm = Buffer.isBuffer(data) ? data : Buffer.from(data as ArrayBuffer);
            if (!pcm.length || pcm.length % 2 !== 0) return;
            client.inputFrames++;
            if (client.engine && client.engineState === 'ready') {
              client.engine.appendAudio(pcm);
              client.forwardedFrames++;
            }
            const now = Date.now();
            if (now - client.lastInputAckAt >= 1000) {
              client.lastInputAckAt = now;
              safeSend(ws, {
                type: 'audio.input.status', receivedFrames: client.inputFrames,
                forwardedFrames: client.forwardedFrames, engineReady: client.engineState === 'ready',
              });
            }
            if (now - client.lastInputLogAt >= 30_000) {
              client.lastInputLogAt = now;
              voiceDiagnostic(room, client, 'input');
            }
            return;
          }
          let message: any;
          try { message = JSON.parse(data.toString()); } catch { return; }
          if (!message || typeof message !== 'object') return;
          const reject = (text: string) => safeSend(ws, { type: 'room.error', message: text });
          switch (message.type) {
            case 'client.audio.status': {
              const allowed = ['capturing', 'stopped', 'capture_stalled', 'input_silent', 'upload_stalled', 'capture_error', 'context_suspended'];
              if (allowed.includes(message.status) && Date.now() - client.lastCaptureLogAt >= 250) {
                client.lastCaptureLogAt = Date.now();
                voiceDiagnostic(room, client, 'capture', { capture: message.status });
              }
              break;
            }
            case 'speech.end': client.engine?.markSpeechEnd(); break;
            case 'text.send': {
              const text = typeof message.text === 'string' ? message.text.trim() : '';
              if (!text || text.length > 4000) { reject('文字须为 1–4000 个字符'); break; }
              ensureClientEngine(room, client).then(engine => {
                if (authorized(room, client) && room.clients.has(client.id)) return engine.translateText?.(text);
              }).catch(error => reject(error.message));
              break;
            }
            case 'room.set_engine':
            case 'room.set_tts_options':
              reject('同传设置在创建房间时确定，创建后不可修改');
              break;
            case 'room.set_match_heroes': {
              if (client.name !== room.record.ownerUsername) { reject('只有房主可以修改阵容'); break; }
              const heroes = message.matchHeroes;
              if (!Array.isArray(heroes) || heroes.length > 10 ||
                  heroes.some(hero => typeof hero !== 'string' || hero.length > 128)) {
                reject('无效英雄列表'); break;
              }
              updateRoomMatchHeroes(room, heroes).catch(error => reject(error.message));
              break;
            }
            case 'client.set_self_monitor':
              client.selfMonitor = Boolean(message.selfMonitor);
              broadcastRoomRoster(room);
              break;
          }
        });
      });
    } catch (error) {
      const status = error instanceof AppError ? error.status : 500;
      socket.end(`HTTP/1.1 ${status} ${http.STATUS_CODES[status]}\r\nConnection: close\r\nContent-Length: 0\r\n\r\n`);
    }
  });

  function sweepConnections() {
    for (const room of rooms.values()) {
      for (const client of room.clients.values()) {
        if (!authorized(room, client)) {
          const code = store.getSession(client.sessionToken) ? 4003 : 4001;
          disconnect(room, client, code, code === 4001 ? 'Session expired' : 'Access revoked');
        }
      }
      broadcastRoomRoster(room);
    }
  }
  const timer = setInterval(sweepConnections, 30_000);
  timer.unref();
  server.on('close', () => {
    clearInterval(timer);
    for (const room of rooms.values()) for (const client of room.clients.values()) {
      disconnect(room, client, 1001, 'Server shutting down');
    }
    wss.close();
  });
  return server;
}
