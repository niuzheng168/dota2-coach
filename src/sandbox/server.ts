import http from 'node:http';
import fs from 'node:fs/promises';
import { createHash, randomBytes, timingSafeEqual } from 'node:crypto';
import { COMBAT, MAP } from '../tactics/combat-data.ts';
import { evaluateRoute } from './experiment.ts';
import { CAMP_EXITS, LANES, SANDBOX_RULES, SandboxWorld } from './world.ts';
import { evaluateEconomyPlan } from './economy.ts';
import { SANDBOX_RECOMMENDATION_SCOPE } from './recommendation-scope.ts';
import { SimulationTimeline } from '../simulation/timeline.ts';
import { CLIENT_MAP, DEFINITIONS, FRAME_SECONDS, ITEM_LABELS, LIMITATIONS, REFERENCE, nearestWalkable } from '../simulation/data.ts';
import { laneRoutes } from '../simulation/world.ts';

export function createSandboxServer(seed = 741, sessionToken?: string) {
  if (sessionToken !== undefined && !/^[A-Za-z0-9_-]{43}$/.test(sessionToken)) throw new Error('无效本机会话 token');
  const token = sessionToken || randomBytes(32).toString('base64url');
  let world = new SandboxWorld(seed);
  let simulation:SimulationTimeline|null=null;
  const simulator=()=>simulation??(simulation=new SimulationTimeline(seed));
  async function savedExamples(): Promise<any[]> {
    try {
      const record = JSON.parse(await fs.readFile(new URL('../../data/sandbox-luna-economy-api-run.json', import.meta.url), 'utf8'));
      return record.environment === SANDBOX_RULES.version && record.physicsRevision === SANDBOX_RULES.physicsRevision &&
        Array.isArray(record.examples) ? record.examples.slice(0, 8) : [];
    } catch { return []; }
  }
  // No route activation until a verified allowed-area validator exists.
  // Manual mechanics tests and isolated numeric reproductions remain available.
  const snapshot = () => ({ ...world.snapshot(), plan: null });
  const server = http.createServer(async (req, res) => {
    res.setHeader('Cache-Control', 'no-store');
    res.setHeader('X-Content-Type-Options', 'nosniff');
    res.setHeader('Referrer-Policy', 'no-referrer');
    res.setHeader('Content-Security-Policy', "default-src 'self'; script-src 'self'; style-src 'self'; img-src 'self' data:; connect-src 'self'; frame-ancestors 'none'");
    const json = (status: number, body: unknown) => {
      res.writeHead(status, { 'Content-Type': 'application/json; charset=utf-8' });
      res.end(JSON.stringify(body));
    };
    const address = server.address();
    const port = typeof address === 'object' && address ? address.port : 0;
    if (![`127.0.0.1:${port}`, `localhost:${port}`].includes(req.headers.host || ''))
      return json(403, { error: '只允许本机主机名' });
    if (req.headers.origin && ![`http://127.0.0.1:${port}`, `http://localhost:${port}`].includes(req.headers.origin))
      return json(403, { error: '跨站请求被拒绝' });
    const url = new URL(req.url || '/', `http://127.0.0.1:${port}`);
    const files: Record<string, string> = { '/': 'simulator.html', '/simulator': 'simulator.html',
      '/simulator.js':'simulator.js','/simulator.css':'simulator.css',
      '/simulator-icons.js':'simulator-icons.js',
      '/farming-replays':'farming-replays.html','/farming-replays.js':'farming-replays.js','/farming-replays.css':'farming-replays.css',
      '/minimap/manifest.json':'minimap/manifest.json','/minimap/minimap-sheet.png':'minimap/minimap-sheet.png','/minimap/luna.png':'minimap/luna.png',
      '/sandbox': 'sandbox.html', '/sandbox.js': 'sandbox.js', '/sandbox.css': 'sandbox.css',
      '/map-audit': 'map-audit.html', '/map-audit.js': 'map-audit.js', '/map-audit.css': 'map-audit.css' };
    if (req.method === 'GET' && Object.hasOwn(files, url.pathname)) {
      try {
        const content = await fs.readFile(new URL(`../../public/${files[url.pathname]}`, import.meta.url));
        const type = url.pathname.endsWith('.png')?'image/png':url.pathname.endsWith('.json')?'application/json':
          url.pathname.endsWith('.js') ? 'text/javascript' : url.pathname.endsWith('.css') ? 'text/css' : 'text/html';
        res.writeHead(200, { 'Content-Type': type==='image/png'?type:`${type}; charset=utf-8` }); res.end(content);
      } catch { json(500, { error: '沙盒页面不可用' }); }
      return;
    }
    const supplied = Buffer.from((req.headers.authorization || '').replace(/^Bearer /, ''));
    const expected = Buffer.from(token);
    if (supplied.length !== expected.length || !timingSafeEqual(supplied, expected))
      return json(401, { error: '需要本次沙盒会话 token（不是 Foundry 密钥）' });
    if(req.method==='GET'&&url.pathname==='/api/sim/state')return json(200,simulator().snapshot());
    if(req.method==='GET'&&url.pathname==='/api/farming-replays'){
      try{return json(200,JSON.parse(await fs.readFile(new URL('../../data/luna-farming-routes.json',import.meta.url),'utf8')));}
      catch{return json(503,{error:'职业回放路线资料尚未构建；请运行 replay:farming'});}
    }
    if(req.method==='GET'&&url.pathname==='/api/farming-study'){
      try{return json(200,JSON.parse(await fs.readFile(new URL('../../data/luna-hundred-index.json',import.meta.url),'utf8')));}
      catch{return json(503,{error:'100 场完整解析及逐场模型复盘尚未全部完成；不会展示假完成数量'});}
    }
    if(req.method==='GET'&&/^\/api\/farming-study\/\d+$/.test(url.pathname)){
      try{
        const id=Number(url.pathname.split('/').at(-1));
        const index=JSON.parse(await fs.readFile(new URL('../../data/luna-hundred-index.json',import.meta.url),'utf8'));
        if(!index.matches.some((m:any)=>m.matchId===id))return json(404,{error:'对局不在已核验研究集'});
        return json(200,JSON.parse(await fs.readFile(new URL(`../../.tmp/replays/study/${id}.route.json`,import.meta.url),'utf8')));
      }catch{return json(503,{error:'该回放的本地轨迹缓存不可用；请重新构建，不使用模拟轨迹替代'});}
    }
    if(req.method==='GET'&&url.pathname==='/api/sim/context')return json(200,{
      frameSeconds:FRAME_SECONDS,heroes:Object.entries(DEFINITIONS.heroes).map(([id,h])=>({id,name:h.label})),
      items:Object.entries(ITEM_LABELS).map(([id,name])=>({id,name})),limits:LIMITATIONS,
      defaultHeroSpawns:{radiant:nearestWalkable({x:-2200,y:-3356}),dire:nearestWalkable({x:1800,y:2600})},
      map:{bounds:REFERENCE.bounds,camps:REFERENCE.camps,trees:CLIENT_MAP.trees,overview:REFERENCE.overview,
        towers:CLIENT_MAP.towers,lanePaths:REFERENCE.lanePaths},walkPaths:laneRoutes(),
    });
    if (req.method === 'GET' && ['/api/map-audit', '/api/map-overview'].includes(url.pathname)) {
      try {
        const reference = JSON.parse(await fs.readFile(new URL('../../data/dota2-map-reference.json', import.meta.url), 'utf8'));
        if (url.pathname === '/api/map-overview') {
          const image = await fs.readFile(new URL('../../.tmp/map-reference/dota-map-741f.png', import.meta.url));
          if (createHash('sha256').update(image).digest('hex') !== reference.sources.files.find((f: any) => f.name === 'dota-map-741f.png')?.sha256)
            throw new Error('地图底图哈希不匹配');
          res.writeHead(200, { 'Content-Type': 'image/png' }); res.end(image); return;
        }
        const report = JSON.parse(await fs.readFile(new URL('../../data/replay-map-validation.json', import.meta.url), 'utf8'));
        const samples = JSON.parse(await fs.readFile(new URL('../../data/replay-map-samples.json', import.meta.url), 'utf8'));
        const clientAudit = await fs.readFile(new URL('../../data/dota2-client-map-validation.json', import.meta.url), 'utf8')
          .then(JSON.parse, () => null);
        return json(200, { reference, report, samples, clientAudit, oldSchematicLanes: LANES });
      } catch { return json(503, { error: '地图/回放核验资料尚未生成或本地缓存缺失；请先运行构建和解析命令' }); }
    }
    if (req.method === 'GET' && url.pathname === '/api/state') return json(200, snapshot());
    if (req.method === 'GET' && url.pathname === '/api/initial') return json(200, new SandboxWorld(world.seed).snapshot());
    if (req.method === 'GET' && url.pathname === '/api/context') return json(200, {
      rules: SANDBOX_RULES, camps: MAP.camps, campExits: CAMP_EXITS, lanes: LANES, grid: MAP.grid,
      recommendationScope: SANDBOX_RECOMMENDATION_SCOPE, examples: [],
      withdrawnExamples: (await savedExamples()).map(e => ({ id: e.id, label: e.label, status: 'withdrawn' })),
      initialPlayer: new SandboxWorld(world.seed).snapshot().player,
      mechanics: { hero: COMBAT.heroes.npc_dota_hero_luna,
        items: { item_mask_of_madness: COMBAT.items.item_mask_of_madness, item_power_treads: COMBAT.items.item_power_treads } },
    });
    if (req.method !== 'POST') return json(404, { error: '未知 API' });
    if (!req.headers['content-type']?.startsWith('application/json'))
      return json(415, { error: '需要 application/json' });
    try {
      const chunks: Buffer[] = []; let size = 0;
      for await (const chunk of req) {
        size += chunk.length;
        if (size > 64 * 1024) { json(413, { error: '请求超过 64 KiB' }); return; }
        chunks.push(Buffer.from(chunk));
      }
      const body = JSON.parse(Buffer.concat(chunks).toString('utf8'));
      if (!body || typeof body !== 'object' || Array.isArray(body)) throw new Error('需要 JSON 对象');
      if(url.pathname.startsWith('/api/sim/')){
        const sim=simulator();
        if(body.revision!==undefined&&body.revision!==sim.revision)return json(409,{error:'模拟状态已改变，请刷新后重新操作'});
        if(url.pathname==='/api/sim/reset'){const next=new SimulationTimeline(body.seed??seed);next.revision=sim.revision+1;simulation=next;return json(200,simulation.snapshot());}
        if(url.pathname==='/api/sim/advance')return json(200,sim.advance(body.ticks));
        if(url.pathname==='/api/sim/seek')return json(200,sim.seek(body.tick));
        if(url.pathname==='/api/sim/command')return json(200,sim.command(body.command));
        return json(404,{error:'未知模拟 API'});
      }
      if (url.pathname === '/api/reset') {
        const next = new SandboxWorld(body.seed ?? world.seed);
        world = next;
      } else if (url.pathname === '/api/action') {
        if (body.action?.type === 'cast') {
          if (typeof body.action.name !== 'string' || body.action.name.length > 80 ||
              (body.action.targetId !== undefined && typeof body.action.targetId !== 'string')) throw new Error('无效施法命令');
          world.cast(body.action.name, body.action.targetId);
        } else world.setOrder(body.action);
      } else if (url.pathname === '/api/step') {
        world.advance(body.seconds);
      } else if (url.pathname === '/api/evaluate') {
        if (body.steps !== undefined) return json(200, evaluateEconomyPlan(body.seed ?? world.seed, body.steps));
        return json(200, { ...evaluateRoute(body.seed ?? world.seed, body.visits),
          recommendationScope: SANDBOX_RECOMMENDATION_SCOPE }); // Isolated historical trial, not advice.
      } else if (url.pathname === '/api/load-example' || url.pathname === '/api/plan') {
        return json(409, { error: SANDBOX_RECOMMENDATION_SCOPE.reason,
          code: SANDBOX_RECOMMENDATION_SCOPE.code, recommendationScope: SANDBOX_RECOMMENDATION_SCOPE });
      } else return json(404, { error: '未知 API' });
      json(200, snapshot());
    } catch (error) {
      json(400, { error: error instanceof Error && !(error instanceof SyntaxError) ? error.message : '无效 JSON 请求' });
    }
  });
  server.requestTimeout = 10_000;
  server.headersTimeout = 10_000;
  return { server, token };
}
