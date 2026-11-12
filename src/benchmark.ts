import fs from 'node:fs/promises';
import path from 'node:path';
import { AzureTtsPoolClient } from './tts-client.ts';
import { createTranslationEngine } from './engines/index.ts';
import type { EngineKind, ParticipantRole, TurnMetrics } from './engines/index.ts';

interface TestUtterance {
  id: string;
  title: string;
  role: ParticipantRole;
  voice: string;
  lang: 'en-US' | 'zh-CN';
  spokenText: string;
}

const TEST_UTTERANCES: TestUtterance[] = [
  {
    id: 'coach_roshan_call',
    title: 'Coach (EN -> ZH): Laning + Smoke Roshan Callout',
    role: 'coach',
    voice: 'en-US-AndrewMultilingualNeural',
    lang: 'en-US',
    spokenText:
      'Pull the small camp and stack ancients, then pop smoke with your BKB and let us take Roshan.',
  },
  {
    id: 'student_zh_question',
    title: 'Student (ZH -> EN): Mixed Slang + Tactical Question',
    role: 'student',
    voice: 'zh-CN-YunyiMultilingualNeural',
    lang: 'zh-CN',
    spokenText: '我有BKB和跳刀了，等我十秒TP下路，我们先打魔方还是直接上高？',
  },
  {
    id: 'student_en_passthrough',
    title: 'Student (EN -> EN): Basic English Verbatim Pass-Through',
    role: 'student',
    voice: 'en-US-BrianMultilingualNeural',
    lang: 'en-US',
    spokenText: 'My BKB is on cooldown for fifteen seconds, do not fight yet, go back.',
  },
];

async function synthesizeClip(tts: AzureTtsPoolClient, u: TestUtterance): Promise<Buffer> {
  const chunks: Buffer[] = [];
  await tts.synthesize({
    text: u.spokenText,
    voice: u.voice,
    lang: u.lang,
    rate: '+0%',
    onAudioChunk: (c) => chunks.push(c),
  });
  return Buffer.concat(chunks);
}

function sleep(ms: number): Promise<void> {
  return new Promise((r) => setTimeout(r, ms));
}

async function runSingleTrial(
  engineKind: EngineKind,
  utterance: TestUtterance,
  pcm24k: Buffer,
): Promise<TurnMetrics> {
  return new Promise<TurnMetrics>(async (resolve, reject) => {
    let settled = false;
    const timeout = setTimeout(() => {
      if (!settled) {
        settled = true;
        engine.stop().catch(() => {});
        reject(new Error(`Timeout waiting for ${engineKind} on ${utterance.id}`));
      }
    }, 45_000);

    const engine = createTranslationEngine(
      engineKind,
      { role: utterance.role, englishOriginalVoiceRelay: true },
      {
        onSourcePartial: () => {},
        onSourceFinal: (_id, txt) => {
          console.log(`    [${engineKind}] ASR: ${txt}`);
        },
        onTargetPartial: () => {},
        onTargetFinal: (_id, txt) => {
          console.log(`    [${engineKind}] OUT: ${txt}`);
        },
        onTargetAudio: () => {},
        onTurnMetrics: (metrics) => {
          if (!settled) {
            settled = true;
            clearTimeout(timeout);
            engine.stop().finally(() => resolve(metrics));
          }
        },
        onStatus: () => {},
        onError: (err) => {
          console.warn(`    [${engineKind}] warn/err: ${err}`);
        },
      },
    );

    try {
      await engine.start();
      // Small settle window after session.updated / session.started
      await sleep(250);

      // Stream audio in real-time 60ms (2880-byte) microphone frames
      const frameBytes = 2880;
      for (let offset = 0; offset < pcm24k.length; offset += frameBytes) {
        const slice = pcm24k.subarray(offset, Math.min(offset + frameBytes, pcm24k.length));
        engine.appendAudio(slice);
        await sleep(60);
      }

      // Signal end of push-to-talk / utterance (for Cascade & Realtime deterministic commit;
      // GptLive continues streaming silence automatically via its internal 60ms pacer)
      engine.markSpeechEnd();
    } catch (err) {
      if (!settled) {
        settled = true;
        clearTimeout(timeout);
        await engine.stop().catch(() => {});
        reject(err);
      }
    }
  });
}

async function main() {
  console.log('=== Synthesizing DOTA2 Coaching Benchmark Audio Clips (24kHz PCM16) ===');
  const tts = new AzureTtsPoolClient();
  await tts.warmup();

  const clips = new Map<string, Buffer>();
  for (const u of TEST_UTTERANCES) {
    const pcm = await synthesizeClip(tts, u);
    clips.set(u.id, pcm);
    console.log(
      `  Prepared [${u.id}]: ${(pcm.length / 48000).toFixed(2)}s (${pcm.length} bytes) -> "${u.spokenText}"`,
    );
  }
  tts.close();

  const engines: EngineKind[] = ['cascade', 'gpt-realtime', 'gpt-live'];
  const allResults: Array<{ scenario: string; metrics: TurnMetrics }> = [];

  for (const engineKind of engines) {
    console.log(`\n=== Benchmarking Engine: ${engineKind.toUpperCase()} ===`);
    for (const u of TEST_UTTERANCES) {
      console.log(`  -> ${u.title}`);
      const pcm = clips.get(u.id)!;
      try {
        const metrics = await runSingleTrial(engineKind, u, pcm);
        allResults.push({ scenario: u.id, metrics });
        console.log(
          `    => TTFA: ${metrics.speechEndToFirstAudioMs}ms | Total: ${metrics.totalTurnMs}ms | PassThrough: ${metrics.passThrough}` +
            (metrics.asrMs !== undefined
              ? ` (ASR ${metrics.asrMs}ms, LLM-TTFT ${metrics.llmFirstTokenMs}ms, TTS-1st ${metrics.ttsFirstChunkMs}ms)`
              : ''),
        );
      } catch (err) {
        console.error(`    !! FAILED [${engineKind} / ${u.id}]:`, err);
      }
    }
  }

  const outDir = path.resolve(process.cwd(), 'benchmark-results');
  await fs.mkdir(outDir, { recursive: true });
  const outPath = path.join(outDir, 'latest.json');
  await fs.writeFile(
    outPath,
    JSON.stringify({ generatedAt: new Date().toISOString(), results: allResults }, null, 2),
  );
  console.log(`\nSaved benchmark results to ${outPath}`);
}

main().catch((err) => {
  console.error(err);
  process.exit(1);
});
