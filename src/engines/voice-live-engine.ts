import crypto from 'node:crypto';
import WebSocket from 'ws';
import { connectSessionSocket, waitForSessionEvent } from './ws-session.ts';
import { DEFAULT_FOUNDRY_CONFIG, tokenManager } from '../azure-auth.ts';
import type { FoundryConfig } from '../azure-auth.ts';
import {
  buildDota2AsrPhraseList,
  buildInterpreterSystemPrompt,
  buildUserTurnWithMatchedGlossary,
  isPureEnglishUtterance,
} from '../dota2-glossary.ts';
import type { TranslationDirection } from '../dota2-glossary.ts';
import type {
  EngineCallbacks,
  EngineOptions,
  ParticipantRole,
  TranslationEngine,
  TtsOptionsUpdate,
  VoiceLiveAsrModel,
} from './types.ts';

export const DEFAULT_VOICELIVE_ZH_VOICE = 'zh-CN-XiaoxiaoMultilingualNeural';
export const DEFAULT_VOICELIVE_EN_VOICE = 'en-US-Andrew:DragonHDLatestNeural';
export const DEFAULT_VOICELIVE_CASCADE_MODEL = 'gpt-6-luna';
export const DEFAULT_VOICELIVE_REALTIME_MODEL = 'gpt-realtime-2.1';

const OPENAI_NATIVE_VOICES = new Set([
  'alloy',
  'ash',
  'ballad',
  'cedar',
  'coral',
  'echo',
  'marin',
  'sage',
  'shimmer',
  'verse',
]);

interface ActiveResponseState {
  turnId: string;
  speechEndTs: number;
  asrCompletedMs?: number;
  firstTokenMs?: number;
  firstAudioMs: number | null;
  outputBytes: number;
  targetTranscript: string;
  sourceTranscript: string;
  deltaStreamType?: string;
}

export type VoiceLivePipelineMode = 'cascade' | 'gpt-realtime';

/**
 * Unified Azure Voice Live Engine (`/voice-live/realtime?api-version=v1`).
 *
 * Drives BOTH:
 * - `cascade`: Server-orchestrated Cascaded pipeline (`azure-speech` / `mai-transcribe-2-streaming` ASR
 *   + `azure_semantic_vad` + `gpt-6-luna` streaming text LLM + streaming Azure TTS)
 * - `gpt-realtime`: End-to-end Speech-to-Speech (`gpt-realtime-2.1` + `azure_semantic_vad` + shared
 *   selectable Azure Standard/DragonHD TTS voices or OpenAI native voices)
 *
 * Using one unified protocol & selectable voice catalog guarantees true apples-to-apples latency comparisons
 * with zero Speech SDK dependency and 0ms drop mid-game hero/voice updates via `session.update`.
 */
export class VoiceLiveEngine implements TranslationEngine {
  readonly kind: VoiceLivePipelineMode;
  readonly role: ParticipantRole;
  private readonly direction: TranslationDirection;
  private readonly callbacks: EngineCallbacks;
  private readonly config: FoundryConfig;
  private readonly model: string;

  private matchHeroes: string[];
  private systemPrompt: string;
  private ttsZhVoice: string;
  private ttsEnVoice: string;
  private asrModel: VoiceLiveAsrModel;

  private ws: WebSocket | null = null;
  private stopped = false;
  private uncommittedBytes = 0;
  private vadCommitted = false;
  private speechStoppedAt: number | null = null;
  private latestAsrMs: number | undefined;
  private latestInputTranscript = '';
  private inputItemDeltas = new Map<string, string>();
  private activeResponse: ActiveResponseState | null = null;
  private recentItemIds: string[] = [];

  constructor(
    mode: VoiceLivePipelineMode,
    options: EngineOptions,
    callbacks: EngineCallbacks,
    config: FoundryConfig = DEFAULT_FOUNDRY_CONFIG,
  ) {
    this.kind = mode;
    this.role = options.role;
    this.direction = options.role === 'coach' ? 'coach-en-to-zh' : 'student-zh-or-en-to-en';
    this.matchHeroes = [...(options.matchHeroes ?? [])];
    this.systemPrompt = this.composeInstructions();
    this.callbacks = callbacks;
    this.config = config;

    this.model =
      options.voiceLiveModel ||
      (mode === 'cascade'
        ? process.env.VOICELIVE_CASCADE_MODEL || DEFAULT_VOICELIVE_CASCADE_MODEL
        : process.env.VOICELIVE_REALTIME_MODEL || DEFAULT_VOICELIVE_REALTIME_MODEL);

    this.ttsZhVoice = options.ttsZhVoice || process.env.AZURE_TTS_ZH_VOICE || DEFAULT_VOICELIVE_ZH_VOICE;
    this.ttsEnVoice = options.ttsEnVoice || process.env.AZURE_TTS_EN_VOICE || DEFAULT_VOICELIVE_EN_VOICE;
    this.asrModel = options.voiceLiveAsrModel || 'azure-speech';
  }

