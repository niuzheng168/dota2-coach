import crypto from 'node:crypto';
import WebSocket from 'ws';
import { connectSessionSocket } from './ws-session.ts';
import { getGptLiveFoundryConfig, tokenManager } from '../azure-auth.ts';
import type { FoundryConfig } from '../azure-auth.ts';
import {
  buildInterpreterSystemPrompt,
  buildUserTurnWithMatchedGlossary,
  formatMatchHeroesSection,
  isPureEnglishUtterance,
} from '../dota2-glossary.ts';
import type { TranslationDirection } from '../dota2-glossary.ts';
import type {
  EngineCallbacks,
  EngineOptions,
  ParticipantRole,
  TranslationEngine,
} from './types.ts';

// 60ms @ 24kHz 16-bit mono = 1440 samples = 2880 bytes
const PACE_INTERVAL_MS = 60;
const PACE_CHUNK_BYTES = 24 * PACE_INTERVAL_MS * 2;
const UTTERANCE_IDLE_FINALIZE_MS = 650;

export class GptLiveEngine implements TranslationEngine {
  readonly kind = 'gpt-live' as const;
  readonly role: ParticipantRole;
  private readonly direction: TranslationDirection;
  private readonly callbacks: EngineCallbacks;
  private readonly config: FoundryConfig;
  private readonly deployment: string;
  private systemPrompt: string;

  private ws: WebSocket | null = null;
  private stopped = false;
  private sessionStarted = false;

  // Continuous real-time 24kHz PCM FIFO queue + silence pacer
  private outboundQueue: Buffer[] = [];
  private outboundQueuedBytes = 0;
  private pacerTimer: NodeJS.Timeout | null = null;
  private hadActiveSpeechInQueue = false;
  private speechEndedAt: number | null = null;

  // Continuous stream segmentation into readable UI turns & metrics
  private currentTurnId = `live_${crypto.randomUUID().slice(0, 8)}`;
  private sourceBuffer = '';
  private targetBuffer = '';
  private firstOutputAudioAt: number | null = null;
  private turnAnchorTs: number | null = null;
  private turnOutputBytes = 0;
  private finalizeTimer: NodeJS.Timeout | null = null;
  private activeHeroes: string[] = [];

  constructor(
    options: EngineOptions,
    callbacks: EngineCallbacks,
    config: FoundryConfig = getGptLiveFoundryConfig(),
  ) {
    this.role = options.role;
    this.direction = options.role === 'coach' ? 'coach-en-to-zh' : 'student-zh-or-en-to-en';
    this.activeHeroes = (options.matchHeroes || []).map((h) => h.trim()).filter(Boolean);
    this.systemPrompt = this.composeLivePrompt(this.activeHeroes);
    this.callbacks = callbacks;
    this.config = config;
    this.deployment = options.liveDeployment || config.deployments.gptLive;
  }

  private composeLivePrompt(matchHeroes?: string[]): string {
    return [
      buildInterpreterSystemPrompt(this.direction, { matchHeroes }),
      '',
      'LIVE FULL-DUPLEX STREAM RULES:',
      '- Remain completely silent on startup and during silence/background noise. NEVER greet first.',
      '- Begin interpreting immediately as soon as the speaker finishes a clause or short pause — do not wait for multi-second silences.',
    ].join('\n');
  }

  async start(): Promise<void> {
    this.stopped = false;
    await this.connect();
    this.startPacer();
    this.callbacks.onStatus(`GPT-Live ready (${this.deployment}, full-duplex)`);
  }

