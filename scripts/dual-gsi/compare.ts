import fs from 'node:fs';
import path from 'node:path';
import {
  capabilities, clockTransitions, delayStats, heroVisibility, inferOwnTeam, minimapComparison, ntpCorrection,
  positionCheck, readRecording, readScreenshots, screenshotIndex, type Mode, type ProbeRecording,
} from './analyze.ts';

const arg = (name: string) => { const i = process.argv.indexOf(name); return i > 0 ? process.argv[i + 1] : undefined; };
const xDir = arg('--x'), yDir = arg('--y'), shotsDir = arg('--y-shots');
if (!xDir && !yDir) throw new Error('Usage: npm run probe:compare -- --x <X recording dir> --y <Y recording dir> [--y-shots <Steam screenshot dir>] [--out <dir>]');
const outDir = arg('--out') ?? path.join('.tmp', `dual-gsi-report-${new Date().toISOString().replace(/[:.]/g, '-')}`);

const x = xDir ? await readRecording(xDir) : null;
const y = yDir ? await readRecording(yDir) : null;
// Steam names use Y's local time, read here in this machine's time zone.
const shots = y?.packets.length && shotsDir ? readScreenshots(shotsDir, y.packets[0].recvMs - 60_000, y.packets.at(-1)!.recvMs + 60_000) : [];
const TEAM_ID: Record<string, number> = { radiant: 2, dire: 3 };
const xTeamName = x?.packets.find(p => p.teamName === 'radiant' || p.teamName === 'dire')?.teamName ?? null;
const ownTeam = xTeamName ? TEAM_ID[xTeamName] : y ? inferOwnTeam(y.packets) : null;
const modes = (r: ProbeRecording) => (['flat', 'groups'] as Mode[]).filter(m => r.packets.some(p => p.mode === m && p.clock !== null));
const only = (r: ProbeRecording, m: Mode) => r.packets.filter(p => p.mode === m);

const report: Record<string, unknown> = {
  generatedAt: new Date().toISOString(), scope: 'offline-observation-only',
  inputs: Object.fromEntries([['x', x], ['y', y]].filter(([, r]) => r).map(([k, r]) => {
    const rec = r as ProbeRecording;
    return [k, { dir: path.resolve(rec.dir), session: rec.session, packets: rec.packets.length, badLines: rec.badLines,
      truncated: rec.truncated, ntpSamples: rec.ntp.length }];
  })),
};
if (x) report.x = capabilities(x.packets);
if (y) report.y = Object.fromEntries(modes(y).map(m => [m, capabilities(only(y, m))]));

const ids = (r: ProbeRecording | null) => new Set(r?.packets.flatMap(p => p.matchId ? [p.matchId] : []) ?? []);
const xIds = ids(x), yIds = ids(y);
report.sameMatch = !xIds.size || !yIds.size ? 'unknown' : [...xIds].some(id => yIds.has(id)) ? 'yes' : 'no';

if (x && y) {
  const ntp = ntpCorrection(x.ntp, y.ntp);
  const tx = clockTransitions(x.packets);
  report.ntp = ntp;
  report.delayMs = Object.fromEntries(modes(y).map(m => [m, delayStats(tx, clockTransitions(only(y, m)), ntp.correctionMs)]));
  report.delayMsUncorrected = Object.fromEntries(modes(y).map(m => [m, delayStats(tx, clockTransitions(only(y, m)), 0)]));
  const ty = clockTransitions(only(y, 'flat'));
  report.positionCheck = positionCheck(tx, ty);
  report.minimapXvsY = minimapComparison(tx, ty);
}
if (y) {
  report.ownTeam = ownTeam;
  report.yMinimapHeroes = heroVisibility(only(y, 'flat'), ownTeam);
}
if (x) report.xMinimapHeroes = heroVisibility(x.packets, ownTeam);

fs.mkdirSync(outDir, { recursive: true });
fs.writeFileSync(path.join(outDir, 'report.json'), JSON.stringify(report, null, 2));
if (y && shots.length) {
  const rows = screenshotIndex(shots, y.packets);
  fs.writeFileSync(path.join(outDir, 'y-screenshots.csv'), ['file,utc,clock,mode,packetGapMs',
    ...rows.map(r => [r.file, new Date(r.ms).toISOString(), r.clock ?? '', r.mode ?? '', r.packetGapMs ?? ''].join(','))].join('\n'));
}

// ---------------------------------------------------------------------------------- markdown
const f = (v: unknown, unit = '') => v === null || v === undefined ? '—' : typeof v === 'number' ? `${Math.round(v * 10) / 10}${unit}` : String(v);
const clock = (s: number | null) => s === null ? '—' : `${s < 0 ? '-' : ''}${Math.floor(Math.abs(s) / 60)}:${String(Math.abs(s) % 60).padStart(2, '0')}`;
const lines: string[] = ['# 双机 GSI 对照报告', '', `生成时间：${report.generatedAt}。仅为离线观察，未调用模型、未接入教练。`, ''];
for (const [k, r] of [['X（打游戏）', x], ['Y（Plus 观战）', y]] as const) {
  if (r) lines.push(`- ${k}：\`${path.resolve(r.dir)}\`，${r.packets.length} 包，坏行 ${r.badLines}${r.truncated ? '，压缩包末尾被截断' : ''}，NTP ${r.ntp.length} 次`);
}
lines.push('', `同一局：${({ yes: '是（matchid 一致）', no: '**否，matchid 不一致**', unknown: '无法从 matchid 判断' } as Record<string, string>)[report.sameMatch as string]}`);

