import assert from 'node:assert/strict';
import { test } from 'node:test';
import { ProcessListenEngine, listenerSession, parseEnglishTranslation, translateListenerText } from '../src/engines/process-listen-engine.ts';
import { DEFAULT_FOUNDRY_CONFIG, tokenManager } from '../src/azure-auth.ts';
import type { EngineCallbacks } from '../src/engines/types.ts';
// @ts-expect-error Browser-native JavaScript module is also exercised without a browser.
import { DesktopListener, OverlayCaptions, overlaySettings } from '../public/desktop-listener.js';

function harness() {
  const sent: any[] = [], output: any[] = [], errors: string[] = [];
  const callbacks: EngineCallbacks = {
    onSourcePartial(text) { output.push(['partial', text]); },
    onSourceFinal(id, text) { output.push(['source', id, text]); },
    onTargetPartial(id, text) { output.push(['target.partial', id, text]); },
    onTargetFinal(id, text) { output.push(['target', id, text]); },
    onTargetAudio() { assert.fail('A listener must never synthesize audio'); },
    onTurnMetrics() {}, onStatus() {}, onError(text) { errors.push(text); },
  };
  const engine = new ProcessListenEngine({ role: 'coach', inputKind: 'process' }, callbacks);
  const internals = engine as any;
  internals.ws = { readyState: 1, bufferedAmount: 0, send(raw: string) { sent.push(JSON.parse(raw)); }, terminate() {} };
  return { engine, internals, sent, output, errors, event: (event: unknown) => internals.handleEvent(JSON.stringify(event)) };
}
const wait = () => new Promise(resolve => setTimeout(resolve, 20));
const loud = (milliseconds: number) => {
  const buffer = Buffer.alloc(milliseconds * 48);
  for (let index = 0; index < buffer.length; index += 2) buffer.writeInt16LE(1600, index);
  return buffer;
};

test('desktop never starts native capture against an old or non-private listener backend', () => {
  for (const config of [
    null,
    { protocolVersion: 1, privacy: 'room', output: 'text', sampleRate: 24000, channels: 1 },
    { protocolVersion: 1, privacy: 'connection', output: 'text', sampleRate: 24000, channels: 1 },
  ]) {
    const elements = new Map<string, any>([
      ['processConsent', { checked: true }], ['processSelect', { value: '123' }],
      ['listenProcessBtn', {}], ['processListenStatus', {}],
    ]);
    const messages: any[] = [];
    const socket: any = { readyState: 1, close() {} };
    const listener = new DesktopListener({
      root: { getElementById: (id: string) => elements.get(id) },
      bridge: null, location: { protocol: 'https:', host: 'test.example' },
      room: () => ({ roomId: 'test-room' }), onSubtitle() {}, socketFactory: () => socket,
    });
    listener.available = true;
    listener.bridge = { postMessage: (message: any) => messages.push(message) };
    listener.processes = [{ pid: 123, name: 'dota2.exe', startedAt: '100' }];
    listener.start();
    if (config) socket.onmessage({ data: JSON.stringify({ type: 'listener.config', ...config }) });
    socket.onmessage?.({ data: JSON.stringify({ type: 'engine.state', state: 'ready' }) });
    assert.equal(messages.some(message => message.type === 'capture.start'), config?.privacy === 'connection');
    if (config?.privacy !== 'connection') assert.match(elements.get('processListenStatus').textContent, /阻止采集/);
    listener.stop();
  }
});

test('listener ASR autodetects languages, commits locally and never requests TTS', () => {
  for (const model of ['azure-speech', 'mai-transcribe-2', 'mai-transcribe-2-streaming']) {
    const session = listenerSession(model);
    assert.equal(session.type, 'transcription');
    assert.deepEqual(session.audio.input.transcription, { model });
    assert.equal(session.audio.input.turn_detection, null);
    assert.deepEqual(session.audio.input.format, { type: 'audio/pcm', rate: 24000 });
    assert.ok(!('output' in session.audio));
  }
  assert.equal(parseEnglishTranslation('{"english":false,"chinese":""}'), null);
  assert.equal(parseEnglishTranslation('{"english":"true","chinese":"不应通过"}'), null);
  assert.equal(parseEnglishTranslation('{"english":true,"chinese":" 打肉山。 "}'), '打肉山。');
  assert.throws(() => parseEnglishTranslation('{"english":true,"chinese":""}'));
  assert.throws(() => parseEnglishTranslation('not JSON'));
});

