-- On every lab unit that attacks (creeps, towers, the hero): its own attacks' swing beginning and attack point, as
-- [COACH_LASTHIT] JSON lines like addon_game_mode.lua's (whose globals this file does not see). Attack events may reach every
-- modifier that declares them, so only the parent's count.
modifier_coach_watch = class({})

function modifier_coach_watch:IsHidden() return true end
function modifier_coach_watch:IsPurgable() return false end
function modifier_coach_watch:RemoveOnDeath() return true end
function modifier_coach_watch:DeclareFunctions() return { MODIFIER_EVENT_ON_ATTACK_START, MODIFIER_EVENT_ON_ATTACK } end
function modifier_coach_watch:Report(kind, ev)
    if not IsServer() or ev.attacker ~= self:GetParent() then return end
    local target = ev.target and not ev.target:IsNull() and ev.target:entindex() or -1
    print(string.format('[COACH_LASTHIT]{"k":"%s","t":%.4f,"a":%d,"v":%d}', kind, GameRules:GetGameTime(), ev.attacker:entindex(), target))
end
-- The swing begins.
function modifier_coach_watch:OnAttackStart(ev) self:Report("swing", ev) end
-- The attack point: a melee hit lands now, a projectile leaves.
function modifier_coach_watch:OnAttack(ev) self:Report("launch", ev) end
