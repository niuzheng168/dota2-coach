import fs from 'node:fs/promises';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { DEFAULT_FOUNDRY_CONFIG, tokenManager } from '../src/azure-auth.ts';
import {
  buildInterpreterSystemPrompt,
  buildUserTurnWithMatchedGlossary,
  isPureEnglishUtterance,
} from '../src/dota2-glossary.ts';
import type { TranslationDirection } from '../src/dota2-glossary.ts';
import { createTranslationEngine } from '../src/engines/index.ts';
import type { EngineKind, ParticipantRole, TurnMetrics } from '../src/engines/index.ts';
import { AzureTtsPoolClient } from '../src/tts-client.ts';

const __dirname = path.dirname(fileURLToPath(import.meta.url));
const ROOT_DIR = path.resolve(__dirname, '..');

interface DomainTextCase {
  id: string;
  direction: TranslationDirection;
  input: string;
  mustIncludeAnyGroups: string[][];
  mustNotInclude?: string[];
}

const TEXT_DOMAIN_SUITE: DomainTextCase[] = [
  {
    id: 'zh-pl-vs-mk-disambiguation',
    direction: 'student-zh-or-en-to-en',
    input: '猴子分身斧已经好了，但是对面大圣没大招，我们可以开雾抓一波。',
    mustIncludeAnyGroups: [
      ['Phantom Lancer', 'PL'],
      ['Manta'],
      ['Monkey King', 'MK'],
      ['smoke', 'Smoke'],
    ],
  },
  {
    id: 'zh-od-slark-bkb-save',
    direction: 'student-zh-or-en-to-en',
    input: '团战黑鸟先关一下小鱼，别让他开出BKB或者C解状态，大鱼给个点灯。',
    mustIncludeAnyGroups: [
      ['Outworld Destroyer', 'OD'],
      ['Astral'],
      ['Slark'],
      ['BKB'],
      ['Slardar'],
      ['Corrosive Haze', 'Amp', 'amplify', 'mark', 'ult'],
    ],
    mustNotInclude: ['Black Bird', 'blackbird'],
  },
  {
    id: 'zh-stack-deward-asr-homophones',
    direction: 'student-zh-or-en-to-en',
    input: '五号位去帮我堆也顺便屯远骨，把三角区的高台真假眼反演一下。',
    mustIncludeAnyGroups: [
      ['stack'],
      ['ancient', 'Ancient'],
      ['triangle'],
      ['deward'],
    ],
  },
  {
    id: 'zh-new-heroes-largo-kez',
    direction: 'student-zh-or-en-to-en',
    input: '朗戈刚交了灵呱一闪和两栖狂想曲，等凯开完猛禽之舞我们直接打二代肉山。',
    mustIncludeAnyGroups: [
      ['Largo'],
      ['Croak of Genius'],
      ['Amphibian Rhapsody'],
      ['Kez'],
      ['Raptor Dance'],
      ['Rosh', 'Roshan'],
    ],
  },
  {
    id: 'zh-cows-and-cats',
    direction: 'student-zh-or-en-to-en',
    input: '小心白牛冲你，让小牛蹲后面反手沟壑，大牛踩住以后蓝猫直接滚后排。',
    mustIncludeAnyGroups: [
      ['Spirit Breaker', 'SB', 'Bara'],
      ['Earthshaker', 'ES', 'Shaker'],
      ['Fissure'],
      ['Elder Titan', 'ET'],
      ['Storm Spirit', 'Storm'],
    ],
  },
  {
    id: 'zh-nullifier-saves-question',
    direction: 'student-zh-or-en-to-en',
    input: '对面辅助全是绿杖微光和推推，这把我要不要不出大炮改出否决和血棘？',
    mustIncludeAnyGroups: [
      ['Ghost'],
      ['Glimmer'],
      ['Force'],
      ['Daedalus'],
      ['Nullifier'],
      ['Bloodthorn'],
      ['?'],
    ],
  },
  {
    id: 'zh-kns-vs-ynk',
    direction: 'student-zh-or-en-to-en',
    input: '老鹿这把出散慧配永世法衣，紫猫出慧夜配林肯。',
    mustIncludeAnyGroups: [
      ['Leshrac', 'Lesh'],
      ['Kaya and Sange', 'KnS', 'K&S', 'Sange and Kaya'],
      ['Eternal Shroud', 'Shroud'],
      ['Void Spirit'],
      ['Yasha and Kaya', 'YnK', 'Y&K', 'Kaya and Yasha'],
      ['Linken'],
    ],
  },
  {
    id: 'student-english-passthrough',
    direction: 'student-zh-or-en-to-en',
    input: 'I no TP right now, wait my BKB 10 seconds then we go Rosh.',
    mustIncludeAnyGroups: [['no TP'], ['BKB'], ['Rosh']],
  },
  {
    id: 'en-coach-laning-macro',
    direction: 'coach-en-to-zh',
    input: 'Pull aggro onto your ranged creep, half-pull the small camp, and contest the 7-minute Wisdom Rune and 20-minute Tormentor.',
    mustIncludeAnyGroups: [
      ['仇恨'],
      ['远程兵'],
      ['半拉野', '半拉'],
      ['智慧神符', '经验符'],
      ['魔方'],
    ],
    mustNotInclude: ['古树'],
  },
  {
    id: 'en-coach-pl-mk-od-teamfight',
    direction: 'coach-en-to-zh',
    input: 'Kite Monkey King’s Wukong’s Command, save OD’s Astral for Slark, and don’t force high ground until Phantom Lancer finishes Manta and Heart.',
    mustIncludeAnyGroups: [
      ['大圣'],
      ['猴子猴孙', '阵', '大招'],
      ['黑鸟', 'OD'],
      ['关', '星体禁锢'],
      ['小鱼', '斯拉克'],
      ['高地', '上高'],
      ['猴子', '幻影长矛手'],
      ['分身'],
      ['龙心', '恐鳌之心'],
    ],
  },
  {
    id: 'en-coach-largo-roshan-buyback',
    direction: 'coach-en-to-zh',
    input: 'Largo used Croak of Genius and they have no buyback on Shadow Demon, deward their triangle cliff and take Roshan Banner and Aegis.',
    mustIncludeAnyGroups: [
      ['朗戈'],
      ['灵呱一闪'],
      ['没买活'],
      ['毒狗', '暗影恶魔'],
      ['三角区'],
      ['反眼', '排眼', '眼反了', '拆'],
      ['战旗', '肉山旗', '旗子'],
      ['盾', '不朽'],
    ],
  },
  {
    id: 'en-coach-question-discipline',
    direction: 'coach-en-to-zh',
    input: 'Do you have enough gold for Buyback if we take Twin Gate and smoke gank their Medusa in the dead lane?',
    mustIncludeAnyGroups: [
      ['买活'],
      ['传送门', '双生门'],
      ['雾'],
      ['美杜莎', '一姐', '大娜迦'],
      ['吗', '？', '?'],
    ],
  },
];

