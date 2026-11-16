/**
 * Fail closed: camp.team and the schematic lane/river drawing do not establish
 * an own-side movement domain. Do not replace missing evidence with a guessed
 * diagonal, a gold penalty, or an instruction that the model can ignore.
 *
 * Manual mechanics tests and isolated historical reproductions remain usable.
 * This gate must stay closed until paths, chase, pull exits and damage targets
 * can all be checked against a verified scenario area.
 */
export const SANDBOX_RECOMMENDATION_SCOPE = Object.freeze({
  status: 'blocked' as const,
  code: 'OWN_AREA_UNVERIFIED' as const,
  allowedArea: null,
  realGameSafetyVerified: false,
  reason: '路线建议已撤下：己方可活动区域尚未核验。旧线野示例实际触及夜魇营地；仅有本方营地编号和最低血量不足以证明路线安全。',
  remaining: [
    '连续复盘并核对职业录像中的位置、兵线、视野与撤回条件；现有材料只是抽看片段。',
    '确认本场景允许活动的区域，再校验全部移动路径、追击、引野出口和伤害目标。',
    '在该区域内重新进行模型提案与收益实验；历史分数不能充当实战建议。',
  ],
});
