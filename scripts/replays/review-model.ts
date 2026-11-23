/** Model selection for offline replay reviews only. Never shared with live coaching. */
import { request as httpRequest } from 'node:http';
import type { IncomingMessage } from 'node:http';
import { request as httpsRequest } from 'node:https';
import path from 'node:path';
import { replayArea } from '../../src/replays/storage.ts';
import type { ResponsesTransport } from '../../src/tactics/reasoning-planner.ts';

const copilot = 'codey-copilot', sol = 'gpt-6.2-sol';
export const historicalReviews = (directory = replayArea()) => path.join(directory, 'study', 'reviews');
export interface ReviewProfile {
  provider: typeof copilot | 'azure' | 'openai';
  model: string;
  reasoningEffort: string;
}

export function reviewProfile(env?: NodeJS.ProcessEnv): ReviewProfile {
  if (!env) {
    try { process.loadEnvFile(new URL('../../.env', import.meta.url)); }
    catch (e) { if ((e as NodeJS.ErrnoException).code !== 'ENOENT') throw e; }
    env = process.env;
  }
  const provider = env.REPLAY_REVIEW_PROVIDER ?? copilot;
  if (provider !== copilot && provider !== 'azure' && provider !== 'openai')
    throw new Error('Unsupported REPLAY_REVIEW_PROVIDER; no provider fallback');
  const model = env.REPLAY_REVIEW_MODEL ?? (provider === copilot ? sol : env.JUNGLE_PLANNER_MODEL);
  const reasoningEffort = env.REPLAY_REVIEW_REASONING_EFFORT ?? (provider === copilot ? 'xhigh' : 'medium');
  if (!model || !/^[a-zA-Z0-9][a-zA-Z0-9._-]{0,99}$/.test(model) ||
      (provider === copilot && model !== sol && model !== 'gpt-6.2-astra'))
    throw new Error('Invalid replay review model for the selected provider');
  if (!['low', 'medium', 'high', 'xhigh', 'max'].includes(reasoningEffort))
    throw new Error('Invalid REPLAY_REVIEW_REASONING_EFFORT');
  return { provider, model, reasoningEffort };
}

export const reviewDirectory = (p: ReviewProfile, directory?: string) =>
  path.join(historicalReviews(directory), `${p.provider}--${p.model}--${p.reasoningEffort}`);
export const matchesReviewProfile = (record: Partial<ReviewProfile> & { version?: number }, p: ReviewProfile) =>
  record.version === 2 && record.provider === p.provider && record.model === p.model && record.reasoningEffort === p.reasoningEffort;

// As in the desktop adapter: only the local model gateway (not an admin API or a remote proxy).
function gatewayBase(raw: string) {
  if (raw !== raw.trim() || !/^https?:\/\/(?:127\.0\.0\.1|localhost|\[::1\])(?::[1-9]\d{0,4})?\/v1\/?$/i.test(raw))
    throw new Error('DOTA_COACH_COPILOT_BASE_URL must be a loopback HTTP(S) /v1 address');
  const url = new URL(raw);
  if (url.hostname === 'localhost') url.hostname = '127.0.0.1';
  return `${url.origin}/v1`;
}

/** Do not retry a permanent refusal, send the next replay, or fall back to a different target. */
export class ReviewTargetError extends Error {}
type ReviewTransport=(body:Record<string,unknown>,signal:AbortSignal,onDispatch?:()=>void)=>ReturnType<ResponsesTransport>;

