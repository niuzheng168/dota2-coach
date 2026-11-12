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
import { AzureTtsPoolClient } from '../tts-client.ts';
import type {
  EngineCallbacks,
  EngineOptions,
  ParticipantRole,
  TranslationEngine,
  TtsOptionsUpdate,
  TtsStreamMode,
} from './types.ts';

export const DEFAULT_TTS_ZH_VOICE = 'zh-CN-XiaoxiaoMultilingualNeural';
export const DEFAULT_TTS_EN_VOICE = 'en-US-Andrew:DragonHDLatestNeural';

const ZH_CLAUSE_SPLIT_RE = /^([\s\S]{4,}?[，。！？；,.!?;:])\s*([\s\S]*)$/;
const EN_CLAUSE_SPLIT_RE = /^([\s\S]{10,}?[，。！？；,.!?;:])\s*([\s\S]*)$/;
const MAX_TURN_AUDIO_BYTES = 24000 * 2 * 15; // 15s @ 24kHz PCM16

/**
 * @deprecated Superseded by unified {@link VoiceLiveEngine} (`mode = 'cascade'`) over
 * Azure Voice Live (`/voice-live/realtime?api-version=v1`).
 * Preserved intact for reference and local pure-WebSocket v2 text-streaming TTS (`Path:text.flush`
 * / `ReplaceFirstCommaFilter`) benchmarking.
 */
export class CascadeEngine implements TranslationEngine {
  readonly kind = 'cascade' as const;
  readonly role: ParticipantRole;
  private readonly direction: TranslationDirection;
  private systemPrompt: string;
  private readonly callbacks: EngineCallbacks;
  private readonly config: FoundryConfig;
  private readonly tts: AzureTtsPoolClient;
  private readonly englishOriginalVoiceRelay: boolean;
  private readonly llmDeployment: string;
  private ttsZhVoice: string;
  private ttsEnVoice: string;
  private ttsStreamMode: TtsStreamMode;

  private asrWs: WebSocket | null = null;
  private stopped = false;
  private uncommittedBytes = 0;
  private speechStoppedAt: number | null = null;

  // Built-in low-latency RMS VAD for continuous open-mic mode (MAI-Transcribe-2-Streaming uses turn_detection: null)
  private vadSpeechActive = false;
  private vadSilenceBytes = 0;
  private vadSpeechBytes = 0;

  // Per-item streaming transcript accumulator
  private itemDeltas = new Map<string, string>();
  private currentTurnPcm: Buffer[] = [];
  private currentTurnPcmBytes = 0;
  private lastTurnSnapshot: Buffer | null = null;

  constructor(
    options: EngineOptions,
    callbacks: EngineCallbacks,
    ttsPool?: AzureTtsPoolClient,
    config: FoundryConfig = DEFAULT_FOUNDRY_CONFIG,
  ) {
    this.role = options.role;
    this.direction = options.role === 'coach' ? 'coach-en-to-zh' : 'student-zh-or-en-to-en';
    this.systemPrompt = buildInterpreterSystemPrompt(this.direction, {
      matchHeroes: options.matchHeroes,
    });
    this.callbacks = callbacks;
    this.config = config;
    this.tts = ttsPool ?? new AzureTtsPoolClient(config);
    this.englishOriginalVoiceRelay = options.englishOriginalVoiceRelay ?? true;
    this.llmDeployment = options.cascadeLlmDeployment || config.deployments.cascadeLlm;
    this.ttsZhVoice = options.ttsZhVoice || process.env.AZURE_TTS_ZH_VOICE || DEFAULT_TTS_ZH_VOICE;
    this.ttsEnVoice = options.ttsEnVoice || process.env.AZURE_TTS_EN_VOICE || DEFAULT_TTS_EN_VOICE;
    this.ttsStreamMode = options.ttsStreamMode || 'v2-text-flush';
  }

  async start(): Promise<void> {
    this.stopped = false;
    await Promise.all([
      this.connectAsr(),
      this.tts.warmup(true),
      this.warmLlmSocket(),
    ]);
    const activeVoice = this.role === 'coach' ? this.ttsZhVoice : this.ttsEnVoice;
    this.callbacks.onStatus(
      `Cascade ready (${this.config.deployments.cascadeAsrStreaming} -> ${this.llmDeployment} -> ${activeVoice} [${this.ttsStreamMode}])`,
    );
  }

