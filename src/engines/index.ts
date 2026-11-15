import { CascadeEngine } from './cascade-engine.ts';
import { GptLiveEngine } from './gpt-live-engine.ts';
import { GptRealtimeEngine } from './gpt-realtime-engine.ts';
import { VoiceLiveEngine } from './voice-live-engine.ts';
import { ProcessListenEngine } from './process-listen-engine.ts';
import type {
  EngineCallbacks,
  EngineKind,
  EngineOptions,
  TranslationEngine,
} from './types.ts';

export * from './types.ts';
export {
  VoiceLiveEngine,
  GptLiveEngine,
  /** @deprecated Local self-built Cascaded engine — use `VoiceLiveEngine('cascade', ...)` */
  CascadeEngine,
  /** @deprecated Standalone OpenAI Realtime engine — use `VoiceLiveEngine('gpt-realtime', ...)` */
  GptRealtimeEngine,
};

export function createTranslationEngine(
  kind: EngineKind,
  options: EngineOptions,
  callbacks: EngineCallbacks,
): TranslationEngine {
  if (options.inputKind === 'process') return new ProcessListenEngine(options, callbacks);
  switch (kind) {
    case 'cascade':
      return new VoiceLiveEngine('cascade', options, callbacks);
    case 'gpt-realtime':
      return new VoiceLiveEngine('gpt-realtime', options, callbacks);
    case 'gpt-live':
      return new GptLiveEngine(options, callbacks);
    default: {
      const exhaustive: never = kind;
      throw new Error(`Unsupported engine kind: ${String(exhaustive)}`);
    }
  }
}