  private async connect(): Promise<void> {
    this.callbacks.onConnectionState?.('connecting');
    const authHeaders = await tokenManager.getAuthHeaders(this.config);
    const url = `wss://${this.config.openAiHost}/openai/v1/live/sessions`;

    const ws = await connectSessionSocket(url, {
      headers: { ...authHeaders, 'x-ms-client-request-id': crypto.randomUUID() },
      handshakeTimeout: 12_000,
      maxPayload: 2 * 1024 * 1024,
    }, 'session.started', {
      type: 'session.start',
      session: {
        model: this.deployment, store: false, instructions: this.systemPrompt,
        audio: {
          format: { type: 'audio/pcm', rate: 24000 },
          output: { voice: this.role === 'coach' ? 'marin' : 'cedar' },
        },
      },
    });
    if (this.stopped) { ws.terminate(); throw new Error('GPT-Live startup cancelled'); }
    this.sessionStarted = true;

    ws.on('message', (raw: WebSocket.RawData, isBinary: boolean) => {
      if (isBinary) return;
      this.handleLiveEvent(raw.toString());
    });

    ws.on('close', () => {
      if (this.ws !== ws) return;
      this.sessionStarted = false;
      if (!this.stopped && this.ws === ws) {
        this.callbacks.onConnectionState?.('reconnecting');
        this.callbacks.onStatus('GPT-Live reconnecting...');
        setTimeout(() => {
          if (!this.stopped) {
            this.connect().catch((err) => {
              if (this.stopped) return;
              this.callbacks.onConnectionState?.('failed');
              this.callbacks.onError(`GPT-Live reconnect failed: ${err.message}`);
            });
          }
        }, 1000);
      }
    });

    ws.on('error', (err) => {
      if (!this.stopped) {
        this.callbacks.onError(`GPT-Live WS error: ${err.message}`);
      }
    });

    this.ws = ws;
    this.callbacks.onConnectionState?.('ready');
  }

  private startPacer(): void {
    if (this.pacerTimer) clearInterval(this.pacerTimer);
    this.pacerTimer = setInterval(() => {
      if (this.stopped || !this.sessionStarted || !this.ws || this.ws.readyState !== WebSocket.OPEN) {
        return;
      }

      const frame = Buffer.alloc(PACE_CHUNK_BYTES); // zero-filled (silence) by default
      let copied = 0;

      while (copied < PACE_CHUNK_BYTES && this.outboundQueue.length > 0) {
        const head = this.outboundQueue[0];
        const needed = PACE_CHUNK_BYTES - copied;
        if (head.length <= needed) {
          head.copy(frame, copied);
          copied += head.length;
          this.outboundQueuedBytes -= head.length;
          this.outboundQueue.shift();
        } else {
          head.copy(frame, copied, 0, needed);
          this.outboundQueue[0] = head.subarray(needed);
          this.outboundQueuedBytes -= needed;
          copied += needed;
        }
      }

      // Detect exact moment queued real speech finishes draining into the Live socket
      if (this.hadActiveSpeechInQueue && this.outboundQueuedBytes === 0) {
        this.hadActiveSpeechInQueue = false;
        if (this.speechEndedAt === null) {
          this.speechEndedAt = performance.now();
        }
      }

      this.ws.send(
        JSON.stringify({
          type: 'session.input_audio.append',
          audio: frame.toString('base64'),
        }),
      );
    }, PACE_INTERVAL_MS);
  }

  private scheduleTurnFinalize(): void {
    if (this.finalizeTimer) clearTimeout(this.finalizeTimer);
    this.finalizeTimer = setTimeout(() => {
      this.flushCurrentTurn();
    }, UTTERANCE_IDLE_FINALIZE_MS);
  }

  private flushCurrentTurn(): void {
    if (!this.targetBuffer.trim() && this.turnOutputBytes === 0) {
      return;
    }
    const turnId = this.currentTurnId;
    const sourceText = this.sourceBuffer.trim() || '(live stream)';
    const targetText = this.targetBuffer.trim();
    const now = performance.now();
    const speechEnd = this.speechEndedAt ?? this.turnAnchorTs ?? now;
    const actualFinishTs = now - UTTERANCE_IDLE_FINALIZE_MS;
    const totalTurnMs = Math.max(1, Math.round(actualFinishTs - speechEnd));
    const firstAudioMs =
      this.firstOutputAudioAt !== null
        ? Math.max(0, Math.round(this.firstOutputAudioAt - speechEnd))
        : totalTurnMs;

    const outputAudioDurationMs = Math.round((this.turnOutputBytes / 48000) * 1000);

    // Reset accumulators before firing callbacks to prevent re-entrant double-flush if stop() is called
    this.currentTurnId = `live_${crypto.randomUUID().slice(0, 8)}`;
    this.sourceBuffer = '';
    this.targetBuffer = '';
    this.firstOutputAudioAt = null;
    this.turnAnchorTs = null;
    this.turnOutputBytes = 0;
    this.speechEndedAt = null;

    if (sourceText !== '(live stream)') {
      this.callbacks.onSourceFinal(turnId, sourceText);
    }
    if (targetText) {
      this.callbacks.onTargetFinal(turnId, targetText);
    }

    const isModelRepeat =
      this.role === 'student' &&
      sourceText !== '(live stream)' &&
      isPureEnglishUtterance(sourceText);

    this.callbacks.onTurnMetrics({
      turnId,
      engine: 'gpt-live',
      role: this.role,
      sourceText,
      targetText,
      passThrough: isModelRepeat ? 'model-repeat' : 'none',
      speechEndToFirstAudioMs: firstAudioMs,
      totalTurnMs: Math.max(firstAudioMs, totalTurnMs),
      outputAudioDurationMs,
      timestamp: Date.now(),
    });
  }

