import crypto from 'node:crypto';
import WebSocket from 'ws';
import { DEFAULT_FOUNDRY_CONFIG, tokenManager } from './azure-auth.ts';
import type { FoundryConfig } from './azure-auth.ts';

export interface TtsSynthesizeOptions {
  text: string;
  voice: string;
  lang: 'zh-CN' | 'en-US';
  rate?: string; // e.g. "+8%" for snappy live callouts
  onAudioChunk: (pcm24kMono: Buffer) => void;
}

export interface TtsTimingResult {
  firstChunkMs: number;
  totalMs: number;
  totalBytes: number;
}

function escapeXml(raw: string): string {
  return raw
    .replace(/&/g, '&amp;')
    .replace(/</g, '&lt;')
    .replace(/>/g, '&gt;')
    .replace(/"/g, '&quot;')
    .replace(/'/g, '&apos;');
}

interface PendingSynthesis {
  t0: number;
  firstTokenSent: boolean;
  firstChunkMs: number | null;
  flushCount: number;
  totalBytes: number;
  onAudioChunk: (pcm24kMono: Buffer) => void;
  resolve: (res: TtsTimingResult) => void;
  reject: (err: Error) => void;
}

export type TtsV2FlushStrategy = 'first-clause-flush' | 'first-comma-to-period' | 'none';

export interface TtsOpenTextStreamOptions extends Omit<TtsSynthesizeOptions, 'text'> {
  flushStrategy?: TtsV2FlushStrategy;
  /**
   * Non-terminal idle flush timeout in ms (modeled after `_TEXT_IDLE_FLUSH_TIMEOUT_MS = 200`
   * in `voice-agent-orchestrator/components/speech_output/text_flush.py`). Set 0 to disable.
   */
  idleFlushMs?: number;
}

export interface TtsActiveTextStream {
  pushText: (delta: string) => void;
  flushText: () => void;
  endText: () => Promise<TtsTimingResult>;
  abort: () => void;
}

const AUDIO_HEADER_MARKER = Buffer.from('Path:audio\r\n');

function formatWsMessage(path: string, requestId: string, contentType: string, body: string): string {
  return (
    `Path:${path}\r\n` +
    `X-RequestId:${requestId}\r\n` +
    `X-Timestamp:${new Date().toISOString()}\r\n` +
    `Content-Type:${contentType}\r\n\r\n` +
    body
  );
}

function stripMarkdownArtifacts(chunk: string): string {
  return chunk.replace(/[*_`~#]/g, '');
}

/**
 * Modeled after `ReplaceFirstCommaFilter` in `voice-agent-orchestrator/.../websocket_tts.py`:
 * Azure TTS `bidirectionalStreamingMode` segments internally on sentence-ending periods (`.`, `。`, `!`, `?`),
 * NOT commas (`,`, `，`). Replacing ONLY the first speakable clause's comma with a period forces immediate
 * first-sentence synthesis (boosting TTFA by ~600-780ms) while keeping all subsequent commas natural.
 */
export class ReplaceFirstCommaFilter {
  private needReplaceFirstComma = true;
  private seenSentenceEnd = false;
  private bufferedSpeakableChars = 0;
  private readonly minClauseChars: number;

  constructor(lang: 'zh-CN' | 'en-US') {
    this.minClauseChars = lang === 'zh-CN' ? 4 : 10;
  }

  apply(chunk: string): string {
    if (!this.needReplaceFirstComma || !chunk) {
      return chunk;
    }
    if (/[.!?。！？]/.test(chunk)) {
      this.seenSentenceEnd = true;
      this.needReplaceFirstComma = false;
      return chunk;
    }
    const candidateLen = this.bufferedSpeakableChars + chunk.trim().length;
    if (!this.seenSentenceEnd && candidateLen >= this.minClauseChars && /[,，;；]/.test(chunk)) {
      let out = chunk;
      if (out.includes('，')) {
        out = out.replace('，', '。');
      } else if (out.includes('；')) {
        out = out.replace('；', '。');
      } else if (out.includes(', ')) {
        out = out.replace(', ', '. ');
      } else if (out.includes(',')) {
        out = out.replace(',', '.');
      } else if (out.includes(';')) {
        out = out.replace(';', '.');
      }
      this.needReplaceFirstComma = false;
      return out;
    }
    this.bufferedSpeakableChars += chunk.trim().length;
    return chunk;
  }
}

/**
 * Pure WebSocket Azure Speech TTS client (`ws` only, ZERO Speech SDK).
 * Supports both `websocket/v1` (`Path:ssml`) and `voice-agent-orchestrator`-style
 * `websocket/v2` (`bidirectionalStreamingMode: true`, `Path:text.piece`, non-terminal `Path:text.flush`,
 * `ReplaceFirstCommaFilter`, and `Path:text.end`).
 */
export class AzureTtsPoolClient {
  private ws: WebSocket | null = null;
  private connecting: Promise<WebSocket> | null = null;
  private connectedAt = 0;
  private wsV2: WebSocket | null = null;
  private connectingV2: Promise<WebSocket> | null = null;
  private connectedAtV2 = 0;
  private pending = new Map<string, PendingSynthesis>();
  private config: FoundryConfig;

  constructor(config: FoundryConfig = DEFAULT_FOUNDRY_CONFIG) {
    this.config = config;
  }

  async warmup(includeV2 = true): Promise<void> {
    if (includeV2) {
      await Promise.all([this.ensureSocket(), this.ensureSocketV2()]);
    } else {
      await this.ensureSocket();
    }
  }

  private async ensureSocket(): Promise<WebSocket> {
    const now = Date.now();
    // Refresh socket before Entra token window expires (~45 min)
    if (this.ws && this.ws.readyState === WebSocket.OPEN && now - this.connectedAt < 40 * 60_000) {
      return this.ws;
    }
    if (this.connecting) {
      return this.connecting;
    }
    this.connecting = (async () => {
      try {
        const headers = await tokenManager.getSpeechAuthHeaders(this.config);
        const connId = crypto.randomUUID().replace(/-/g, '');
        const url = `wss://${this.config.ttsHost}/cognitiveservices/websocket/v1?X-ConnectionId=${connId}`;
        const ws = await new Promise<WebSocket>((resolve, reject) => {
          const sock = new WebSocket(url, {
            headers: {
              ...headers,
              'User-Agent': 'dota2-coach-bridge',
            },
            handshakeTimeout: 8000,
          });
          sock.once('open', () => resolve(sock));
          sock.once('error', (err) => reject(err));
          sock.once('unexpected-response', (_req, res) => {
            let body = '';
            res.on('data', (c) => (body += c));
            res.on('end', () => reject(new Error(`TTS WS HTTP ${res.statusCode}: ${body}`)));
          });
        });

        this.attachListeners(ws);
        this.ws = ws;
        this.connectedAt = Date.now();
        return ws;
      } finally {
        this.connecting = null;
      }
    })();
    return this.connecting;
  }

  private attachListeners(ws: WebSocket): void {
    ws.on('message', (data: WebSocket.RawData, isBinary: boolean) => {
      const buf = Buffer.isBuffer(data) ? data : Buffer.from(data as ArrayBuffer);
      if (isBinary) {
        const headerEnd = buf.indexOf(AUDIO_HEADER_MARKER);
        if (headerEnd === -1) return;
        const headerText = buf.subarray(0, headerEnd).toString('utf8');
        const reqMatch = /X-RequestId:([a-fA-F0-9]+)/i.exec(headerText);
        if (!reqMatch) return;
        const entry = this.pending.get(reqMatch[1].toLowerCase());
        if (!entry) return;
        const pcm = buf.subarray(headerEnd + AUDIO_HEADER_MARKER.length);
        if (pcm.length > 0) {
          if (entry.firstChunkMs === null) {
            entry.firstChunkMs = Math.max(1, Math.round(performance.now() - entry.t0));
          }
          entry.totalBytes += pcm.length;
          entry.onAudioChunk(pcm);
        }
      } else {
        const text = buf.toString('utf8');
        if (text.includes('Path:turn.end')) {
          const reqMatch = /X-RequestId:([a-fA-F0-9]+)/i.exec(text);
          if (!reqMatch) return;
          const reqId = reqMatch[1].toLowerCase();
          const entry = this.pending.get(reqId);
          if (entry) {
            this.pending.delete(reqId);
            const totalMs = Math.max(1, Math.round(performance.now() - entry.t0));
            entry.resolve({
              firstChunkMs: entry.firstChunkMs ?? totalMs,
              totalMs,
              totalBytes: entry.totalBytes,
            });
          }
        }
      }
    });

    const failAll = (reason: Error) => {
      if (this.ws === ws) {
        this.ws = null;
      }
      if (this.wsV2 === ws) {
        this.wsV2 = null;
      }
      for (const [id, entry] of this.pending.entries()) {
        this.pending.delete(id);
        entry.reject(reason);
      }
    };

    ws.on('close', () => failAll(new Error('TTS WebSocket closed')));
    ws.on('error', (err) => failAll(err instanceof Error ? err : new Error(String(err))));
  }

  async synthesize(opts: TtsSynthesizeOptions): Promise<TtsTimingResult> {
    const trimmed = opts.text.trim();
    if (!trimmed) {
      return { firstChunkMs: 0, totalMs: 0, totalBytes: 0 };
    }

    try {
      return await this.synthesizeOverWs(opts);
    } catch {
      return await this.synthesizeOverHttp(opts);
    }
  }

  private async synthesizeOverWs(opts: TtsSynthesizeOptions): Promise<TtsTimingResult> {
    const ws = await this.ensureSocket();
    const reqId = crypto.randomUUID().replace(/-/g, '').toLowerCase();
    const rate = opts.rate ?? '+6%';

    const configFrame = formatWsMessage(
      'speech.config',
      reqId,
      'application/json',
      JSON.stringify({
        context: {
          synthesis: {
            audio: {
              metadataOptions: { sentenceBoundaryEnabled: false, wordBoundaryEnabled: false },
              outputFormat: 'raw-24khz-16bit-mono-pcm',
            },
          },
        },
      }),
    );

    const ssmlBody =
      `<speak version='1.0' xmlns='http://www.w3.org/2001/10/synthesis' xml:lang='${opts.lang}'>` +
      `<voice name='${opts.voice}'><prosody rate='${rate}'>${escapeXml(opts.text)}</prosody></voice>` +
      `</speak>`;

    const ssmlFrame = formatWsMessage('ssml', reqId, 'application/ssml+xml', ssmlBody);

    return new Promise<TtsTimingResult>((resolve, reject) => {
      const t0 = performance.now();
      this.pending.set(reqId, {
        t0,
        firstTokenSent: true,
        firstChunkMs: null,
        flushCount: 0,
        totalBytes: 0,
        onAudioChunk: opts.onAudioChunk,
        resolve,
        reject,
      });
      ws.send(configFrame);
      ws.send(ssmlFrame);
    });
  }

  private async synthesizeOverHttp(opts: TtsSynthesizeOptions): Promise<TtsTimingResult> {
    const t0 = performance.now();
    const headers = await tokenManager.getSpeechAuthHeaders(this.config);
    const rate = opts.rate ?? '+6%';
    const ssml =
      `<speak version='1.0' xmlns='http://www.w3.org/2001/10/synthesis' xml:lang='${opts.lang}'>` +
      `<voice name='${opts.voice}'><prosody rate='${rate}'>${escapeXml(opts.text)}</prosody></voice>` +
      `</speak>`;

    const res = await fetch(`https://${this.config.ttsHost}/cognitiveservices/v1`, {
      method: 'POST',
      headers: {
        ...headers,
        'Content-Type': 'application/ssml+xml',
        'X-Microsoft-OutputFormat': 'raw-24khz-16bit-mono-pcm',
        'User-Agent': 'dota2-coach-bridge',
      },
      body: ssml,
    });
    if (!res.ok || !res.body) {
      throw new Error(`TTS HTTP ${res.status}: ${await res.text()}`);
    }

    const reader = res.body.getReader();
    let firstChunkMs: number | null = null;
    let totalBytes = 0;
    while (true) {
      const { done, value } = await reader.read();
      if (done) break;
      if (value && value.byteLength > 0) {
        if (firstChunkMs === null) {
          firstChunkMs = Math.round(performance.now() - t0);
        }
        totalBytes += value.byteLength;
        opts.onAudioChunk(Buffer.from(value));
      }
    }
    const totalMs = Math.round(performance.now() - t0);
    return {
      firstChunkMs: firstChunkMs ?? totalMs,
      totalMs,
      totalBytes,
    };
  }

  private async ensureSocketV2(): Promise<WebSocket> {
    const now = Date.now();
    if (this.wsV2 && this.wsV2.readyState === WebSocket.OPEN && now - this.connectedAtV2 < 40 * 60_000) {
      return this.wsV2;
    }
    if (this.connectingV2) {
      return this.connectingV2;
    }
    this.connectingV2 = (async () => {
      try {
        const headers = await tokenManager.getSpeechAuthHeaders(this.config);
        const connId = crypto.randomUUID().replace(/-/g, '').toLowerCase();
        // Matches voice-agent-orchestrator's high-priority voice-live v2 WebSocket query flags
        const url =
          `wss://${this.config.ttsHost}/cognitiveservices/websocket/v2` +
          `?X-ConnectionId=${connId}&trafficType=voice-live&priority=high&feature.tts_boost_first_byte_latency=true`;
        const ws = await new Promise<WebSocket>((resolve, reject) => {
          const sock = new WebSocket(url, {
            headers: { ...headers, 'User-Agent': 'dota2-coach-bridge-v2' },
            handshakeTimeout: 8000,
          });
          sock.once('open', () => resolve(sock));
          sock.once('error', reject);
          sock.once('unexpected-response', (_req, res) => {
            let body = '';
            res.on('data', (c) => (body += c));
            res.on('end', () => reject(new Error(`TTS v2 WS HTTP ${res.statusCode}: ${body}`)));
          });
        });

        this.attachListeners(ws);
        this.wsV2 = ws;
        this.connectedAtV2 = Date.now();
        return ws;
      } finally {
        this.connectingV2 = null;
      }
    })();
    return this.connectingV2;
  }

  /**
   * Pure WebSocket v2 Text-Streaming TTS modeled directly on `voice-agent-orchestrator`:
   * - `Path:speech.config` + `Path:synthesis.context` (`bidirectionalStreamingMode: true`)
   * - `Path:text.piece` for streaming LLM token deltas
   * - `Path:text.flush` for non-terminal immediate synthesis of buffered text (without closing the turn)
   * - `ReplaceFirstCommaFilter` option to rewrite the first speakable comma into a period
   * - 200ms token-idle auto-flush (`text_flush.py` `_TEXT_IDLE_FLUSH_TIMEOUT_MS`)
   * - `Path:text.end` to close the turn stream
   */
  async openTextStream(opts: TtsOpenTextStreamOptions): Promise<TtsActiveTextStream> {
    const ws = await this.ensureSocketV2();
    const reqId = crypto.randomUUID().replace(/-/g, '').toLowerCase();
    const flushStrategy: TtsV2FlushStrategy = opts.flushStrategy ?? 'first-clause-flush';
    const idleFlushMs = opts.idleFlushMs ?? (flushStrategy === 'none' ? 0 : 200);
    const minClauseChars = opts.lang === 'zh-CN' ? 4 : 10;
    const commaFilter =
      flushStrategy === 'first-comma-to-period' ? new ReplaceFirstCommaFilter(opts.lang) : null;

    const entry: PendingSynthesis = {
      t0: performance.now(),
      firstTokenSent: false,
      firstChunkMs: null,
      flushCount: 0,
      totalBytes: 0,
      onAudioChunk: opts.onAudioChunk,
      resolve: () => {},
      reject: () => {},
    };

    const donePromise = new Promise<TtsTimingResult>((resolve, reject) => {
      entry.resolve = resolve;
      entry.reject = reject;
      this.pending.set(reqId, entry);
    });

    // Per-turn speech.config + synthesis.context matching voice-agent-orchestrator/tts_ws_protocol.json
    ws.send(
      formatWsMessage(
        'speech.config',
        reqId,
        'application/json',
        JSON.stringify({
          context: {
            system: {
              name: 'VoiceLive',
              version: '0.0.1',
              build: 'Node',
              lang: 'TypeScript',
            },
          },
        }),
      ),
    );

    ws.send(
      formatWsMessage(
        'synthesis.context',
        reqId,
        'application/json',
        JSON.stringify({
          synthesis: {
            audio: {
              metadataOptions: {
                bookmarkEnabled: false,
                punctuationBoundaryEnabled: false,
                sentenceBoundaryEnabled: false,
                sessionEndEnabled: false,
                visemeEnabled: false,
                wordBoundaryEnabled: false,
              },
              outputFormat: 'raw-24khz-16bit-mono-pcm',
            },
            language: { autoDetection: false },
            input: {
              bidirectionalStreamingMode: true,
              voiceName: opts.voice,
              language: opts.lang,
              rate: opts.rate ?? '+6%',
            },
          },
        }),
      ),
    );

    let closed = false;
    let flushedFirstClause = false;
    let seenSentenceEnd = false;
    let hasUnflushedText = false;
    let speakableChars = 0;
    let idleTimer: ReturnType<typeof setTimeout> | null = null;

    const clearIdleTimer = () => {
      if (idleTimer !== null) {
        clearTimeout(idleTimer);
        idleTimer = null;
      }
    };

    const flushText = () => {
      if (closed || !hasUnflushedText) return;
      clearIdleTimer();
      hasUnflushedText = false;
      if (ws.readyState === WebSocket.OPEN) {
        entry.flushCount += 1;
        ws.send(formatWsMessage('text.flush', reqId, 'text/plain', ''));
      }
    };

    const scheduleIdleFlush = () => {
      if (idleFlushMs <= 0 || closed) return;
      clearIdleTimer();
      idleTimer = setTimeout(() => {
        idleTimer = null;
        if (!closed && hasUnflushedText && speakableChars >= minClauseChars) {
          flushText();
        }
      }, idleFlushMs);
    };

    return {
      pushText: (rawDelta: string) => {
        if (closed || !rawDelta) return;
        let delta = stripMarkdownArtifacts(rawDelta);
        if (!delta) return;

        if (!entry.firstTokenSent) {
          entry.firstTokenSent = true;
          entry.t0 = performance.now();
        }

        const rawHasSentenceEnd = /[.!?。！？]/.test(delta);
        const rawHasClauseBreak = /[,，;；:：]/.test(delta);

        if (commaFilter) {
          delta = commaFilter.apply(delta);
        }

        speakableChars += delta.trim().length;
        hasUnflushedText = true;

        if (ws.readyState === WebSocket.OPEN) {
          ws.send(formatWsMessage('text.piece', reqId, 'text/plain', delta));
        }

        if (rawHasSentenceEnd || /[.!?。！？]/.test(delta)) {
          seenSentenceEnd = true;
          hasUnflushedText = false;
          clearIdleTimer();
          return;
        }

        if (
          flushStrategy === 'first-clause-flush' &&
          !flushedFirstClause &&
          !seenSentenceEnd &&
          rawHasClauseBreak &&
          speakableChars >= minClauseChars
        ) {
          flushedFirstClause = true;
          flushText();
          return;
        }

        scheduleIdleFlush();
      },
      flushText,
      endText: () => {
        if (!closed) {
          closed = true;
          clearIdleTimer();
          if (ws.readyState === WebSocket.OPEN) {
            ws.send(formatWsMessage('text.end', reqId, 'text/plain', ''));
          }
        }
        return donePromise;
      },
      abort: () => {
        if (closed) return;
        closed = true;
        clearIdleTimer();
        this.pending.delete(reqId);
        donePromise.catch(() => {});
        if (ws.readyState === WebSocket.OPEN) {
          ws.send(formatWsMessage('text.end', reqId, 'text/plain', ''));
        }
      },
    };
  }

  close(): void {
    if (this.ws) {
      this.ws.close();
      this.ws = null;
    }
    if (this.wsV2) {
      this.wsV2.close();
      this.wsV2 = null;
    }
  }
}