  private composeInstructions(): string {
    const base = buildInterpreterSystemPrompt(this.direction, {
      matchHeroes: this.matchHeroes,
    });
    if (this.kind === 'cascade') {
      return (
        `${base}\n\n` +
        `STREAMING TTS LATENCY RULE: End each short tactical clause with a period ("。" in Chinese, "." in English) instead of chaining multiple clauses with commas, so the server-side streaming TTS speaks the first clause immediately.`
      );
    }
    return base;
  }

  private resolveOutputVoice(): { voice: string; voice_type: 'azure-standard' | 'openai' } {
    const raw = this.role === 'coach' ? this.ttsZhVoice : this.ttsEnVoice;
    const stripped = raw.startsWith('openai:') ? raw.slice('openai:'.length) : raw;
    if (OPENAI_NATIVE_VOICES.has(stripped)) {
      if (this.kind === 'cascade') {
        // Cascaded text models only support azure-standard / custom / personal voices; fall back safely
        const fallback = this.role === 'coach' ? DEFAULT_VOICELIVE_ZH_VOICE : DEFAULT_VOICELIVE_EN_VOICE;
        return { voice: fallback, voice_type: 'azure-standard' };
      }
      return { voice: stripped, voice_type: 'openai' };
    }
    return { voice: stripped, voice_type: 'azure-standard' };
  }

  private buildTranscriptionConfig(): Record<string, unknown> {
    if (this.asrModel === 'mai-transcribe-2-streaming') {
      const cfg: Record<string, unknown> = { model: 'mai-transcribe-2-streaming' };
      if (this.role === 'coach') {
        cfg.language = 'en';
      }
      return cfg;
    }

    const phraseList = buildDota2AsrPhraseList(this.role, this.matchHeroes);
    if (this.asrModel === 'mai-transcribe-2') {
      const cfg: Record<string, unknown> = {
        model: 'mai-transcribe-2',
        phrase_list: phraseList,
      };
      if (this.role === 'coach') {
        cfg.language = 'en';
      }
      return cfg;
    }

    // Default: `azure-speech` — supports bilingual ZH+EN code-switching + DOTA2 phrase_list biasing!
    return {
      model: 'azure-speech',
      language: this.role === 'coach' ? 'en-US' : 'zh-CN',
      phrase_list: phraseList,
    };
  }

  private buildSessionPayload(): Record<string, unknown> {
    const { voice, voice_type } = this.resolveOutputVoice();
    const isCascaded = this.kind === 'cascade';

    const payload: Record<string, unknown> = {
      type: 'realtime',
      output_modalities: isCascaded ? ['text', 'audio'] : ['audio'],
      instructions: this.systemPrompt,
      audio: {
        input: {
          format: { type: 'audio/pcm', rate: 24000 },
          transcription: this.buildTranscriptionConfig(),
          turn_detection: {
            type: 'azure_semantic_vad',
            threshold: 0.5,
            prefix_padding_ms: 240,
            silence_duration_ms: 480,
            create_response: true,
            interrupt_response: false,
          },
        },
        output: {
          format: { type: 'audio/pcm', rate: 24000 },
          voice,
          voice_type,
        },
      },
    };

    if (isCascaded) {
      if (this.model.startsWith('gpt-6')) {
        payload.reasoning = { effort: 'none' };
      } else if (this.model.startsWith('gpt-5')) {
        payload.reasoning = { effort: 'minimal' };
      } else {
        payload.temperature = 0.1;
      }
    } else if (this.model.includes('2')) {
      payload.reasoning = { effort: 'minimal' };
    }

    return payload;
  }

  async start(): Promise<void> {
    this.stopped = false;
    await this.connect();
    const { voice } = this.resolveOutputVoice();
    this.callbacks.onStatus(
      `VoiceLive [${this.kind}] ready (${this.asrModel} -> ${this.model} -> ${voice})`,
    );
  }

