-- Test preparation only, never executed by the coach or its model.
if GetMapName() ~= "dota" then error("Expected local dota map") end
local h=PlayerResource:GetSelectedHeroEntity(0)
if not h or h:GetUnitName()~="npc_dota_hero_luna" then error("Expected own Luna") end
h:MoveToPosition(Vector(-900,-7100,0))
print("[COACH_FIXTURE] Own Luna moved toward allied camp for observation; not a model-issued order")
