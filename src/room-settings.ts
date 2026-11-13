import { AppError } from './access-store.ts';
import { isValidVoiceLiveAsrModel } from './engines/types.ts';
import type { EngineKind, VoiceLiveAsrModel } from './engines/types.ts';

export const TTS_VOICE_CATALOG = {
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

export interface RoomSettings {
  engineKind: EngineKind;
  ttsZhVoice: string;
  ttsEnVoice: string;
  voiceLiveAsrModel: VoiceLiveAsrModel;
}

export const DEFAULT_ROOM_SETTINGS: RoomSettings = {
  engineKind: 'cascade',
  ttsZhVoice: 'zh-CN-XiaoxiaoMultilingualNeural',
  ttsEnVoice: 'en-US-Andrew:DragonHDLatestNeural',
  voiceLiveAsrModel: 'azure-speech',
};

export function validateRoomSettings(value: unknown): RoomSettings {
  if (!value || typeof value !== 'object' || Array.isArray(value)) {
    throw new AppError(400, '请选择同传设置');
  }
  const settings = value as Record<string, unknown>;
  if (Object.keys(settings).some(key => !Object.hasOwn(DEFAULT_ROOM_SETTINGS, key)) ||
      !['cascade', 'gpt-realtime', 'gpt-live'].includes(settings.engineKind as string) ||
      !TTS_VOICE_CATALOG.zh.some(voice => voice.id === settings.ttsZhVoice) ||
      !TTS_VOICE_CATALOG.en.some(voice => voice.id === settings.ttsEnVoice) ||
      !isValidVoiceLiveAsrModel(settings.voiceLiveAsrModel)) {
    throw new AppError(400, '同传引擎、音色或 ASR 设置无效');
  }
  return {
    engineKind: settings.engineKind as EngineKind,
    ttsZhVoice: settings.ttsZhVoice as string,
    ttsEnVoice: settings.ttsEnVoice as string,
    voiceLiveAsrModel: settings.voiceLiveAsrModel,
  };
}