  private handleLiveEvent(jsonStr: string): void {
    let evt: any;
    try {
      evt = JSON.parse(jsonStr);
    } catch {
      return;
    }

    switch (evt.type) {
      case 'session.input_transcript.delta': {
        if (evt.delta) {
          this.sourceBuffer += evt.delta;
          if (this.turnAnchorTs === null) {
            this.turnAnchorTs = performance.now();
          }
          this.callbacks.onSourcePartial(this.sourceBuffer);
        }
        break;
      }

      case 'session.output_transcript.delta': {
        if (evt.delta) {
          this.targetBuffer += evt.delta;
          this.callbacks.onTargetPartial(this.currentTurnId, this.targetBuffer);
          this.scheduleTurnFinalize();
        }
        break;
      }

      case 'session.output_audio.delta': {
        if (evt.delta) {
          const pcm = Buffer.from(evt.delta, 'base64');
          if (pcm.length > 0 && this.hasAudibleSpeech(pcm)) {
            if (this.firstOutputAudioAt === null) {
              this.firstOutputAudioAt = performance.now();
            }
            this.turnOutputBytes += pcm.length;
            this.callbacks.onTargetAudio(this.currentTurnId, pcm);
            this.scheduleTurnFinalize();
          }
        }
        break;
      }

      case 'error': {
        const msg = `${evt.error?.code || 'error'}: ${evt.error?.message || JSON.stringify(evt)}`;
        this.callbacks.onError(`GPT-Live: ${msg}`);
        break;
      }
    }
  }

  /**
   * gpt-live-1 emits continuous zero-filled PCM frames during idle silence.
   * Only treat frames with non-trivial sample amplitude as real spoken output.
   */
  private hasAudibleSpeech(pcm: Buffer, threshold = 80): boolean {
    const samples = Math.floor(pcm.length / 2);
    for (let i = 0; i < samples; i++) {
      if (Math.abs(pcm.readInt16LE(i * 2)) > threshold) {
        return true;
      }
    }
    return false;
  }

  appendAudio(pcm24kMono: Buffer): void {
    if (this.stopped || !pcm24kMono.length) return;
    const aligned =
      pcm24kMono.length % 2 === 0
        ? pcm24kMono
        : pcm24kMono.subarray(0, pcm24kMono.length - 1);
    if (!aligned.length) return;

    this.outboundQueue.push(aligned);
    this.outboundQueuedBytes += aligned.length;
    this.hadActiveSpeechInQueue = true;
    this.speechEndedAt = null;
  }

  markSpeechEnd(): void {
    if (this.outboundQueuedBytes === 0 && this.speechEndedAt === null) {
      this.speechEndedAt = performance.now();
    }
  }

  private sendInstructionChunks(textBlock: string): void {
    if (!this.ws || this.ws.readyState !== WebSocket.OPEN || !this.sessionStarted) return;
    const footer =
      '(Silent context update only — do NOT speak or acknowledge this instruction update; wait for real user speech.)';
    const maxChunkBytes = 900; // Safely below gpt-live's 500-token per-append ceiling
    const lines = textBlock.split('\n').filter((l) => l.trim().length > 0);
    const chunks: string[] = [];
    let current = '';

    for (const line of lines) {
      const candidate = current ? `${current}\n${line}` : line;
      if (current && Buffer.byteLength(`${candidate}\n${footer}`, 'utf8') > maxChunkBytes) {
        chunks.push(`${current}\n${footer}`);
        current = line;
      } else {
        current = candidate;
      }
    }
    if (current) {
      chunks.push(`${current}\n${footer}`);
    }

    for (const content of chunks) {
      this.ws.send(
        JSON.stringify({
          type: 'session.instructions.append',
          event_id: `bp_${crypto.randomUUID().slice(0, 12)}`,
          delegation_id: null,
          content,
        }),
      );
    }
  }