function copilotClient(profile: ReviewProfile, env: NodeJS.ProcessEnv) {
  const base = gatewayBase(env.DOTA_COACH_COPILOT_BASE_URL ?? 'http://127.0.0.1:4141/v1');
  const key = env.CODEY_MODEL_API_KEY;
  if (!key || key.length > 4096 || /[^\x21-\x7e]|[,'"]/.test(key))
    throw new ReviewTargetError('Set CODEY_MODEL_API_KEY to the Codey model gateway key; no request sent');

  async function json(route: 'models' | 'responses', signal: AbortSignal, body?: Record<string, unknown>) {
    // Direct connection, including when a system HTTP proxy is configured; never follow redirects with the key.
    const url = new URL(`${base}/${route}`), send = url.protocol === 'https:' ? httpsRequest : httpRequest;
    const res = await new Promise<IncomingMessage>((ok, fail) => {
      const req = send(url, { method: body ? 'POST' : 'GET', agent: false, signal, headers: {
        Authorization: `Bearer ${key}`, Accept: 'application/json', 'User-Agent': 'DotaCoach-Replay',
        ...(body && { 'Content-Type': 'application/json' }),
      } }, ok);
      req.on('error', fail);
      req.end(body && JSON.stringify(body));
    });
    try {
      if (res.statusCode !== 200) {
        const s = res.statusCode ?? 0, ErrorType = s < 500 && s !== 408 && s !== 429 ? ReviewTargetError : Error;
        // Do not put arbitrary upstream error text, echoed prompts, or credentials in a study log.
        let hint='';
        if(s===400){
          const pieces:Buffer[]=[];let n=0;
          for await(const part of res){n+=part.length;if(n>8192)break;pieces.push(part);}
          try{
            const detail=JSON.parse(Buffer.concat(pieces).toString()).error, text=String(detail?.message??'');
            const safe=(v:unknown)=>typeof v==='string'&&/^[a-z][a-z0-9_.]{0,48}$/.test(v)?v:'';
            hint=[safe(detail?.code),safe(detail?.param),
              /json.object|json.schema|text.format|structured.output/i.test(text)?'structured-output':'',
              /'json'|word.{0,10}json/i.test(text)?'json-in-input-required':'',
              /reasoning.effort/i.test(text)?'reasoning-effort':'',/model.{0,30}(not supported|not available)/i.test(text)?'model-unavailable':''
            ].filter(Boolean).join('/');
          }catch{/* Never echo the raw body. */}
        }
        throw new ErrorType(`Codey ${route} HTTP ${s}${hint?' ('+hint+')':''}`);
      }
      const limit = route === 'models' || Number(body?.max_output_tokens)>12000 ? 4 * 1024 ** 2 : 512 * 1024;
      const chunks: Buffer[] = []; let size = 0;
      for await (const chunk of res) {
        size += chunk.length;
        if (size > limit) throw new ReviewTargetError(`Codey ${route} response too large`);
        chunks.push(chunk);
      }
      try { return JSON.parse(Buffer.concat(chunks).toString('utf8')); }
      catch { throw new Error(`Invalid Codey ${route} JSON`); }
    } finally { res.destroy(); }
  }

  async function checkModel(signal: AbortSignal) {
    const catalog = await json('models', AbortSignal.any([signal, AbortSignal.timeout(15_000)]));
    const rows = Array.isArray(catalog?.data) && catalog.data.length <= 4096 &&
      (!('has_more' in catalog) || catalog.has_more === false)
      ? catalog.data.filter((r: any) => r?.id === profile.model) : [];
    const row = rows.length === 1 ? rows[0] : null, label = row?.display_name;
    const efforts = row?.capabilities?.supports?.reasoning_effort;
    if (typeof label !== 'string' || !(label.endsWith(' (github-copilot)') ||
        (label.startsWith('github-copilot ') && !label.endsWith(')'))) ||
        !Array.isArray(row.supported_endpoints) || !row.supported_endpoints.includes('/responses') ||
        (row.policy?.state != null && row.policy.state !== 'enabled') ||
        !Array.isArray(efforts) || !efforts.includes(profile.reasoningEffort))
      throw new ReviewTargetError(`Codey did not confirm GitHub Copilot ${profile.model} /responses with ${profile.reasoningEffort}; no replay sent`);
    return row.capabilities?.limits?.max_output_tokens;
  }
  const transport: ReviewTransport = async (body, signal, onDispatch) => {
    const ceiling=await checkModel(signal);
    if(Number(body.max_output_tokens)>12000){
      if(!Number.isSafeInteger(ceiling)||ceiling<1)throw new ReviewTargetError('Missing verified output ceiling for detailed replay review; no replay sent');
      body={...body,max_output_tokens:Math.min(Number(body.max_output_tokens),ceiling)};
    }
    onDispatch?.(); // A failed GET is not a model submission or a paid slot.
    const r = await json('responses', signal, body);
    if (r?.model !== profile.model || (r.reasoning?.effort != null && r.reasoning.effort !== profile.reasoningEffort))
      throw new ReviewTargetError('Codey returned a different model/effort or no model ID; discarded response');
    return r;
  };
  return { transport, checkModel };
}

export async function createReviewClient(profile: ReviewProfile, env: NodeJS.ProcessEnv = process.env) {
  let transport: ReviewTransport, checkModel: ((signal: AbortSignal) => Promise<unknown>) | undefined;
  if (profile.provider === copilot) {
    ({ transport, checkModel } = copilotClient(profile, env));
  } else {
    // Reuse an explicitly selected planner provider, never pass its credentials to Copilot or another provider.
    if (profile.provider !== (env.JUNGLE_PLANNER_PROVIDER ?? 'azure'))
      throw new ReviewTargetError('Selected replay provider does not match the configured planner credentials');
    const { configuredResponsesClient } = await import('../../src/tactics/reasoning-planner.ts');
    const client = configuredResponsesClient({ ...env, JUNGLE_PLANNER_MODEL: profile.model });
    if (!client) throw new ReviewTargetError('Selected replay provider is not configured; no fallback');
    transport = (body,signal,onDispatch)=>{onDispatch?.();return client.transport(body,signal);};
  }
  return { checkModel, generate: (instructions: string, input: string, signal: AbortSignal,
    schema?:{name:string;schema:Record<string,unknown>},onDispatch?:()=>void,outputTokens=12000) => transport({
    model: profile.model, store: false, instructions,
    input: [{ role: 'user', content: [{ type: 'input_text', text: input }] }],
    reasoning: { effort: profile.reasoningEffort }, max_output_tokens: outputTokens,
    text: { format: schema ? { type:'json_schema',strict:true,...schema } : { type: 'json_object' } },
  }, signal,onDispatch) };
}