  private async connect(): Promise<void> {
    this.callbacks.onConnectionState?.('connecting');
    const authHeaders = await tokenManager.getAuthHeaders(this.config);
    const query = new URLSearchParams({
      'api-version': 'v1',
      model: this.model,
    });
    const url = `wss://${this.config.aiServicesHost}/voice-live/realtime?${query.toString()}`;

    const ws = await connectSessionSocket(url, {
      headers: { ...authHeaders, 'x-ms-client-request-id': crypto.randomUUID() },
      handshakeTimeout: 12_000,
    }, 'session.created');
    try {
      await this.sendSessionUpdateAndWait(ws, this.buildSessionPayload());
      if (this.stopped) throw new Error('VoiceLive startup cancelled');
    } catch (error) { ws.terminate(); throw error; }

    ws.on('message', (raw: WebSocket.RawData) => {
      this.handleServerEvent(raw.toString());
    });

    ws.on('close', () => {
      if (!this.stopped && this.ws === ws) {
        this.callbacks.onConnectionState?.('reconnecting');
        this.callbacks.onStatus(`VoiceLive [${this.kind}] reconnecting...`);
        setTimeout(() => {
          if (!this.stopped) {
            this.connect().catch((err) => {
              if (this.stopped) return;
              this.callbacks.onConnectionState?.('failed');
              this.callbacks.onError(`VoiceLive reconnect failed: ${err.message}`);
            });
          }
        }, 800);
      }
    });

    ws.on('error', (err) => {
      if (!this.stopped) {
        this.callbacks.onError(`VoiceLive WS error: ${err.message}`);
      }
    });

    this.ws = ws;
    this.callbacks.onConnectionState?.('ready');
  }

  private sendSessionUpdateAndWait(ws: WebSocket, sessionPayload: Record<string, unknown>): Promise<void> {
    return waitForSessionEvent(ws, 'session.updated', () => {
      ws.send(JSON.stringify({ type: 'session.update', session: sessionPayload }));
    }, 10_000);
  }

