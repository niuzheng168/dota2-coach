import assert from 'node:assert/strict';
import { createServer } from 'node:http';
import { once } from 'node:events';

export const models = ['gpt-6.2-astra', 'gpt-6.2-sol'];
export const labels = ['GPT-6.2 Astra', 'GPT-6.2 Sol'];
export const coachUA = 'DotaCoach/0.6.3';

// Only this upstream is fake. In particular it never serves /v1/* or adds (github-copilot).
export async function startMock(key, alternateKey, jpeg) {
  const calls = [], errors = [];
  const fixture = { limited: false, wrongEcho: false };
  const server = createServer(async (req, res) => {
    const reply = (status, obj) => res.writeHead(status, { 'content-type': 'application/json' }).end(JSON.stringify(obj));
    try {
      assert.ok(!req.headers['api-key'] && !req.headers['x-api-key']);
      const alternate = req.method === 'GET' && req.url === '/alternate/v1/models';
      assert.ok(req.headers.authorization === `Bearer ${alternate ? alternateKey : key}`, 'Unexpected upstream credential');
      if (!alternate) assert.equal(req.headers['copilot-integration-id'], 'vscode-chat');
      if (req.method === 'GET' && (req.url === '/models' || alternate)) {
        if (!alternate) assert.equal(req.headers['x-interaction-type'], 'model-access');
        calls.push(`GET ${req.url}`);
        const data = models.map((id, i) => ({
          id, name: labels[i], vendor: 'OpenAI', model_picker_enabled: true, policy: { state: 'enabled' },
          supported_endpoints: [fixture.limited && i === 0 ? '/v1/responses' : '/responses'],
          capabilities: { type: 'chat', supports: { vision: !(fixture.limited && i === 1) },
            limits: { max_prompt_tokens: 64000, vision: { max_prompt_image_size: fixture.imageLimit ?? 1024 * 1024 } } },
        }));
        return reply(200, { object: 'list', data: [...data, { ...data[0], id: 'hidden-fixture', model_picker_enabled: false }] });
      }
      assert.equal(`${req.method} ${req.url}`, 'POST /responses');
      assert.equal(req.headers['x-initiator'], 'user');
      assert.ok(req.headers['x-request-id']);
      let raw = '';
      req.setEncoding('utf8');
      for await (const part of req) {
        raw += part;
        assert.ok(raw.length <= 128 * 1024, 'Unexpectedly large smoke request');
      }
      const body = JSON.parse(raw);
      assert.ok(models.includes(body.model));
      assert.equal(body.store, false);
      assert.notEqual(body.stream, true);
      assert.ok(!Object.hasOwn(body, 'service_tier')); // Route witness: present on raw probes, stripped by Codey.
      assert.equal(body.input.length, 1);
      assert.equal(body.input[0].role, 'user');
      const parts = body.input[0].content;
      const image = parts.length === 2;
      assert.deepEqual(parts.map(p => p.type), image ? ['input_text', 'input_image'] : ['input_text']);
      assert.equal(req.headers['copilot-vision-request'], image ? 'true' : undefined);
      assert.ok(parts[0].text.length > 0);
      const redacted = image && parts[1].image_url !== jpeg;
      if (redacted) {
        // Classify what actually arrived, independently of the expected cap result.
        assert.ok(fixture.imageLimit && parts[1].image_url.startsWith('data:image/png;base64,'));
        const png = Buffer.from(parts[1].image_url.split(',')[1], 'base64');
        assert.equal(png.subarray(0, 8).toString('hex'), '89504e470d0a1a0a');
        assert.deepEqual([png.readUInt32BE(16), png.readUInt32BE(20), parts[1].detail], [96, 32, 'low']);
      } else if (image) {
        assert.equal(parts[1].detail, 'high');
      }
      const schema = body.text?.format;
      if (schema) {
        assert.equal(schema.type, 'json_schema');
        assert.equal(schema.strict, true);
        assert.equal(schema.name, image ? 'visible_farming_advice' : 'minute_farm_choice');
      }
      const kind = image ? 'image' : 'text';
      calls.push(`POST /responses ${body.model} ${kind} ${schema?.name ?? 'probe'}${redacted ? ' REDACTED_PNG' : ''}`);
      const text = image ? {
        hero: 'npc_dota_hero_luna', team: 'radiant', scene: 'synthetic', visibleFacts: ['gray tile'],
        unknowns: [], message: 'fixture-image-ok', nextSteps: ['wait'], cancelIf: 'change', marker: null, markerBasis: '',
      } : { optionId: 'smoke-lane' };
      return reply(200, {
        id: `resp-smoke-${kind}`, object: 'response', status: 'completed',
        model: fixture.wrongEcho ? models.find(id => id !== body.model) : body.model,
        output: [{ type: 'message', role: 'assistant', status: 'completed',
          content: [{ type: 'output_text', text: JSON.stringify(text) }] }],
        usage: { input_tokens: 17, output_tokens: 4, total_tokens: 21 },
      });
    } catch (e) {
      errors.push(e);
      reply(500, { error: { message: 'HTTP mock rejected the request' } });
    }
  });
  server.listen(0, '127.0.0.1');
  await once(server, 'listening');
  return { fixture, calls, errors, origin: `http://127.0.0.1:${server.address().port}`,
    close: () => { server.closeAllConnections(); return new Promise(resolve => server.close(resolve)); } };
}