  async updateMatchHeroes(heroes: string[]): Promise<void> {
    const nextHeroes = heroes.map((h) => h.trim()).filter(Boolean);
    const prevLower = new Set(this.activeHeroes.map((h) => h.toLowerCase()));
    const nextLower = new Set(nextHeroes.map((h) => h.toLowerCase()));

    const unchanged =
      prevLower.size === nextLower.size && Array.from(prevLower).every((h) => nextLower.has(h));
    if (unchanged) return;

    const removedAny = Array.from(prevLower).some((h) => !nextLower.has(h));
    const newlyAdded = nextHeroes.filter((h) => !prevLower.has(h.toLowerCase()));

    this.activeHeroes = nextHeroes;
    // Keep full systemPrompt updated in memory so any future reconnect starts with the full state
    this.systemPrompt = this.composeLivePrompt(nextHeroes);

    // Inject mid-session via native session.instructions.append — ZERO WebSocket drop during BP!
    if (!this.stopped && this.sessionStarted && this.ws && this.ws.readyState === WebSocket.OPEN) {
      if (nextHeroes.length === 0) {
        this.sendInstructionChunks(
          '=== MATCH HEROES CLEARED (NEW DRAFT / BP) ===\nPrevious match hero overrides are cleared. Rely on the full 127-hero DOTA2 dictionary until new heroes are picked.',
        );
      } else if (!removedAny && newlyAdded.length > 0) {
        const deltaBlock = formatMatchHeroesSection(newlyAdded);
        if (deltaBlock) {
          this.sendInstructionChunks(deltaBlock);
        }
      } else {
        const fullBlock = formatMatchHeroesSection(nextHeroes);
        if (fullBlock) {
          this.sendInstructionChunks(
            `=== UPDATED ACTIVE MATCH HEROES (REPLACES PREVIOUS MATCH HERO LIST) ===\n${fullBlock}`,
          );
        }
      }
      this.callbacks.onStatus(
        `GPT-Live BP heroes updated live (${nextHeroes.length}/10, 0ms audio interruption)`,
      );
    }
  }

  async translateText(text: string): Promise<void> {
    const trimmed = text.trim();
    if (!trimmed || !this.ws || this.ws.readyState !== WebSocket.OPEN || !this.sessionStarted) {
      return;
    }
    const { normalizedText, matchedTerms } = buildUserTurnWithMatchedGlossary(trimmed);
    this.sourceBuffer = normalizedText;
    this.speechEndedAt = performance.now();
    this.callbacks.onSourceFinal(this.currentTurnId, normalizedText);

    const hintSuffix =
      matchedTerms.length > 0 ? `\nGlossary hints:\n${matchedTerms.slice(0, 5).join('\n')}` : '';
    const prompt = `Interpret and speak ONLY the translation of this chat message immediately: "${normalizedText}"${hintSuffix}`;
    const bounded = Buffer.from(prompt, 'utf8').subarray(0, 900).toString('utf8');

    this.ws.send(
      JSON.stringify({
        type: 'session.commentary.append',
        event_id: `txt_${crypto.randomUUID().slice(0, 12)}`,
        delegation_id: null,
        content: bounded,
      }),
    );
  }

  async stop(): Promise<void> {
    this.stopped = true;
    this.sessionStarted = false;
    if (this.pacerTimer) {
      clearInterval(this.pacerTimer);
      this.pacerTimer = null;
    }
    if (this.finalizeTimer) {
      clearTimeout(this.finalizeTimer);
      this.finalizeTimer = null;
    }
    this.flushCurrentTurn();
    if (this.ws) {
      if (this.ws.readyState === WebSocket.OPEN) {
        try {
          this.ws.send(JSON.stringify({ type: 'session.close' }));
        } catch {
          // ignore
        }
      }
      this.ws.close();
      this.ws = null;
    }
  }
}