test('silence is not uploaded; arbitrary packets yield one bounded speech commit and exclude short noise', async () => {
  const h = harness();
  h.engine.appendAudio(Buffer.alloc(48_000));
  assert.equal(h.sent.length, 0);
  const speech = loud(300);
  h.engine.appendAudio(speech.subarray(0, 222));
  h.engine.appendAudio(speech.subarray(222));
  h.engine.appendAudio(Buffer.alloc(550 * 48));
  assert.equal(h.sent.filter(e => e.type === 'input_audio_buffer.commit').length, 1);
  const total = h.sent.filter(e => e.audio).reduce((sum, e) => sum + Buffer.from(e.audio, 'base64').length, 0);
  assert.equal(total, (200 + 300 + 550) * 48);
  h.engine.appendAudio(loud(50));
  h.engine.appendAudio(Buffer.alloc(550 * 48));
  assert.equal(h.sent.filter(e => e.type === 'input_audio_buffer.clear').length, 1);
  await h.engine.stop();
});

test('continuous game audio is segmented; overload fails closed rather than buffering minutes of audio', async () => {
  const h = harness();
  h.engine.appendAudio(loud(12_000));
  assert.equal(h.sent.filter(e => e.type === 'input_audio_buffer.commit').length, 2);
  assert.equal(h.errors.length, 1);
  assert.match(h.errors[0]!, /积压/);
  const count = h.sent.length;
  h.engine.appendAudio(loud(100));
  assert.equal(h.sent.length, count);
  await h.engine.stop();
});

test('language gate receives raw transcript separately from glossary and uses no audio generation', async t => {
  t.mock.method(tokenManager, 'getAuthHeaders', async () => ({ 'api-key': 'fake' }));
  let request: any;
  t.mock.method(globalThis, 'fetch', async (_url: unknown, options: RequestInit) => {
    request = JSON.parse(String(options.body));
    return Response.json({ choices: [{ message: { content: '{"english":false,"chinese":""}' } }] });
  });
  const config = { ...DEFAULT_FOUNDRY_CONFIG, deployments: { ...DEFAULT_FOUNDRY_CONFIG.deployments,
    listenerLlm: 'gpt-6-luna-listener-custom', cascadeLlm: 'legacy-not-deployed' } };
  assert.equal(await translateListenerText('我们打肉山', [], config, new AbortController().signal), null);
  assert.equal(request.model, 'gpt-6-luna-listener-custom');
  assert.equal(request.reasoning_effort, 'none');
  assert.equal(JSON.parse(request.messages[1].content).transcript, '我们打肉山');
  assert.equal(request.response_format.type, 'json_object');
  assert.ok(!('audio' in request));
});

test('non-English and unclassified partials never reach chat; accepted pairs preserve ASR commit order', async t => {
  t.mock.method(tokenManager, 'getAuthHeaders', async () => ({}));
  const translated: string[] = [];
  t.mock.method(globalThis, 'fetch', async (_url: unknown, options: RequestInit) => {
    const text = JSON.parse(JSON.parse(String(options.body)).messages[1].content).transcript;
    translated.push(text);
    return Response.json({ choices: [{ message: { content: JSON.stringify({
      english: text !== '我们打肉山', chinese: text === 'Go Roshan' ? '打肉山' : text === 'Back now' ? '撤退' : '',
    }) } }] });
  });
  const h = harness();
  h.internals.commitTimes = [100, 200];
  h.event({ type: 'conversation.item.input_audio_transcription.delta', item_id: 'a', delta: 'unclassified' });
  h.event({ type: 'input_audio_buffer.committed', item_id: 'a' });
  h.event({ type: 'input_audio_buffer.committed', item_id: 'b' });
  h.event({ type: 'conversation.item.input_audio_transcription.completed', item_id: 'b', transcript: 'Go Roshan' });
  assert.equal(h.output.length, 0);
  // A newer result must not reset the timeout for the missing oldest result.
  assert.deepEqual(h.internals.commitTimes, [100, 200]);
  h.event({ type: 'conversation.item.input_audio_transcription.completed', item_id: 'a', transcript: '我们打肉山' });
  await wait();
  assert.deepEqual(translated, ['我们打肉山', 'Go Roshan']);
  assert.equal(h.output.length, 2);
  assert.equal(h.output[0][2], 'Go Roshan');
  assert.equal(h.output[1][2], '打肉山');
  assert.equal(h.output[0][1], h.output[1][1]);
  h.event({ type: 'conversation.item.input_audio_transcription.completed', item_id: 'b', transcript: 'Go Roshan' });
  h.event({ type: 'input_audio_buffer.committed', item_id: 'b' });
  await wait();
  assert.equal(h.output.length, 2);
  assert.equal(h.internals.items.length, 0);
  await h.engine.stop();
});

