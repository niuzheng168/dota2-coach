import crypto from 'node:crypto';
import WebSocket from 'ws';
import { DEFAULT_FOUNDRY_CONFIG, tokenManager } from '../azure-auth.ts';
import type { FoundryConfig } from '../azure-auth.ts';
import {
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
} from './types.ts';

interface ActiveResponseState {
  turnId: string;
  speechEndTs: number;
  firstAudioMs: number | null;
  outputBytes: number;
  targetTranscript: string;
  sourceTranscript: string;
}

/**
 * @deprecated Superseded by unified {@link VoiceLiveEngine} (`mode = 'gpt-realtime'`) over
 * Azure Voice Live (`/voice-live/realtime?api-version=v1`), which shares selectable Azure TTS voices
 * and `azure_semantic_vad` with Cascaded mode for apples-to-apples latency comparison.
 * Preserved intact for reference.
 */
export class GptRealtimeEngine implements TranslationEngine {
  readonly kind = 'gpt-realtime' as const;
  readonly role: ParticipantRole;
  private readonly direction: TranslationDirection;
  private readonly callbacks: EngineCallbacks;
  private readonly config: FoundryConfig;
  private readonly deployment: string;
  private systemPrompt: string;

  private ws: WebSocket | null = null;
  private stopped = false;
  private uncommittedBytes = 0;
  private vadCommitted = false;
  private speechStoppedAt: number | null = null;
  private latestInputTranscript = '';
  private inputItemDeltas = new Map<string, string>();
  private activeResponse: ActiveResponseState | null = null;
  private recentItemIds: string[] = [];

  constructor(
    options: EngineOptions,
    callbacks: EngineCallbacks,
    config: FoundryConfig = DEFAULT_FOUNDRY_CONFIG,
  ) {
    this.role = options.role;
    this.direction = options.role === 'coach' ? 'coach-en-to-zh' : 'student-zh-or-en-to-en';
    this.systemPrompt = buildInterpreterSystemPrompt(this.direction, {
      matchHeroes: options.matchHeroes,
    });
    this.callbacks = callbacks;
    this.config = config;
    this.deployment = options.realtimeDeployment || config.deployments.gptRealtime;
  }

  async start(): Promise<void> {
    this.stopped = false;
    await this.connect();
    this.callbacks.onStatus(`GPT-Realtime ready (${this.deployment})`);
  }

  private async connect(): Promise<void> {
    const authHeaders = await tokenManager.getAuthHeaders();
    const url = `wss://${this.config.openAiHost}/openai/v1/realtime?model=${encodeURIComponent(this.deployment)}`;

    const ws = await new Promise<WebSocket>((resolve, reject) => {
      const sock = new WebSocket(url, {
        headers: {
          ...authHeaders,
          'x-ms-client-request-id': crypto.randomUUID(),
        },
        handshakeTimeout: 10_000,
      });

      const onInit = (raw: WebSocket.RawData) => {
        try {
          const evt = JSON.parse(raw.toString());
          if (evt.type === 'session.created') {
            sock.off('message', onInit);
            resolve(sock);
          } else if (evt.type === 'error') {
            sock.off('message', onInit);
            reject(new Error(`GPT-Realtime init error: ${evt.error?.message || JSON.stringify(evt)}`));
          }
        } catch (err) {
          sock.off('message', onInit);
          reject(err);
        }
      };

      sock.on('message', onInit);
      sock.once('error', reject);
      sock.once('unexpected-response', (_req, res) => {
        let body = '';
        res.on('data', (c) => (body += c));
        res.on('end', () => reject(new Error(`GPT-Realtime HTTP ${res.statusCode}: ${body}`)));
      });
    });

    const isRealtime2 = this.deployment.includes('2');
    const transcriptionCfg: Record<string, unknown> = {
      model: 'gpt-4o-mini-transcribe',
      prompt:
        this.role === 'coach'
          ? 'DOTA2 coaching: Roshan, Aegis, Tormentor, Wisdom Rune, BKB, Manta, Blink Dagger, Gleipnir, Nullifier, Scythe, deward, stack ancients, Largo, Croak of Genius, Kez.'
          : 'DOTA2语音教学：肉山、不朽盾、痛苦魔方、智慧神符、BKB、跳刀、分身斧、羊刀、紫怨、否决、堆野、屯远古、反眼、高台眼、猴子、大圣、黑鸟、小牛、大牛、小鱼、朗戈、灵呱一闪。',
    };
    if (this.role === 'coach') {
      transcriptionCfg.language = 'en';
    }

    const sessionPayload: Record<string, unknown> = {
      type: 'realtime',
      output_modalities: ['audio'],
      instructions: this.systemPrompt,
      audio: {
        input: {
          format: { type: 'audio/pcm', rate: 24000 },
          noise_reduction: { type: 'near_field' },
          transcription: transcriptionCfg,
          turn_detection: {
            type: 'server_vad',
            threshold: 0.5,
            prefix_padding_ms: 240,
            silence_duration_ms: 580,
            create_response: true,
            interrupt_response: false,
          },
        },
        output: {
          format: { type: 'audio/pcm', rate: 24000 },
          voice: this.role === 'coach' ? 'marin' : 'cedar',
          speed: 1.05,
        },
      },
    };

    if (isRealtime2) {
      sessionPayload.reasoning = { effort: 'minimal' };
    }

    const updatedPromise = new Promise<void>((resolve) => {
      const timer = setTimeout(() => resolve(), 3000);
      const onUpd = (raw: WebSocket.RawData) => {
        try {
          const evt = JSON.parse(raw.toString());
          if (evt.type === 'session.updated') {
            clearTimeout(timer);
            ws.off('message', onUpd);
            resolve();
          }
        } catch {
          // ignore
        }
      };
      ws.on('message', onUpd);
    });

    ws.send(JSON.stringify({ type: 'session.update', session: sessionPayload }));
    await updatedPromise;

    ws.on('message', (raw: WebSocket.RawData) => {
      this.handleServerEvent(raw.toString());
    });

    ws.on('close', () => {
      if (!this.stopped) {
        this.callbacks.onStatus('GPT-Realtime reconnecting...');
        setTimeout(() => {
          if (!this.stopped) {
            this.connect().catch((err) =>
              this.callbacks.onError(`GPT-Realtime reconnect failed: ${err.message}`),
            );
          }
        }, 800);
      }
    });

    ws.on('error', (err) => {
      if (!this.stopped) {
        this.callbacks.onError(`GPT-Realtime WS error: ${err.message}`);
      }
    });

    this.ws = ws;
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
        this.latestInputTranscript = '';
        break;
      }

