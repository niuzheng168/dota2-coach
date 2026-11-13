import assert from 'node:assert/strict';
import { once } from 'node:events';
import fs from 'node:fs/promises';
import os from 'node:os';
import path from 'node:path';
import { test } from 'node:test';
import type { TestContext } from 'node:test';
import WebSocket, { WebSocketServer } from 'ws';
import { DEFAULT_FOUNDRY_CONFIG, getGptLiveFoundryConfig, loadFoundryConfig, tokenManager } from '../src/azure-auth.ts';
import { connectSessionSocket, waitForSessionEvent } from '../src/engines/ws-session.ts';
import { VoiceLiveEngine } from '../src/engines/voice-live-engine.ts';

async function socketPair(t: TestContext) {
  const server = new WebSocketServer({ port: 0, host: '127.0.0.1' });
  await once(server, 'listening');
  const address = server.address();
  assert.ok(address && typeof address === 'object');
  const url = `ws://127.0.0.1:${address.port}`;
  const incoming = once(server, 'connection');
  const client = new WebSocket(url);
  client.on('error', () => {});
  const [peer] = await incoming as [WebSocket];
  await once(client, 'open');
  t.after(async () => {
    client.terminate(); peer.terminate();
    await new Promise<void>(resolve => server.close(() => resolve()));
  });
  return { client, peer, url };
}

test('session update timeout rejects instead of falsely reporting readiness', async t => {
  const { client } = await socketPair(t);
  await assert.rejects(waitForSessionEvent(client, 'session.updated', () => {
    client.send(JSON.stringify({ type: 'session.update' }));
  }, 30), /Timed out waiting for session.updated/);
});

test('session wait requires the correct acknowledgment and cleans up listeners', async t => {
  const { client, peer } = await socketPair(t);
  const waiting = waitForSessionEvent(client, 'session.updated', undefined, 500);
  peer.send(JSON.stringify({ type: 'session.created' }));
  peer.send(JSON.stringify({ type: 'session.updated' }));
  await waiting;
  assert.equal(client.listenerCount('message'), 0);
  assert.equal(client.listenerCount('close'), 0);
});

test('service closes and explicit startup errors fail promptly, not with a false ready', async t => {
  const first = await socketPair(t);
  const closed = waitForSessionEvent(first.client, 'session.started', undefined, 500);
  first.peer.close();
  await assert.rejects(closed, /closed before session.started/);
  const second = await socketPair(t);
  const error = waitForSessionEvent(second.client, 'session.started', undefined, 500);
  second.peer.send(JSON.stringify({ type: 'error', error: { code: 'model_not_deployed', message: 'Deployment missing' } }));
  await assert.rejects(error, /model_not_deployed/);
});

test('socket startup waits for session.started and sends session.start once', async t => {
  const server = new WebSocketServer({ port: 0, host: '127.0.0.1' });
  await once(server, 'listening');
  const address = server.address();
  assert.ok(address && typeof address === 'object');
  server.on('connection', socket => socket.once('message', raw => {
    assert.equal(JSON.parse(raw.toString()).type, 'session.start');
    socket.send(JSON.stringify({ type: 'session.started' }));
  }));
  const ws = await connectSessionSocket(`ws://127.0.0.1:${address.port}`, {}, 'session.started', { type: 'session.start' });
  t.after(async () => {
    ws.terminate();
    for (const socket of server.clients) socket.terminate();
    await new Promise<void>(resolve => server.close(() => resolve()));
  });
  assert.equal(ws.readyState, WebSocket.OPEN);
});

test('dedicated GPT-Live credentials do not override the default region or API key', async t => {
  const directory = await fs.mkdtemp(path.join(os.tmpdir(), 'foundry-routing-'));
  const filename = path.join(directory, 'live.env');
  await fs.writeFile(filename, [
    'FOUNDRY_ENDPOINT=https://live-resource.services.ai.azure.com/api/projects/live',
    'AOAI_ENDPOINT=https://live-resource.openai.azure.com/openai/v1',
    'FOUNDRY_KEY=fake-live-key',
    'DEPLOYMENT_GPT_LIVE=live-deployment',
  ].join('\n'), { mode: 0o600 });
  t.after(() => fs.rm(directory, { recursive: true, force: true }));
  const previousKey = process.env.AZURE_API_KEY;
  const previousFile = process.env.GPT_LIVE_FOUNDRY_ENV_FILE;
  process.env.AZURE_API_KEY = 'fake-default-key';
  process.env.GPT_LIVE_FOUNDRY_ENV_FILE = filename;
  try {
    const config = getGptLiveFoundryConfig();
    assert.equal(config.openAiHost, 'live-resource.openai.azure.com');
    assert.equal(config.deployments.gptLive, 'live-deployment');
    assert.deepEqual(await tokenManager.getAuthHeaders(config), { 'api-key': 'fake-live-key' });
    assert.deepEqual(await tokenManager.getAuthHeaders(), { 'api-key': 'fake-default-key' });
    assert.equal(process.env.AZURE_API_KEY, 'fake-default-key');
    assert.notEqual(DEFAULT_FOUNDRY_CONFIG.openAiHost, config.openAiHost);
    await fs.writeFile(filename, 'FOUNDRY_KEY=not-an-endpoint');
    assert.throws(() => loadFoundryConfig(filename), /missing endpoint/);
  } finally {
    if (previousKey === undefined) delete process.env.AZURE_API_KEY; else process.env.AZURE_API_KEY = previousKey;
    if (previousFile === undefined) delete process.env.GPT_LIVE_FOUNDRY_ENV_FILE; else process.env.GPT_LIVE_FOUNDRY_ENV_FILE = previousFile;
  }
});

test('input transcription failures are visible instead of silently ignored', () => {
  const errors: string[] = [];
  const engine = new VoiceLiveEngine('cascade', { role: 'student', voiceLiveAsrModel: 'mai-transcribe-2-streaming' }, {
    onSourcePartial() {}, onSourceFinal() {}, onTargetPartial() {}, onTargetFinal() {},
    onTargetAudio() {}, onTurnMetrics() {}, onStatus() {}, onError: message => errors.push(message),
  });
  (engine as unknown as { handleServerEvent(json: string): void }).handleServerEvent(JSON.stringify({
    type: 'conversation.item.input_audio_transcription.failed', error: { message: 'ASR unavailable' },
  }));
  assert.match(errors[0], /ASR unavailable/);
});
