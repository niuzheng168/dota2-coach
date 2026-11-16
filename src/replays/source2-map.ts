/** Strict reader for Source2Viewer's TEXT entity values, not binary KV3.
 * Strings, resource tags, vectors and multiline spline arrays are data only.
 * Never evaluate them as JS. Unknown shapes fail rather than guessing geometry.
 */
export function balancedEnd(text: string, start: number): number {
  const open = text[start], close = open === '{' ? '}' : open === '[' ? ']' : '';
  if (!close) throw new Error('Expected opening delimiter');
  let depth = 1;
  for (let i = start + 1; i < text.length; i++) {
    if (text.startsWith('"""', i)) {
      const end = text.indexOf('"""', i + 3); if (end < 0) throw new Error('Unclosed multiline string');
      i = end + 2;
    } else if (text[i] === '"') {
      let end = i + 1;
      for (; end < text.length; end++) {
        if (text[end] === '\\') end++;
        else if (text[end] === '"') break;
      }
      if (end >= text.length) throw new Error('Unclosed string'); i = end;
    } else if (text.startsWith('//', i)) {
      const end = text.indexOf('\n', i); i = end < 0 ? text.length : end;
    } else if (text[i] === open) depth++;
    else if (text[i] === close && --depth === 0) return i;
  }
  throw new Error('Unclosed delimiter');
}
function quoted(text: string, start: number): [string, number] {
  for (let i = start + 1; i < text.length; i++) {
    if (text[i] === '\\') i++;
    else if (text[i] === '"') return [JSON.parse(text.slice(start, i + 1)), i + 1];
  }
  throw new Error('Unclosed string value');
}
export function parseArrayData(text: string): unknown[] {
  // Spline strings contain JSON-compatible arrays with trailing commas.
  // Remove trailing commas only outside quoted strings.
  let result = '';
  for (let i = 0; i < text.length; i++) {
    if (text[i] === '"') {
      const [, end] = quoted(text, i); result += text.slice(i, end); i = end - 1;
    } else if (text[i] !== ',' || !/^\s*[\]}]/.test(text.slice(i + 1))) result += text[i];
  }
  const value = JSON.parse(result);
  if (!Array.isArray(value)) throw new Error('Expected array data');
  return value;
}
export function entityValues(text: string): Record<string, any>[] {
  if (text.length > 64 * 1024 * 1024) throw new Error('Entity dump exceeds limit');
  const result: Record<string, any>[] = [];
  const marker = /\bkeyValues3Data\s*=\s*\{/g;
  for (let match = marker.exec(text); match; match = marker.exec(text)) {
    const start = marker.lastIndex - 1, end = balancedEnd(text, start);
    const body = text.slice(start + 1, end), values = /\bvalues\s*=\s*\{/.exec(body);
    if (!values) throw new Error('Missing entity values');
    const vStart = start + 1 + values.index + values[0].length - 1;
    const vEnd = balancedEnd(text, vStart), fields: Record<string, any> = Object.create(null);
    let cursor = vStart + 1;
    while (cursor < vEnd) {
      const ws = /^\s*/.exec(text.slice(cursor))![0]; cursor += ws.length;
      if (cursor >= vEnd) break;
      const field = /^([A-Za-z0-9_.]+)\s*=\s*/.exec(text.slice(cursor));
      if (!field) throw new Error(`Unsupported entity field near ${text.slice(cursor, cursor + 60)}`);
      const name = field[1]; cursor += field[0].length;
      if (Object.hasOwn(fields, name)) throw new Error(`Duplicate entity key ${name}`);
      if (text.startsWith('"""', cursor)) {
        const close = text.indexOf('"""', cursor + 3);
        if (close < 0 || close > vEnd) throw new Error('Unclosed entity multiline value');
        fields[name] = text.slice(cursor + 3, close).trim(); cursor = close + 3;
      } else {
        const tag = /^(resource_name|resource|soundevent):/.exec(text.slice(cursor));
        if (tag) cursor += tag[0].length;
        if (text[cursor] === '"') {
          const [value, close] = quoted(text, cursor); fields[name] = value; cursor = close;
        } else if (text[cursor] === '[') {
          const close = balancedEnd(text, cursor);
          fields[name] = parseArrayData(text.slice(cursor, close + 1)); cursor = close + 1;
        } else {
          const scalar = /^(true|false|null|-?(?:\d+(?:\.\d*)?|\.\d+)(?:e[+-]?\d+)?)(?=\s|})/i.exec(text.slice(cursor));
          if (!scalar) throw new Error(`Unsupported value for ${name}`);
          fields[name] = JSON.parse(scalar[0]); cursor += scalar[0].length;
        }
      }
    }
    result.push(fields); marker.lastIndex = end + 1;
  }
  if (!result.length) throw new Error('No entity values decoded');
  return result;
}
export function extractSpeedPaths(entities: Record<string, any>[]) {
  return entities.filter(e => e.classname === 'dota_movespeed_modifier_path').map(e => {
    const nodes = parseArrayData(e.pathNodes), radii = parseArrayData(e.pathNodeRadiusScales);
    const heightRadii = parseArrayData(e.pathNodeRadiusHeightScales), speedTypes = parseArrayData(e.pathNodeMoveSpeedTypes);
    if (nodes.length !== radii.length || nodes.length !== heightRadii.length || nodes.length !== speedTypes.length ||
        !nodes.every(n => Array.isArray(n) && n.length === 9 && n.every(Number.isFinite)))
      throw new Error('Unsupported movespeed path schema');
    return { id: e.hammerUniqueId, origin: e.origin, angles: e.angles, scales: e.scales,
      nodes, radii, heightRadii, speedTypes,
      interpretation: 'Raw engine path control data only; spline interpolation, directional effect and exact volume membership are NOT inferred here.' };
  });
}
export function deriveLanePaths(entities: Record<string, any>[]) {
  const cornerRows=entities.filter(e => e.classname === 'path_corner');
  const corners = new Map(cornerRows.map(e => [e.targetname, e]));
  if(corners.size!==cornerRows.length)throw new Error('Duplicate named lane corner');
  return entities.filter(e => /^npc_dota_spawner_(good|bad)_(top|mid|bot)$/.test(e.classname)).map(e => {
    // Inspect the actual link field; no nearest-node graph or hand-shaped lane.
    const start = e.NPCFirstWaypoint;
    if (typeof start !== 'string' || !corners.has(start)) throw new Error('Spawner has no known NPCFirstWaypoint');
    const points: number[][] = [], names: string[] = [], visited = new Set<string>();
    let next = start;
    while (next) {
      if (visited.has(next)) throw new Error('Cyclic lane path');
      visited.add(next);
      const c = corners.get(next); if (!c || !Array.isArray(c.origin)) throw new Error('Broken lane path link');
      points.push(c.origin); names.push(next);
      // Source map top-lane terminal points target themselves. Retain this
      // explicit terminal convention, but reject cycles through other nodes.
      if (c.target === next) break;
      next = c.target || '';
    }
    return { id: e.classname, spawn: e.origin, names, points };
  });
}
export function validateEngineMapCapture(capture:any): void {
  const finiteVector=(v:any)=>Array.isArray(v)&&v.length===3&&v.every(Number.isFinite);
  if(capture.map!=='dota'||!Number.isInteger(capture.width)||!Number.isInteger(capture.height)||
    capture.width<1||capture.height<1||capture.width*capture.height>2_000_000||
    !Array.isArray(capture.rows)||capture.rows.length!==capture.height)throw new Error('Invalid engine grid dimensions');
  for(const [i,r] of capture.rows.entries()){
    if(r.index!==i||!Array.isArray(r.heights)||r.heights.length!==capture.width||!r.heights.every(Number.isFinite))
      throw new Error('Invalid engine height row');
    for(const name of ['traversable','blocked','nearbyTree0','nearbyTree24'])
      if(typeof r[name]!=='string'||r[name].length!==capture.width||!/^[01]+$/.test(r[name]))throw new Error('Invalid engine point-query row');
  }
  if(!Array.isArray(capture.trees)||capture.trees.length!==capture.completed?.trees||
    new Set(capture.trees.map((t:any)=>t.index)).size!==capture.trees.length||
    !capture.trees.every((t:any)=>Number.isInteger(t.index)&&finiteVector(t.position)))throw new Error('Incomplete/duplicate engine tree capture');
  if(!Array.isArray(capture.entities)||!capture.entities.every((e:any)=>finiteVector(e.position))||
    capture.entities.filter((e:any)=>e.class==='npc_dota_tower').length!==capture.completed?.towers)throw new Error('Incomplete engine entity capture');
}