  private async warmLlmSocket(): Promise<void> {
    try {
      const authHeaders = await tokenManager.getAuthHeaders();
      // Lightweight HEAD/OPTIONS pre-warm to establish undici keep-alive TLS session to openAiHost
      const res = await fetch(`https://${this.config.openAiHost}/openai/v1/models`, {
        method: 'GET',
        headers: authHeaders,
      });
      await res.arrayBuffer().catch(() => {});
    } catch {
      // non-fatal prewarm
    }
  }

  private async connectAsr(): Promise<void> {
    const authHeaders = await tokenManager.getAuthHeaders();
    const url = `wss://${this.config.openAiHost}/openai/v1/realtime?intent=transcription`;

    const ws = await new Promise<WebSocket>((resolve, reject) => {
      const sock = new WebSocket(url, {
        headers: {
          ...authHeaders,
          'x-ms-mai-force-transcribe': 'true',
          'x-ms-client-request-id': crypto.randomUUID(),
        },
        handshakeTimeout: 10_000,
      });

      const onInitialMessage = (raw: WebSocket.RawData) => {
        try {
          const evt = JSON.parse(raw.toString());
          if (evt.type === 'session.created') {
            sock.off('message', onInitialMessage);
            resolve(sock);
          } else if (evt.type === 'error') {
            sock.off('message', onInitialMessage);
            reject(new Error(`MAI ASR error: ${evt.error?.message || JSON.stringify(evt)}`));
          }
        } catch (err) {
          sock.off('message', onInitialMessage);
          reject(err);
        }
      };

      sock.on('message', onInitialMessage);
      sock.once('error', reject);
      sock.once('unexpected-response', (_req, res) => {
        let body = '';
        res.on('data', (c) => (body += c));
        res.on('end', () => reject(new Error(`MAI ASR HTTP ${res.statusCode}: ${body}`)));
      });
    });

    const transcriptionCfg: Record<string, unknown> = {
      model: this.config.deployments.cascadeAsrStreaming,
    };
    if (this.role === 'coach') {
      transcriptionCfg.language = 'en';
    }

    // MAI-Transcribe-2-Streaming requires turn_detection: null; we drive commits via local RMS VAD + PTT!
    const sessionUpdated = new Promise<void>((resolve, reject) => {
      const timer = setTimeout(() => resolve(), 3000);
      const onUpdate = (raw: WebSocket.RawData) => {
        try {
          const evt = JSON.parse(raw.toString());
          if (evt.type === 'session.updated') {
            clearTimeout(timer);
            ws.off('message', onUpdate);
            resolve();
          } else if (evt.type === 'error') {
            clearTimeout(timer);
            ws.off('message', onUpdate);
            reject(new Error(`MAI session.update error: ${evt.error?.message || JSON.stringify(evt)}`));
          }
        } catch {
          // ignore
        }
      };
      ws.on('message', onUpdate);
    });

    ws.send(
      JSON.stringify({
        type: 'session.update',
        session: {
          type: 'transcription',
          audio: {
            input: {
              format: { type: 'audio/pcm', rate: 24000 },
              transcription: transcriptionCfg,
              turn_detection: null,
            },
          },
        },
      }),
    );

    await sessionUpdated;

    ws.on('message', (raw: WebSocket.RawData) => {
      this.handleAsrEvent(raw.toString());
    });

    ws.on('close', () => {
      if (!this.stopped) {
        this.callbacks.onStatus('MAI ASR reconnecting...');
        setTimeout(() => {
          if (!this.stopped) {
            this.connectAsr().catch((err) => this.callbacks.onError(`MAI reconnect failed: ${err.message}`));
          }
        }, 800);
      }
    });

    ws.on('error', (err) => {
      if (!this.stopped) {
        this.callbacks.onError(`MAI ASR WS error: ${err.message}`);
      }
    });

    this.asrWs = ws;
  }