const cols: [string, ReturnType<typeof capabilities>][] = [];
if (x) cols.push(['X', report.x as ReturnType<typeof capabilities>]);
if (y) for (const m of modes(y)) cols.push([`Y ${m === 'flat' ? '(队伍视角)' : '(完整观战)'}`, capabilities(only(y, m))]);
const row = (label: string, get: (c: ReturnType<typeof capabilities>) => unknown) => `| ${label} | ${cols.map(([, c]) => f(get(c))).join(' | ')} |`;
lines.push('', '## 各机器 GSI 实际给了什么（对局内包）', '', `| 项目 | ${cols.map(([n]) => n).join(' | ')} |`, `|---|${cols.map(() => '---').join('|')}|`,
  row('对局内包数', c => c.inGamePackets), row('游戏时间', c => `${clock(c.clockFrom)}–${clock(c.clockTo)}`),
  row('收包间隔中位数 ms', c => c.intervalMs.median), row('player.team_name', c => Object.keys(c.teamNames).join('/')),
  row('自身英雄 hero.name 有值 %', c => c.heroPct), row('自身英雄坐标 %', c => c.heroPositionPct),
  row('装备非空 %', c => c.itemsPct), row('技能非空 %', c => c.abilitiesPct),
  row('teamN/playerN 分组 %', c => c.groupedPct), row('分组内玩家数（中位）', c => c.groupedPlayersMedian),
  row('小地图条目（中位）', c => c.minimapMedian), row('buildings 分组', c => c.buildings.join('/') || '—'),
  ...['roshan', 'couriers', 'neutralitems', 'draft', 'wearables', 'league'].map(k => row(`${k} 非空 %`, c => c.nonEmptyPct[k])),
  row('事件类型', c => Object.entries(c.eventTypes).map(([t, n]) => `${t}×${n}`).join(' ') || '—'));

if (x && y) {
  const ntp = report.ntp as ReturnType<typeof ntpCorrection>;
  lines.push('', '## Y 相对 X 的延迟', '',
    `时钟校准：${ntp.method}，修正 ${f(ntp.correctionMs, ' ms')}，服务器 ${ntp.servers.join(', ') || '—'}，` +
    `服务器间差异 ${f(ntp.spreadMs, ' ms')}，NTP 往返中位 ${f(ntp.rttMedianMs, ' ms')}。` +
    '方法：比较两台机器各自第一次收到同一个游戏秒（clock_time 从 T-1 跳到 T）的时刻。', '',
    '| Y 模式 | 样本 | 中位数 | P10 | P90 | 最小 | 最大 | 前 1/3 中位 | 后 1/3 中位 | 未校准中位 |', '|---|---|---|---|---|---|---|---|---|---|');
  const d = report.delayMs as Record<string, ReturnType<typeof delayStats>>, u = report.delayMsUncorrected as typeof d;
  for (const m of Object.keys(d)) {
    const s = d[m];
    lines.push(`| ${m === 'flat' ? '队伍视角' : '完整观战'} | ${s.n} | ${f(s.median, ' ms')} | ${f(s.p10)} | ${f(s.p90)} | ${f(s.min)} | ${f(s.max)} | ${f(s.firstThirdMedian)} | ${f(s.lastThirdMedian)} | ${f(u[m].median)} |`);
  }
  lines.push('', '## Y 小地图里 A 的英雄 vs X 自身坐标', '', '比较 X 在游戏秒 T 的坐标与 Y 在 T+偏移 的小地图坐标；距离最小处应在偏移 0。', '',
    '| 偏移（秒） | 样本 | 距离中位数 |', '|---|---|---|',
    ...(report.positionCheck as { lagSeconds: number; n: number; medianDistance: number | null }[]).map(r => `| ${r.lagSeconds} | ${r.n} | ${f(r.medianDistance)} |`));
  const mm = report.minimapXvsY as ReturnType<typeof minimapComparison>;
  lines.push('', '## X（玩家模式）有没有小地图', '', (report.x as ReturnType<typeof capabilities>).minimapMedian
    ? `有。与 Y 同一游戏秒对比 ${mm.compared} 次：单位种类重合度中位 ${f(mm.kindJaccardMedian)}，英雄集合完全一致 ${f(mm.heroSetsEqualPct, '%')}。`
    : 'X 的对局包里没有小地图数据。');
}
for (const [label, v] of [['Y 小地图英雄出现率（队伍视角）', report.yMinimapHeroes], ['X 小地图英雄出现率', report.xMinimapHeroes]] as const) {
  const h = v as ReturnType<typeof heroVisibility> | undefined;
  if (!h?.packets) continue;
  lines.push('', `## ${label}`, '', `${h.packets} 包；同名英雄出现多个条目的包占 ${f(h.duplicatePacketsPct, '%')}。`, '',
    '| 阵营 | 英雄 | 出现率 |', '|---|---|---|', ...h.heroes.map(r => `| ${r.side} | ${r.name} | ${f(r.presentPct, '%')} |`));
}
if (shots.length) lines.push('', `Y 截图与游戏时间的对应表：\`y-screenshots.csv\`（${shots.length} 张）。`);
lines.push('');
fs.writeFileSync(path.join(outDir, 'report.md'), lines.join('\n'));
console.log(lines.join('\n'));
console.error(`\nReport: ${path.resolve(outDir)}`);