async function runTextCall(
  systemPrompt: string,
  userContent: string,
): Promise<{ output: string; ms: number; cachedTokens: number; promptTokens: number }> {
  const headers = await tokenManager.getAuthHeaders();
  const t0 = performance.now();
  const callModel = (deployment: string) =>
    fetch(`https://${DEFAULT_FOUNDRY_CONFIG.openAiHost}/openai/v1/chat/completions`, {
      method: 'POST',
      headers: { ...headers, 'Content-Type': 'application/json' },
      body: JSON.stringify({
        model: deployment,
        temperature: 0.1,
        max_tokens: 180,
        messages: [
          { role: 'system', content: systemPrompt },
          { role: 'user', content: userContent },
        ],
      }),
    });

  let res = await callModel(DEFAULT_FOUNDRY_CONFIG.deployments.cascadeLlm);
  if (res.status === 429) {
    res = await callModel('gpt-4.1-datazone-standard');
  }
  if (!res.ok) {
    throw new Error(`HTTP ${res.status}: ${await res.text()}`);
  }
  const json: any = await res.json();
  const ms = Math.round(performance.now() - t0);
  const output = (json.choices?.[0]?.message?.content || '').trim();
  const cachedTokens = json.usage?.prompt_tokens_details?.cached_tokens ?? 0;
  const promptTokens = json.usage?.prompt_tokens ?? 0;
  return { output, ms, cachedTokens, promptTokens };
}

function gradeOutput(tc: DomainTextCase, output: string): { passed: boolean; misses: string[] } {
  const lower = output.toLowerCase();
  const misses: string[] = [];
  for (const group of tc.mustIncludeAnyGroups) {
    const ok = group.some((kw) => lower.includes(kw.toLowerCase()));
    if (!ok) misses.push(`Missing one of [${group.join(' | ')}]`);
  }
  for (const forbidden of tc.mustNotInclude || []) {
    if (lower.includes(forbidden.toLowerCase())) {
      misses.push(`Forbidden literal [${forbidden}]`);
    }
  }
  return { passed: misses.length === 0, misses };
}