  private handleAsrEvent(jsonStr: string): void {
    let evt: any;
    try {
      evt = JSON.parse(jsonStr);
    } catch {
      return;
    }

    switch (evt.type) {
      case 'input_audio_buffer.committed': {
        this.uncommittedBytes = 0;
        break;
      }

      case 'conversation.item.input_audio_transcription.delta': {
        const itemId = evt.item_id || 'default';
        const prev = this.itemDeltas.get(itemId) || '';
        const next = prev + (evt.delta || '');
        this.itemDeltas.set(itemId, next);
        this.callbacks.onSourcePartial(next);
        break;
      }

      case 'conversation.item.input_audio_transcription.intermediate': {
        const itemId = evt.item_id || 'default';
        const prev = this.itemDeltas.get(itemId) || '';
        const preview = prev + (evt.transcript || '');
        if (preview.trim()) {
          this.callbacks.onSourcePartial(preview);
        }
        break;
      }

      case 'conversation.item.input_audio_transcription.completed': {
        const itemId = evt.item_id || crypto.randomUUID();
        this.itemDeltas.delete(itemId);
        const transcript = (evt.transcript || '').trim();
        if (!transcript) return;

        const speechEndTs = this.speechStoppedAt ?? performance.now();
        this.speechStoppedAt = null;
        const asrMs = Math.max(1, Math.round(performance.now() - speechEndTs));
        const audioSnapshot = this.lastTurnSnapshot;
        this.lastTurnSnapshot = null;

        this.processCompletedTranscript(itemId, transcript, speechEndTs, asrMs, audioSnapshot).catch(
          (err) => this.callbacks.onError(`Cascade turn error: ${err instanceof Error ? err.message : String(err)}`),
        );
        break;
      }

      case 'error': {
        const msg = evt.error?.message || JSON.stringify(evt);
        if (!msg.toLowerCase().includes('buffer too small') && !msg.toLowerCase().includes('empty')) {
          this.callbacks.onError(`MAI ASR: ${msg}`);
        }
        break;
      }
    }
  }

  private computeFrameRms(pcm: Buffer): number {
    const samples = Math.floor(pcm.length / 2);
    if (samples === 0) return 0;
    let sumSq = 0;
    for (let i = 0; i < samples; i++) {
      const s = pcm.readInt16LE(i * 2);
      sumSq += s * s;
    }
    return Math.sqrt(sumSq / samples);
  }

  private commitCurrentTurn(): void {
    // 4800 bytes = 100ms @ 24kHz PCM16 (minimum commit requirement on MAI streaming)
    if (this.asrWs && this.asrWs.readyState === WebSocket.OPEN && this.uncommittedBytes >= 4800) {
      if (this.speechStoppedAt === null) {
        this.speechStoppedAt = performance.now();
      }
      if (this.currentTurnPcmBytes > 0) {
        this.lastTurnSnapshot = Buffer.concat(this.currentTurnPcm, this.currentTurnPcmBytes);
        this.currentTurnPcm = [];
        this.currentTurnPcmBytes = 0;
      }
      this.asrWs.send(JSON.stringify({ type: 'input_audio_buffer.commit' }));
      this.uncommittedBytes = 0;
      this.vadSpeechActive = false;
      this.vadSilenceBytes = 0;
      this.vadSpeechBytes = 0;
    }
  }

  appendAudio(pcm24kMono: Buffer): void {
    if (this.stopped || !pcm24kMono.length) return;
    const rms = this.computeFrameRms(pcm24kMono);
    const isSpeech = rms > 320;

    if (isSpeech) {
      this.vadSpeechActive = true;
      this.vadSilenceBytes = 0;
      this.vadSpeechBytes += pcm24kMono.length;
    } else if (this.vadSpeechActive) {
      this.vadSilenceBytes += pcm24kMono.length;
    }

    // Only stream audio while speaking or within pre-roll/tail window so idle room silence isn't billed
    if (this.asrWs && this.asrWs.readyState === WebSocket.OPEN && (isSpeech || this.vadSpeechActive)) {
      this.asrWs.send(
        JSON.stringify({
          type: 'input_audio_buffer.append',
          audio: pcm24kMono.toString('base64'),
        }),
      );
      this.uncommittedBytes += pcm24kMono.length;

      if (this.role === 'student' && this.englishOriginalVoiceRelay) {
        this.currentTurnPcm.push(pcm24kMono);
        this.currentTurnPcmBytes += pcm24kMono.length;
        while (this.currentTurnPcmBytes > MAX_TURN_AUDIO_BYTES && this.currentTurnPcm.length > 1) {
          const dropped = this.currentTurnPcm.shift()!;
          this.currentTurnPcmBytes -= dropped.length;
        }
      }
    }

    // Auto-commit in open-mic mode after 580ms of trailing silence (27,840 bytes @ 24kHz PCM16)
    // provided at least 180ms of actual voiced speech was observed.
    if (this.vadSpeechActive && this.vadSpeechBytes >= 8640 && this.vadSilenceBytes >= 27840) {
      this.commitCurrentTurn();
    }
  }

