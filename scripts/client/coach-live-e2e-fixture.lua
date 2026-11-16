-- Exact scenario used by the local live-coaching acceptance test.
-- Run only in an explicitly owned, insecure LOCAL BOT process with cheats enabled.
-- This prepares test inputs. It is not a farming policy or an income benchmark.
if GetMapName() ~= "dota" then error("Expected local dota map") end
if COACH_LIVE_E2E_FIXTURE_DONE then return end
local h=PlayerResource:GetSelectedHeroEntity(0)
if not h or h:GetUnitName()~="npc_dota_hero_luna" then error("Expected own selected Luna") end
COACH_LIVE_E2E_FIXTURE_DONE=true
while h:GetLevel()<10 do h:HeroLevelUp(false) end
for name,level in pairs({luna_lucent_beam=3,luna_moon_glaive=4,luna_lunar_orbit=2,luna_lunar_grace=2,luna_eclipse=1}) do
    local a=h:FindAbilityByName(name)
    if a and not a:IsHidden() then a:SetLevel(level) end
end
h:SetAbilityPoints(0)
if not h:HasItemInInventory("item_power_treads") then h:AddItemByName("item_power_treads") end
if not h:HasItemInInventory("item_mask_of_madness") then h:AddItemByName("item_mask_of_madness") end
h:SetHealth(h:GetMaxHealth())
h:SetMana(h:GetMaxMana())
h:Stop()
FindClearSpaceForUnit(h,Vector(-900,-6250,128),true)
print("[COACH_FIXTURE] own Luna level="..h:GetLevel().." treads+mask; position="..tostring(h:GetAbsOrigin()))