  private handleServerEvent(jsonStr: string): void {
    let evt: any;
    try {
      evt = JSON.parse(jsonStr);
    } catch {
      return;
    }

    switch (evt.type) {
      case 'input_audio_buffer.speech_started': {
        this.vadCommitted = false;
        this.speechStoppedAt = null;
        this.latestAsrMs = undefined;
        this.latestInputTranscript = '';
        break;
      }

      case 'input_audio_buffer.speech_stopped': {
        if (this.speechStoppedAt === null) {
          this.speechStoppedAt = performance.now();
        }
        break;
      }

      case 'input_audio_buffer.committed': {
        this.vadCommitted = true;
        this.uncommittedBytes = 0;
        if (this.speechStoppedAt === null) {
          this.speechStoppedAt = performance.now();
        }
        break;
      }

      case 'conversation.item.added':
      case 'conversation.item.created': {
        const itemId = evt.item?.id;
        if (itemId && !this.recentItemIds.includes(itemId)) {
          this.recentItemIds.push(itemId);
          // Retain only last 6 items (~3 turns) over 45-min DOTA2 games to prevent context bloat
          while (this.recentItemIds.length > 6) {
            const oldest = this.recentItemIds.shift()!;
            if (this.ws && this.ws.readyState === WebSocket.OPEN) {
              this.ws.send(JSON.stringify({ type: 'conversation.item.delete', item_id: oldest }));
            }
          }
        }
        break;
      }

      case 'conversation.item.input_audio_transcription.delta': {
        const itemId = evt.item_id || 'default';
        const next = (this.inputItemDeltas.get(itemId) || '') + (evt.delta || '');
        this.inputItemDeltas.set(itemId, next);
        this.callbacks.onSourcePartial(next);
        break;
      }

      case 'conversation.item.input_audio_transcription.intermediate': {
        const itemId = evt.item_id || 'default';
        const preview = (this.inputItemDeltas.get(itemId) || '') + (evt.transcript || '');
        if (preview.trim()) {
          this.callbacks.onSourcePartial(preview);
        }
        break;
      }

      case 'conversation.item.input_audio_transcription.completed': {
        const itemId = evt.item_id || 'default';
        this.inputItemDeltas.delete(itemId);
        const transcript = (evt.transcript || '').trim();
        if (transcript) {
          this.latestInputTranscript = transcript;
          if (this.speechStoppedAt !== null) {
            this.latestAsrMs = Math.max(1, Math.round(performance.now() - this.speechStoppedAt));
          }
          if (this.activeResponse) {
            this.activeResponse.sourceTranscript = transcript;
            if (this.activeResponse.asrCompletedMs === undefined && this.latestAsrMs !== undefined) {
              this.activeResponse.asrCompletedMs = this.latestAsrMs;
            }
          }
          this.callbacks.onSourceFinal(this.activeResponse?.turnId || itemId, transcript);
        }
        break;
      }

      case 'conversation.item.input_audio_transcription.failed': {
        this.callbacks.onError(`VoiceLive [${this.asrModel}] 输入转录失败: ${evt.error?.message || evt.error?.code || 'Unknown error'}`);
        break;
      }

      case 'response.created': {
        const turnId = evt.response?.id || `vl_${crypto.randomUUID().slice(0, 8)}`;
        const speechEndTs = this.speechStoppedAt ?? performance.now();
        this.activeResponse = {
          turnId,
          speechEndTs,
          asrCompletedMs: this.latestAsrMs,
          firstAudioMs: null,
          outputBytes: 0,
          targetTranscript: '',
          sourceTranscript: this.latestInputTranscript,
        };
        break;
      }

      case 'response.output_audio.delta':
      case 'response.audio.delta': {
        if (!this.activeResponse) {
          this.activeResponse = {
            turnId: `vl_${crypto.randomUUID().slice(0, 8)}`,
            speechEndTs: this.speechStoppedAt ?? performance.now(),
            asrCompletedMs: this.latestAsrMs,
            firstAudioMs: null,
            outputBytes: 0,
            targetTranscript: '',
            sourceTranscript: this.latestInputTranscript,
          };
        }
        if (evt.delta) {
          const pcm = Buffer.from(evt.delta, 'base64');
          if (pcm.length > 0) {
            if (this.activeResponse.firstAudioMs === null) {
              this.activeResponse.firstAudioMs = Math.max(
                1,
                Math.round(performance.now() - this.activeResponse.speechEndTs),
              );
            }
            this.activeResponse.outputBytes += pcm.length;
            this.callbacks.onTargetAudio(this.activeResponse.turnId, pcm);
          }
        }
        break;
      }

      case 'response.output_audio_transcript.delta':
      case 'response.audio_transcript.delta':
      case 'response.output_text.delta':
      case 'response.text.delta': {
        if (this.activeResponse && evt.delta) {
          if (this.activeResponse.firstTokenMs === undefined) {
            this.activeResponse.firstTokenMs = Math.max(
              1,
              Math.round(performance.now() - this.activeResponse.speechEndTs),
            );
          }
          if (!this.activeResponse.deltaStreamType) {
            this.activeResponse.deltaStreamType = evt.type;
          }
          if (this.activeResponse.deltaStreamType === evt.type) {
            this.activeResponse.targetTranscript += evt.delta;
            this.callbacks.onTargetPartial(
              this.activeResponse.turnId,
              this.activeResponse.targetTranscript,
            );
          }
        }
        break;
      }

      case 'response.done': {
        this.speechStoppedAt = null;
        if (this.activeResponse) {
          const r = this.activeResponse;
          this.activeResponse = null;
          const totalTurnMs = Math.max(1, Math.round(performance.now() - r.speechEndTs));

          // Fallback transcript extraction from response.output if delta stream used alternate event name
          let target = r.targetTranscript.trim();
          if (!target && Array.isArray(evt.response?.output)) {
            const parts: string[] = [];
            for (const item of evt.response.output) {
              for (const c of item?.content ?? []) {
                if (typeof c?.text === 'string') parts.push(c.text);
                else if (typeof c?.transcript === 'string') parts.push(c.transcript);
              }
            }
            target = parts.join('').trim();
          }

          const source = (r.sourceTranscript || this.latestInputTranscript).trim() || '(audio input)';
          if (target) {
            this.callbacks.onTargetFinal(r.turnId, target);
          }
          const isModelRepeat =
            this.role === 'student' && source !== '(audio input)' && isPureEnglishUtterance(source);
          const { voice } = this.resolveOutputVoice();

          if (r.outputBytes > 0 || target) {
            this.callbacks.onTurnMetrics({
              turnId: r.turnId,
              engine: this.kind,
              role: this.role,
              sourceText: source,
              targetText: target,
              passThrough: isModelRepeat ? 'model-repeat' : 'none',
              speechEndToFirstAudioMs: r.firstAudioMs ?? totalTurnMs,
              totalTurnMs,
              asrMs: r.asrCompletedMs,
              llmFirstTokenMs: r.firstTokenMs,
              ttsVoice: voice,
              voiceLiveModel: this.model,
              voiceLiveAsrModel: this.asrModel,
              outputAudioDurationMs: Math.round((r.outputBytes / 48000) * 1000),
              timestamp: Date.now(),
            });
          }
        }
        break;
      }

      case 'error': {
        const msg = evt.error?.message || JSON.stringify(evt);
        const lower = msg.toLowerCase();
        if (
          !lower.includes('buffer too small') &&
          !lower.includes('commit_empty') &&
          !lower.includes('without audio') &&
          !lower.includes('already has an active response')
        ) {
          this.callbacks.onError(`VoiceLive [${this.kind}]: ${msg}`);
        }
        break;
      }
    }
  }

