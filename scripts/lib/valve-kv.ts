// Small, non-executing reader for Valve's text KeyValues (not KV3).
// #base inclusion is deliberately handled by the caller, never by arbitrary paths.
export interface KV { [key: string]: string | KV }
export function parseKeyValues(text: string): KV {
  const tokens = text.replace(/^\uFEFF/, '').match(/"(?:\\.|[^"\\])*"|\/\/[^\n]*|[{}]|[^\s{}"]+/g) ?? [];
  let cursor = 0;
  function next(): string | undefined {
    while (tokens[cursor]?.startsWith('//')) cursor++;
    return tokens[cursor++];
  }
  function decode(token: string): string {
    return token.startsWith('"') ? token.slice(1, -1).replace(/\\(["\\])/g, '$1') : token;
  }
  function object(nested: boolean): KV {
    const result: KV = Object.create(null);
    for (let token = next(); token !== undefined; token = next()) {
      if (token === '}') {
        if (!nested) throw new Error('Unexpected KeyValues closing brace');
        return result;
      }
      if (token.startsWith('[')) continue; // platform conditions, e.g. [$WIN32]
      const value = next();
      if (value === undefined || value === '}') throw new Error(`Missing KeyValues value: ${token}`);
      const key = decode(token);
      const parsed = value === '{' ? object(true) : decode(value);
      if (key !== '#base') result[key] = parsed;
    }
    if (nested) throw new Error('Unclosed KeyValues object');
    return result;
  }
  return object(false);
}
export function kvObject(value: string | KV | undefined): KV {
  return value && typeof value === 'object' ? value : {};
}
export function numbers(value: string | KV | undefined): number[] {
  const raw = typeof value === 'object' ? value.value : value;
  if (typeof raw !== 'string' || !raw.trim()) return [];
  const result = raw.trim().split(/\s+/).map(Number);
  return result.every(Number.isFinite) ? result : [];
}
export function numericValues(value: string | KV | undefined): Record<string, number[]> {
  return Object.fromEntries(Object.entries(kvObject(value))
    .map(([key, val]) => [key, numbers(val)] as const).filter(([, val]) => val.length));
}