  markSpeechEnd(): void {
    this.commitCurrentTurn();
  }

  async updateMatchHeroes(heroes: string[]): Promise<void> {
    this.systemPrompt = buildInterpreterSystemPrompt(this.direction, { matchHeroes: heroes });
  }

  async updateTtsOptions(opts: TtsOptionsUpdate): Promise<void> {
    if (opts.ttsZhVoice) this.ttsZhVoice = opts.ttsZhVoice;
    if (opts.ttsEnVoice) this.ttsEnVoice = opts.ttsEnVoice;
    if (opts.ttsStreamMode) this.ttsStreamMode = opts.ttsStreamMode;

    if (this.ttsStreamMode !== 'v1-clause') {
      await this.tts.warmup(true).catch(() => {});
    }
    const activeVoice = this.role === 'coach' ? this.ttsZhVoice : this.ttsEnVoice;
    this.callbacks.onStatus(
      `Cascade TTS updated -> ${activeVoice} [${this.ttsStreamMode}] (0ms ASR drop)`,
    );
  }

  async translateText(text: string): Promise<void> {
    const trimmed = text.trim();
    if (!trimmed) return;
    const turnId = `txt_${crypto.randomUUID().slice(0, 8)}`;
    await this.processCompletedTranscript(turnId, trimmed, performance.now(), 0, null);
  }

  private trimSilencePcm16(pcm: Buffer, threshold = 350): Buffer {
    let startSample = 0;
    const totalSamples = Math.floor(pcm.length / 2);
    while (startSample < totalSamples && Math.abs(pcm.readInt16LE(startSample * 2)) < threshold) {
      startSample++;
    }
    let endSample = totalSamples - 1;
    while (endSample > startSample && Math.abs(pcm.readInt16LE(endSample * 2)) < threshold) {
      endSample--;
    }
    // Keep 80ms padding around speech
    const padSamples = 24 * 80;
    const from = Math.max(0, startSample - padSamples) * 2;
    const to = Math.min(totalSamples, endSample + padSamples) * 2;
    return to > from ? pcm.subarray(from, to) : pcm;
  }