  appendAudio(pcm24kMono: Buffer): void {
    if (this.stopped || !pcm24kMono.length) return;
    if (this.ws && this.ws.readyState === WebSocket.OPEN) {
      this.ws.send(
        JSON.stringify({
          type: 'input_audio_buffer.append',
          audio: pcm24kMono.toString('base64'),
        }),
      );
      this.uncommittedBytes += pcm24kMono.length;
    }
  }

  markSpeechEnd(): void {
    if (this.speechStoppedAt === null) {
      this.speechStoppedAt = performance.now();
    }
    if (
      this.ws &&
      this.ws.readyState === WebSocket.OPEN &&
      !this.vadCommitted &&
      this.uncommittedBytes >= 4800
    ) {
      this.ws.send(JSON.stringify({ type: 'input_audio_buffer.commit' }));
      this.ws.send(JSON.stringify({ type: 'response.create' }));
      this.uncommittedBytes = 0;
      this.vadCommitted = true;
    }
  }

  async updateMatchHeroes(heroes: string[]): Promise<void> {
    this.matchHeroes = [...heroes];
    this.systemPrompt = this.composeInstructions();
    if (this.ws && this.ws.readyState === WebSocket.OPEN) {
      this.ws.send(
        JSON.stringify({
          type: 'session.update',
          session: this.buildSessionPayload(),
        }),
      );
      this.callbacks.onStatus(
        `VoiceLive [${this.kind}] match heroes updated (${this.matchHeroes.length}) — 0ms WS drop`,
      );
    }
  }

  async updateTtsOptions(opts: TtsOptionsUpdate): Promise<void> {
    if (opts.ttsZhVoice) this.ttsZhVoice = opts.ttsZhVoice;
    if (opts.ttsEnVoice) this.ttsEnVoice = opts.ttsEnVoice;
    if (opts.voiceLiveAsrModel) this.asrModel = opts.voiceLiveAsrModel;

    if (this.ws && this.ws.readyState === WebSocket.OPEN) {
      this.ws.send(
        JSON.stringify({
          type: 'session.update',
          session: this.buildSessionPayload(),
        }),
      );
      const { voice } = this.resolveOutputVoice();
      this.callbacks.onStatus(
        `VoiceLive [${this.kind}] updated -> ASR:${this.asrModel} · Voice:${voice} (0ms WS drop)`,
      );
    }
  }

  async translateText(text: string): Promise<void> {
    const trimmed = text.trim();
    if (!trimmed || !this.ws || this.ws.readyState !== WebSocket.OPEN) return;
    const { normalizedText, userPrompt } = buildUserTurnWithMatchedGlossary(trimmed);
    this.latestInputTranscript = normalizedText;
    this.latestAsrMs = 0;
    this.speechStoppedAt = performance.now();
    this.callbacks.onSourceFinal(`txt_${crypto.randomUUID().slice(0, 6)}`, normalizedText);
    this.ws.send(
      JSON.stringify({
        type: 'conversation.item.create',
        item: {
          type: 'message',
          role: 'user',
          content: [{ type: 'input_text', text: userPrompt }],
        },
      }),
    );
    this.ws.send(JSON.stringify({ type: 'response.create' }));
  }

  async stop(): Promise<void> {
    this.stopped = true;
    if (this.ws) {
      this.ws.close();
      this.ws = null;
    }
  }
}