async function runAudioEngineCheck(
  kind: EngineKind,
  role: ParticipantRole,
  pcm: Buffer,
  mustContainAny: string[][],
): Promise<{
  engine: EngineKind;
  role: ParticipantRole;
  sourceText: string;
  targetText: string;
  ttfaMs: number;
  passed: boolean;
  misses: string[];
}> {
  const turns: TurnMetrics[] = [];
  let speechFullySent = false;
  let settleTimer: NodeJS.Timeout | null = null;
  let resolveDone!: () => void;
  const donePromise = new Promise<void>((r) => {
    resolveDone = r;
  });

  const scheduleSettle = () => {
    if (!speechFullySent) return;
    if (settleTimer) clearTimeout(settleTimer);
    settleTimer = setTimeout(() => resolveDone(), 1400);
  };

  const engine = createTranslationEngine(
    kind,
    {
      role,
      englishOriginalVoiceRelay: true,
      matchHeroes: ['Phantom Lancer', 'Monkey King', 'Outworld Destroyer', 'Slark', 'Largo'],
    },
    {
      onSourcePartial: () => {},
      onSourceFinal: () => {},
      onTargetPartial: () => {},
      onTargetFinal: () => {},
      onTargetAudio: () => {},
      onTurnMetrics: (m) => {
        turns.push(m);
        scheduleSettle();
      },
      onStatus: () => {},
      onError: (err) => console.warn(`  [${kind}] warn: ${err}`),
    },
  );

  await engine.start();
  await new Promise((r) => setTimeout(r, 250));

  const frameBytes = 2880; // 60ms @ 24kHz
  for (let off = 0; off < pcm.length; off += frameBytes) {
    engine.appendAudio(pcm.subarray(off, Math.min(off + frameBytes, pcm.length)));
    await new Promise((r) => setTimeout(r, 60));
  }
  speechFullySent = true;
  engine.markSpeechEnd();
  if (turns.length > 0) scheduleSettle();

  await Promise.race([donePromise, new Promise<void>((r) => setTimeout(r, 14_000))]);
  if (settleTimer) clearTimeout(settleTimer);
  await engine.stop();

  if (turns.length === 0) {
    return {
      engine: kind,
      role,
      sourceText: '',
      targetText: '(timeout)',
      ttfaMs: -1,
      passed: false,
      misses: ['Timed out waiting for turn metrics'],
    };
  }

  const combinedSource = turns.map((t) => t.sourceText).join(' ');
  const combinedTarget = turns.map((t) => t.targetText).join(' ');
  const lastTurn = turns[turns.length - 1];
  const lower = combinedTarget.toLowerCase();
  const misses: string[] = [];
  for (const grp of mustContainAny) {
    if (!grp.some((k) => lower.includes(k.toLowerCase()))) {
      misses.push(`Missing [${grp.join(' | ')}]`);
    }
  }
  return {
    engine: kind,
    role,
    sourceText: combinedSource,
    targetText: combinedTarget,
    ttfaMs: lastTurn.speechEndToFirstAudioMs,
    passed: misses.length === 0,
    misses,
  };
}