  private async processCompletedTranscript(
    turnId: string,
    rawSourceText: string,
    speechEndTs: number,
    asrMs: number,
    audioSnapshot: Buffer | null,
  ): Promise<void> {
    const { normalizedText: sourceText, userPrompt } = buildUserTurnWithMatchedGlossary(rawSourceText);
    this.callbacks.onSourceFinal(turnId, sourceText);

    // FAST PATH: Student spoke pure English -> pass through without LLM distortion!
    if (this.role === 'student' && isPureEnglishUtterance(sourceText)) {
      this.callbacks.onTargetPartial(turnId, sourceText);
      this.callbacks.onTargetFinal(turnId, sourceText);

      if (this.englishOriginalVoiceRelay && audioSnapshot && audioSnapshot.length >= 4800) {
        const trimmedVoice = this.trimSilencePcm16(audioSnapshot);
        const firstAudioMs = Math.max(1, Math.round(performance.now() - speechEndTs));
        // Stream in 100ms (4800-byte) chunks to listener
        for (let off = 0; off < trimmedVoice.length; off += 4800) {
          this.callbacks.onTargetAudio(turnId, trimmedVoice.subarray(off, Math.min(off + 4800, trimmedVoice.length)));
        }
        const totalTurnMs = Math.max(firstAudioMs, Math.round(performance.now() - speechEndTs));
        this.callbacks.onTurnMetrics({
          turnId,
          engine: 'cascade',
          role: this.role,
          sourceText,
          targetText: sourceText,
          passThrough: 'raw-voice',
          speechEndToFirstAudioMs: firstAudioMs,
          totalTurnMs,
          asrMs,
          llmFirstTokenMs: 0,
          llmTotalMs: 0,
          ttsFirstChunkMs: 0,
          outputAudioDurationMs: Math.round((trimmedVoice.length / 48000) * 1000),
          timestamp: Date.now(),
        });
        return;
      }

      // Otherwise synthesize verbatim English via warm Azure TTS (still 0ms LLM!)
      let firstAudioMs: number | null = null;
      const ttsRes = await this.tts.synthesize({
        text: sourceText,
        voice: this.ttsEnVoice,
        lang: 'en-US',
        onAudioChunk: (chunk) => {
          if (firstAudioMs === null) {
            firstAudioMs = Math.round(performance.now() - speechEndTs);
          }
          this.callbacks.onTargetAudio(turnId, chunk);
        },
      });
      const totalTurnMs = Math.round(performance.now() - speechEndTs);
      this.callbacks.onTurnMetrics({
        turnId,
        engine: 'cascade',
        role: this.role,
        sourceText,
        targetText: sourceText,
        passThrough: 'tts-verbatim',
        speechEndToFirstAudioMs: firstAudioMs ?? totalTurnMs,
        totalTurnMs,
        asrMs,
        llmFirstTokenMs: 0,
        llmTotalMs: 0,
        ttsFirstChunkMs: ttsRes.firstChunkMs,
        ttsVoice: this.ttsEnVoice,
        ttsStreamMode: 'v1-clause',
        outputAudioDurationMs: Math.round((ttsRes.totalBytes / 48000) * 1000),
        timestamp: Date.now(),
      });
      return;
    }

    // STANDARD PATH: Stream GPT translation -> pure WebSocket Azure TTS (v2 TextStream or v1 SSML)
    const voice = this.role === 'coach' ? this.ttsZhVoice : this.ttsEnVoice;
    const lang = this.role === 'coach' ? 'zh-CN' : 'en-US';
    const activeStreamMode = this.ttsStreamMode;
    const clauseSplitRe = lang === 'zh-CN' ? ZH_CLAUSE_SPLIT_RE : EN_CLAUSE_SPLIT_RE;
    const useTtsV2TextStream = activeStreamMode !== 'v1-clause';

    let llmFirstTokenMs: number | undefined;
    let firstAudioMs: number | null = null;
    let firstTtsChunkMs: number | undefined;
    let outputBytes = 0;
    let fullTarget = '';
    let clauseBuffer = '';

    const llmStart = performance.now();
    const authHeaders = await tokenManager.getAuthHeaders();

    // Pre-open v2 speech.config + synthesis.context concurrently while GPT-4.1 computes TTFT!
    const ttsV2Promise = useTtsV2TextStream
      ? this.tts.openTextStream({
          voice,
          lang,
          flushStrategy:
            activeStreamMode === 'v2-text-flush'
              ? 'first-clause-flush'
              : activeStreamMode === 'v2-comma-period'
                ? 'first-comma-to-period'
                : 'none',
          onAudioChunk: (chunk) => {
            if (firstAudioMs === null) {
              firstAudioMs = Math.round(performance.now() - speechEndTs);
            }
            outputBytes += chunk.length;
            this.callbacks.onTargetAudio(turnId, chunk);
          },
        })
      : Promise.resolve(null);

    const sendChat = (deployment: string) =>
      fetch(`https://${this.config.openAiHost}/openai/v1/chat/completions`, {
        method: 'POST',
        headers: {
          ...authHeaders,
          'Content-Type': 'application/json',
        },
        body: JSON.stringify({
          model: deployment,
          stream: true,
          temperature: 0.1,
          max_tokens: 200,
          messages: [
            { role: 'system', content: this.systemPrompt },
            { role: 'user', content: userPrompt },
          ],
        }),
      });

    let [chatRes, ttsV2Stream] = await Promise.all([sendChat(this.llmDeployment), ttsV2Promise]);
    if (chatRes.status === 429 && this.llmDeployment !== 'gpt-4.1-datazone-standard') {
      chatRes = await sendChat('gpt-4.1-datazone-standard');
    }

    if (!chatRes.ok || !chatRes.body) {
      ttsV2Stream?.abort();
      throw new Error(`GPT HTTP ${chatRes.status}: ${await chatRes.text()}`);
    }

    // Warm v1 fast path: fire synthesis in parallel as soon as each clause completes while
    // strictly preserving audio playback order across clauses (0ms inter-clause wait!)
    let playbackChain = Promise.resolve();
    const enqueueClauseTts = (clause: string) => {
      const clean = clause.trim();
      if (!clean) return;
      const chunkQueue: Buffer[] = [];
      let synthDone = false;
      let notifyChunk: (() => void) | null = null;

      const synthPromise = this.tts.synthesize({
        text: clean,
        voice,
        lang,
        onAudioChunk: (chunk) => {
          chunkQueue.push(chunk);
          if (notifyChunk) {
            const wake = notifyChunk;
            notifyChunk = null;
            wake();
          }
        },
      });

      playbackChain = playbackChain.then(async () => {
        synthPromise
          .then((res) => {
            if (firstTtsChunkMs === undefined) {
              firstTtsChunkMs = res.firstChunkMs;
            }
          })
          .finally(() => {
            synthDone = true;
            if (notifyChunk) {
              const wake = notifyChunk;
              notifyChunk = null;
              wake();
            }
          });

        while (!synthDone || chunkQueue.length > 0) {
          while (chunkQueue.length > 0) {
            const chunk = chunkQueue.shift()!;
            if (firstAudioMs === null) {
              firstAudioMs = Math.round(performance.now() - speechEndTs);
            }
            outputBytes += chunk.length;
            this.callbacks.onTargetAudio(turnId, chunk);
          }
          if (!synthDone) {
            await new Promise<void>((r) => {
              notifyChunk = r;
            });
          }
        }
        await synthPromise;
      });
    };

    const decoder = new TextDecoder();
    let sseBuffer = '';
    const reader = chatRes.body.getReader();

    while (true) {
      const { done, value } = await reader.read();
      if (done) break;
      sseBuffer += decoder.decode(value, { stream: true });
      const lines = sseBuffer.split('\n');
      sseBuffer = lines.pop() || '';

      for (const rawLine of lines) {
        const line = rawLine.trim();
        if (!line.startsWith('data:')) continue;
        const payload = line.slice(5).trim();
        if (!payload || payload === '[DONE]') continue;
        try {
          const parsed = JSON.parse(payload);
          const delta: string | undefined = parsed.choices?.[0]?.delta?.content;
          if (delta) {
            if (llmFirstTokenMs === undefined) {
              llmFirstTokenMs = Math.round(performance.now() - llmStart);
            }
            fullTarget += delta;
            this.callbacks.onTargetPartial(turnId, fullTarget);

            if (ttsV2Stream) {
              ttsV2Stream.pushText(delta);
            } else {
              clauseBuffer += delta;
              const match = clauseSplitRe.exec(clauseBuffer);
              if (match) {
                enqueueClauseTts(match[1]);
                clauseBuffer = match[2];
              }
            }
          }
        } catch {
          // ignore partial JSON comment frames
        }
      }
    }

    const llmTotalMs = Math.round(performance.now() - llmStart);
    this.callbacks.onTargetFinal(turnId, fullTarget.trim());

    if (ttsV2Stream) {
      const res = await ttsV2Stream.endText();
      firstTtsChunkMs = res.firstChunkMs;
    } else {
      if (clauseBuffer.trim()) {
        enqueueClauseTts(clauseBuffer);
      }
      await playbackChain;
    }
    const totalTurnMs = Math.round(performance.now() - speechEndTs);

    this.callbacks.onTurnMetrics({
      turnId,
      engine: 'cascade',
      role: this.role,
      sourceText,
      targetText: fullTarget.trim(),
      passThrough: 'none',
      speechEndToFirstAudioMs: firstAudioMs ?? totalTurnMs,
      totalTurnMs,
      asrMs,
      llmFirstTokenMs,
      llmTotalMs,
      ttsFirstChunkMs: firstTtsChunkMs,
      ttsVoice: voice,
      ttsStreamMode: activeStreamMode,
      outputAudioDurationMs: Math.round((outputBytes / 48000) * 1000),
      timestamp: Date.now(),
    });
  }

  async stop(): Promise<void> {
    this.stopped = true;
    if (this.asrWs) {
      this.asrWs.close();
      this.asrWs = null;
    }
    this.tts.close();
  }
}