      case 'input_audio_buffer.speech_stopped': {
        this.speechStoppedAt = performance.now();
        break;
      }

      case 'input_audio_buffer.committed': {
        this.vadCommitted = true;
        this.uncommittedBytes = 0;
        break;
      }

      case 'conversation.item.added': {
        const itemId = evt.item?.id;
        if (itemId) {
          this.recentItemIds.push(itemId);
          // Keep context lean over long 45-min DOTA2 games (retain last 6 items = ~3 turns)
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

      case 'conversation.item.input_audio_transcription.completed': {
        const itemId = evt.item_id || 'default';
        this.inputItemDeltas.delete(itemId);
        const transcript = (evt.transcript || '').trim();
        if (transcript) {
          this.latestInputTranscript = transcript;
          if (this.activeResponse) {
            this.activeResponse.sourceTranscript = transcript;
          }
          this.callbacks.onSourceFinal(this.activeResponse?.turnId || itemId, transcript);
        }
        break;
      }

      case 'response.created': {
        const turnId = evt.response?.id || `rt_${crypto.randomUUID().slice(0, 8)}`;
        const speechEndTs = this.speechStoppedAt ?? performance.now();
        this.speechStoppedAt = null;
        this.activeResponse = {
          turnId,
          speechEndTs,
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
            turnId: `rt_${crypto.randomUUID().slice(0, 8)}`,
            speechEndTs: this.speechStoppedAt ?? performance.now(),
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
      case 'response.output_text.delta': {
        if (this.activeResponse && evt.delta) {
          this.activeResponse.targetTranscript += evt.delta;
          this.callbacks.onTargetPartial(
            this.activeResponse.turnId,
            this.activeResponse.targetTranscript,
          );
        }
        break;
      }

      case 'response.done': {
        if (this.activeResponse) {
          const r = this.activeResponse;
          this.activeResponse = null;
          const totalTurnMs = Math.max(1, Math.round(performance.now() - r.speechEndTs));
          const target = r.targetTranscript.trim();
          const source = r.sourceTranscript.trim() || '(audio input)';
          if (target) {
            this.callbacks.onTargetFinal(r.turnId, target);
          }
          const isModelRepeat =
            this.role === 'student' && source !== '(audio input)' && isPureEnglishUtterance(source);
          if (r.outputBytes > 0 || target) {
            this.callbacks.onTurnMetrics({
              turnId: r.turnId,
              engine: 'gpt-realtime',
              role: this.role,
              sourceText: source,
              targetText: target,
              passThrough: isModelRepeat ? 'model-repeat' : 'none',
              speechEndToFirstAudioMs: r.firstAudioMs ?? totalTurnMs,
              totalTurnMs,
              outputAudioDurationMs: Math.round((r.outputBytes / 48000) * 1000),
              timestamp: Date.now(),
            });
          }
        }
        break;
      }

      case 'error': {
        const msg = evt.error?.message || JSON.stringify(evt);
        if (
          !msg.toLowerCase().includes('buffer too small') &&
          !msg.toLowerCase().includes('already has an active response')
        ) {
          this.callbacks.onError(`GPT-Realtime: ${msg}`);
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
    this.systemPrompt = buildInterpreterSystemPrompt(this.direction, { matchHeroes: heroes });
    if (this.ws && this.ws.readyState === WebSocket.OPEN) {
      this.ws.send(
        JSON.stringify({
          type: 'session.update',
          session: {
            type: 'realtime',
            instructions: this.systemPrompt,
          },
        }),
      );
    }
  }

  async translateText(text: string): Promise<void> {
    const trimmed = text.trim();
    if (!trimmed || !this.ws || this.ws.readyState !== WebSocket.OPEN) return;
    const { normalizedText, userPrompt } = buildUserTurnWithMatchedGlossary(trimmed);
    this.latestInputTranscript = normalizedText;
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