test('stopping a listener aborts pending translation and prevents late subtitle callbacks', async t => {
  t.mock.method(tokenManager, 'getAuthHeaders', async () => ({}));
  let finish!: (response: Response) => void;
  t.mock.method(globalThis, 'fetch', async () => new Promise<Response>(resolve => { finish = resolve; }));
  const h = harness();
  h.event({ type: 'input_audio_buffer.committed', item_id: 'a' });
  h.event({ type: 'conversation.item.input_audio_transcription.completed', item_id: 'a', transcript: 'Go Roshan' });
  await wait();
  await h.engine.stop();
  finish(Response.json({ choices: [{ message: { content: '{"english":true,"chinese":"打肉山"}' } }] }));
  await wait();
  assert.deepEqual(h.output, []);
});

test('overlay settings clamp unsafe values and captions never mix speakers; caches are bounded', () => {
  const settings = overlaySettings({ sourceColor: '<script>', targetColor: '#123456', opacity: 99,
    backgroundOpacity: -1, fontSize: 900, top: 'bad', seconds: 0 });
  assert.equal(settings.sourceColor, '#ffffff');
  assert.equal(settings.targetColor, '#123456');
  assert.equal(settings.opacity, 1);
  assert.equal(settings.backgroundOpacity, 0);
  assert.equal(settings.fontSize, 48);
  assert.equal(settings.top, 5);
  assert.equal(settings.seconds, 3);
  assert.equal(overlaySettings(null).enabled, true);
  assert.equal(overlaySettings({ top: 100 }).top, 100);
  assert.equal(overlaySettings({ top: 900 }).top, 100);
  assert.equal(overlaySettings({ top: -1 }).top, 0);
  assert.equal(overlaySettings({ top: 60 }).top, 60); // Preserve existing saved preferences.
  const messages: any[] = [];
  const captions = new OverlayCaptions((message: any) => messages.push(message));
  captions.accept({ type: 'subtitle.source.partial', streamId: 'app', speaker: 'Coach', text: 'Go Roshan' });
  captions.accept({ type: 'subtitle.source.final', streamId: 'game', speaker: 'dota2.exe', turnId: 'g', text: 'Back' });
  captions.accept({ type: 'subtitle.target.partial', streamId: 'app', turnId: 'a', text: '打肉山' });
  assert.equal(messages.at(-1).source, 'Go Roshan');
  assert.equal(messages.at(-1).target, '打肉山');
  captions.accept({ type: 'subtitle.target.partial', streamId: 'app', turnId: 'next', text: '新一句' });
  assert.equal(messages.at(-1).source, ''); // Do not reuse the previous turn's original.
  for (let i = 0; i < 200; i++) captions.accept({ type: 'subtitle.source.final', streamId: String(i), turnId: String(i), text: 'go' });
  assert.equal(captions.turns.size, 100);
  assert.equal(captions.sources.size, 100);
  captions.clear();
  assert.equal(captions.turns.size, 0);
  assert.equal(messages.at(-1).type, 'overlay.clear');
});

test('full-range overlay position reaches 100%; old desktop fallback is explicit and preserves saved bottom preference', () => {
  const elements = new Map<string, any>();
  for (const id of ['overlayTop', 'overlayTopValue', 'overlayPositionLabel', 'overlayPositionHint']) {
    elements.set(id, { setAttribute(key: string, value: string) { this[key] = value; } });
  }
  const messages: any[] = [];
  const listener = new DesktopListener({
    room: () => null, onSubtitle() {}, bridge: null,
    root: { getElementById: (id: string) => elements.get(id) },
  });
  listener.bridge = { postMessage: (message: any) => messages.push(message) };
  listener.settings = overlaySettings({ top: 100 });
  listener.fullRangePosition = true;
  listener.postOverlaySettings();
  assert.equal(messages.at(-1).top, 100);
  assert.equal(elements.get('overlayTop').max, '100');
  assert.equal(elements.get('overlayTopValue').textContent, '100%');
  assert.match(elements.get('overlayTop')['aria-valuetext'], /底部/);
  listener.fullRangePosition = false;
  listener.postOverlaySettings();
  assert.equal(messages.at(-1).top, 60);
  assert.equal(elements.get('overlayTop').max, '60');
  assert.match(elements.get('overlayPositionHint').textContent, /升级到 v0\.1\.3/);
  assert.equal(listener.settings.top, 100, 'Legacy binary must not overwrite the saved bottom preference');
  listener.fullRangePosition = true;
  listener.postOverlaySettings();
  assert.equal(messages.at(-1).top, 100);
});
