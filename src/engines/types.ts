export type EngineKind = 'cascade' | 'gpt-realtime' | 'gpt-live';
export type ParticipantRole = 'coach' | 'student';
export type TtsStreamMode =
  | 'v2-text-flush'
  | 'v2-comma-period'
  | 'v2-text-raw'
  | 'v1-clause';

export type VoiceLiveAsrModel =
  | 'azure-speech'
  | 'mai-transcribe-2-streaming'
  | 'mai-transcribe-2';

export const VALID_TTS_STREAM_MODES: readonly TtsStreamMode[] = [
  'v2-text-flush',
  'v2-comma-period',
  'v2-text-raw',
  'v1-clause',
];

export const VALID_VOICE_LIVE_ASR_MODELS: readonly VoiceLiveAsrModel[] = [
  'azure-speech',
  'mai-transcribe-2-streaming',
  'mai-transcribe-2',
];

export function isValidTtsStreamMode(value: unknown): value is TtsStreamMode {
  return typeof value === 'string' && (VALID_TTS_STREAM_MODES as readonly string[]).includes(value);
}

export function isValidVoiceLiveAsrModel(value: unknown): value is VoiceLiveAsrModel {
  return typeof value === 'string' && (VALID_VOICE_LIVE_ASR_MODELS as readonly string[]).includes(value);
}

export interface TurnMetrics {
  turnId: string;
  engine: EngineKind;
  role: ParticipantRole;
  sourceText: string;
  targetText: string;
  passThrough: 'none' | 'raw-voice' | 'tts-verbatim' | 'model-repeat';
  speechEndToFirstAudioMs: number;
  totalTurnMs: number;
  asrMs?: number;
  llmFirstTokenMs?: number;
  llmTotalMs?: number;
  ttsFirstChunkMs?: number;
  ttsVoice?: string;
  ttsStreamMode?: TtsStreamMode;
  voiceLiveModel?: string;
  voiceLiveAsrModel?: VoiceLiveAsrModel;
  outputAudioDurationMs: number;
  timestamp: number;
}

export interface EngineCallbacks {
  onSourcePartial: (text: string) => void;
  onSourceFinal: (turnId: string, text: string) => void;
  onTargetPartial: (turnId: string, text: string) => void;
  onTargetFinal: (turnId: string, text: string) => void;
  onTargetAudio: (turnId: string, pcm24kMono: Buffer) => void;
  onTurnMetrics: (metrics: TurnMetrics) => void;
  onStatus: (status: string) => void;
  onError: (message: string) => void;
}

export interface TtsOptionsUpdate {
  ttsZhVoice?: string;
  ttsEnVoice?: string;
  ttsStreamMode?: TtsStreamMode;
  voiceLiveAsrModel?: VoiceLiveAsrModel;
}

export interface EngineOptions {
  role: ParticipantRole;
  englishOriginalVoiceRelay?: boolean;
  matchHeroes?: string[];
  ttsZhVoice?: string;
  ttsEnVoice?: string;
  ttsStreamMode?: TtsStreamMode;
  voiceLiveModel?: string;
  voiceLiveAsrModel?: VoiceLiveAsrModel;
  cascadeLlmDeployment?: string;
  realtimeDeployment?: string;
  liveDeployment?: string;
}

export interface TranslationEngine {
  readonly kind: EngineKind;
  readonly role: ParticipantRole;
  start(): Promise<void>;
  appendAudio(pcm24kMono: Buffer): void;
  /**
   * Signals that the speaker just finished a push-to-talk or test clip utterance.
   * Useful for deterministic latency measurement and immediate turn commit.
   */
  markSpeechEnd(): void;
  /**
   * Updates the active 1–10 match heroes to pin their exact abilities & nicknames in the prompt.
   */
  updateMatchHeroes?(heroes: string[]): Promise<void>;
  /**
   * Dynamically updates TTS output voice(s) and ASR/TTS options without dropping WebSocket sessions.
   */
  updateTtsOptions?(opts: TtsOptionsUpdate): Promise<void>;
  /**
   * Direct text injection fallback (e.g. quick chat / testing glossary without mic).
   */
  translateText?(text: string): Promise<void>;
  stop(): Promise<void>;
}
