import type { FarmShadowBridge } from '../src/tactics/bridge-742.ts';

// Deliberately invented own-POV data; NOT replay world state or a captured player packet.
export const startingMs = Date.UTC(2026, 10, 22, 6) + 125;
export function playerPacket(at = startingMs, id = '742000001', clock = 660, team = 'radiant', hero = 'luna') {
  return {
    provider: { name: 'Dota 2', appid: 570, version: 48, timestamp: Math.floor(at / 1000) },
    map: { matchid: id, clock_time: clock, game_state: 'DOTA_GAMERULES_STATE_GAME_IN_PROGRESS', paused: false },
    player: { team_name: team, last_hits: 80, activity: 'playing', steamid: 'private-player-id', name: 'private-player-name' },
    hero: { name: 'npc_dota_hero_' + hero, level: 9, xpos: 1100, ypos: -5400, alive: true,
      stunned: false, disarmed: false, hexed: false, silenced: false, health: 900, max_health: 1100, mana: 400, max_mana: 520 },
    items: { slot0: { name: 'item_power_treads', cooldown: 0 }, slot1: { name: 'empty' },
      slot6: { name: 'item_bfury' }, neutral0: { name: 'item_manta' } },
    abilities: { ability0: { name: hero === 'troll_warlord' ? 'troll_warlord_whirling_axes_ranged' : 'luna_lucent_beam', level: 2, cooldown: 4 },
      ability1: { name: hero === 'troll_warlord' ? 'troll_warlord_battle_trance' : 'luna_eclipse', level: 1 } },
  };
}
export type State = ReturnType<FarmShadowBridge['state']>;
export function anchor(s: State, capturedAt: number) {
  if (!s.binding || !s.own) throw new Error('Fixture expected a fresh own frame');
  return { ...s.binding, clock: s.own.clock, capturedAt };
}
export function confirmation(s: State, at: number) {
  return { ...anchor(s, at), exercise: s.exercise, ownPlayerView: true, build: 6986,
    matchStartUnix: Math.floor(at / 1000) - 3_600 }; // invented start for this synthetic exercise
}
export function visible(s: State, at: number) {
  return { ...anchor(s, at), visibleNow: true, danger: 'not-seen',
    targets: [{ targetId: 'lane:bot', state: 'present', area: 'own' }, { targetId: 'good_1', state: 'empty', area: 'own' }] };
}