async function main() {
  console.log('=== PART 1: Baseline (Generic Prompt) vs Domain-Tuned DOTA2 Dictionary Evaluation ===\n');

  const promptCoachTuned = buildInterpreterSystemPrompt('coach-en-to-zh');
  const promptStudentTuned = buildInterpreterSystemPrompt('student-zh-or-en-to-en');

  const promptCoachBaseline =
    'You are a live interpreter. Translate the English speaker into Simplified Chinese only. Do not answer questions.';
  const promptStudentBaseline =
    'You are a live interpreter. If the speaker speaks English, repeat it in English; if Chinese, translate into English only. Do not answer questions.';

  // Pre-warm prompt cache once for each direction
  await runTextCall(promptStudentTuned, '测试肉山');
  await runTextCall(promptCoachTuned, 'Check Roshan');

  let baselinePassed = 0;
  let tunedPassed = 0;
  const detailedTextResults = [];

  for (const tc of TEXT_DOMAIN_SUITE) {
    const baseSys =
      tc.direction === 'coach-en-to-zh' ? promptCoachBaseline : promptStudentBaseline;
    const tunedSys = tc.direction === 'coach-en-to-zh' ? promptCoachTuned : promptStudentTuned;

    const baseRes = await runTextCall(baseSys, tc.input);
    const baseGrade = gradeOutput(tc, baseRes.output);
    if (baseGrade.passed) baselinePassed++;

    let tunedOutput = '';
    let tunedMs = 0;
    let cachedTokens = 0;
    let promptTokens = 0;

    if (tc.direction === 'student-zh-or-en-to-en' && isPureEnglishUtterance(tc.input)) {
      tunedOutput = tc.input;
      tunedMs = 0;
    } else {
      const { userPrompt } = buildUserTurnWithMatchedGlossary(tc.input);
      const tunedRes = await runTextCall(tunedSys, userPrompt);
      tunedOutput = tunedRes.output;
      tunedMs = tunedRes.ms;
      cachedTokens = tunedRes.cachedTokens;
      promptTokens = tunedRes.promptTokens;
    }
    const tunedGrade = gradeOutput(tc, tunedOutput);
    if (tunedGrade.passed) tunedPassed++;

    console.log(`[${tc.id}]`);
    console.log(`  Input   : ${tc.input}`);
    console.log(
      `  Baseline: ${baseGrade.passed ? 'PASS' : 'FAIL'} (${baseRes.ms}ms) -> ${baseRes.output}${baseGrade.misses.length ? `  !! ${baseGrade.misses.join('; ')}` : ''}`,
    );
    console.log(
      `  Tuned   : ${tunedGrade.passed ? 'PASS' : 'FAIL'} (${tunedMs}ms, cached ${cachedTokens}/${promptTokens} tok) -> ${tunedOutput}${tunedGrade.misses.length ? `  !! ${tunedGrade.misses.join('; ')}` : ''}\n`,
    );

    detailedTextResults.push({
      id: tc.id,
      input: tc.input,
      baseline: { ...baseRes, ...baseGrade },
      tuned: { output: tunedOutput, ms: tunedMs, cachedTokens, promptTokens, ...tunedGrade },
    });
  }

  console.log(
    `>>> Text Suite Accuracy: Baseline ${baselinePassed}/${TEXT_DOMAIN_SUITE.length} (${Math.round((baselinePassed / TEXT_DOMAIN_SUITE.length) * 100)}%)  -->  Domain-Tuned ${tunedPassed}/${TEXT_DOMAIN_SUITE.length} (${Math.round((tunedPassed / TEXT_DOMAIN_SUITE.length) * 100)}%)\n`,
  );

  console.log('=== PART 2: Live Voice Audio Domain Accuracy Across All 3 Architectures ===\n');
  const tts = new AzureTtsPoolClient();
  await tts.warmup();

  const synth = async (text: string, voice: string, lang: 'zh-CN' | 'en-US') => {
    const chunks: Buffer[] = [];
    await tts.synthesize({ text, voice, lang, onAudioChunk: (c) => chunks.push(c) });
    return Buffer.concat(chunks);
  };

  const zhHardClip = await synth(
    '猴子分身斧好了，大圣没大招，去把三角区高台眼反了顺便屯波远古。',
    'zh-CN-YunyiMultilingualNeural',
    'zh-CN',
  );
  const enHardClip = await synth(
    'Save Astral for Slark, kite Monkey King ult, and wait for Phantom Lancer Manta before Roshan.',
    'en-US-AndrewMultilingualNeural',
    'en-US',
  );
  tts.close();

  const engines: EngineKind[] = ['cascade', 'gpt-realtime', 'gpt-live'];
  const voiceResults = [];

  for (const kind of engines) {
    console.log(`Testing live voice domain translation on [${kind}]...`);
    const rZh = await runAudioEngineCheck(kind, 'student', zhHardClip, [
      ['Phantom Lancer', 'PL'],
      ['Manta'],
      ['Monkey King', 'MK'],
      ['deward', 'ward'],
      ['ancient', 'Ancient'],
    ]);
    console.log(
      `  [${kind} | ZH->EN] ${rZh.passed ? 'PASS' : 'FAIL'} (TTFA ${rZh.ttfaMs}ms) | ASR="${rZh.sourceText}" -> "${rZh.targetText}" ${rZh.misses.join('; ')}`,
    );
    voiceResults.push(rZh);

    const rEn = await runAudioEngineCheck(kind, 'coach', enHardClip, [
      ['关', '星体禁锢'],
      ['小鱼', '斯拉克'],
      ['大圣', '猴子猴孙'],
      ['猴子', '幻影长矛手'],
      ['分身'],
      ['肉山'],
    ]);
    console.log(
      `  [${kind} | EN->ZH] ${rEn.passed ? 'PASS' : 'FAIL'} (TTFA ${rEn.ttfaMs}ms) | ASR="${rEn.sourceText}" -> "${rEn.targetText}" ${rEn.misses.join('; ')}\n`,
    );
    voiceResults.push(rEn);
  }

  const reportPath = path.join(ROOT_DIR, 'benchmark-results', 'domain-accuracy.json');
  await fs.mkdir(path.dirname(reportPath), { recursive: true });
  await fs.writeFile(
    reportPath,
    JSON.stringify(
      {
        evaluatedAt: new Date().toISOString(),
        textSummary: {
          total: TEXT_DOMAIN_SUITE.length,
          baselinePassed,
          tunedPassed,
        },
        detailedTextResults,
        voiceResults,
      },
      null,
      2,
    ),
    'utf8',
  );
  console.log(`Saved domain accuracy report to ${reportPath}`);
}

main().catch((err) => {
  console.error(err);
  process.exit(1);
});
