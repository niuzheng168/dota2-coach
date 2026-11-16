-- Controlled probes in our OWN disposable dedicated process only.
-- These are mechanism measurements, never a farming strategy.
if GetMapName() ~= "dota" then error("Expected default dota map") end
if #Entities:FindAllByClassname("npc_dota_tower") ~= 22 then return end
if COACH_MECHANICS_PROBE_STARTED then return end
COACH_MECHANICS_PROBE_STARTED=true
local function encode(v)
    if v==nil then return "null" end
    if type(v)=="number" then return string.format("%.9g",v) end
    if type(v)=="boolean" then return v and "true" or "false" end
    if type(v)=="string" then return string.format("%q",v) end
    local parts={}
    if #v>0 then for _,x in ipairs(v) do parts[#parts+1]=encode(x) end
        return "["..table.concat(parts,",").."]" end
    for k,x in pairs(v) do parts[#parts+1]=encode(k)..":"..encode(x) end
    table.sort(parts);return "{"..table.concat(parts,",").."}"
end
local function emit(kind,value) print("[COACH_MAP_AUDIT]"..encode({kind=kind,data=value})) end
local function vec(v) return {v.x,v.y,v.z} end
local function call(e,method,...)
    local args={...};local ok,value=pcall(function() return e[method](e,unpack(args)) end)
    if ok then return value end
    return nil
end
emit("begin",{map=GetMapName(),probe="controlled-movement-tree-v1"})
local function run()
    emit("sample",{label="before-unit-create"})
    local h=CreateUnitByName("npc_dota_hero_luna",Vector(-3500,-6112,256),true,nil,nil,DOTA_TEAM_GOODGUYS)
    if not h then emit("end",{error="Luna creation failed"});return end
    emit("sample",{label="unit-created",index=h:entindex()})
    h:AddNewModifier(h,nil,"modifier_invulnerable",{})
    h:SetIdleAcquire(false)
    local actions={}
    local function add(delay,fn) actions[#actions+1]={delay=delay,fn=fn} end
    local function sample(label,requested)
        local p=h:GetAbsOrigin();local mods={}
        for _,m in ipairs(h:FindAllModifiers()) do mods[#mods+1]=m:GetName() end
        table.sort(mods)
        emit("sample",{label=label,gameTime=GameRules:GetGameTime(),position=vec(p),requested=requested,
            baseSpeed=h:GetBaseMoveSpeed(),idealSpeed=call(h,"GetIdealSpeed"),hull=h:GetHullRadius(),
            computedSpeed=call(h,"GetMoveSpeedModifier",h:GetBaseMoveSpeed(),false),health=h:GetHealth(),
            mana=h:GetMana(),modifiers=mods})
    end
    local function at(p) h:Stop();h:SetAbsOrigin(GetGroundPosition(Vector(p[1],p[2],0),h)) end
    add(0.5,function() sample("bare-luna") end)
    local mask
    add(0.5,function()
        h:AddItemByName("item_power_treads"); mask=h:AddItemByName("item_mask_of_madness")
    end)
    add(1,function() sample("treads-mask-inactive") end)
    add(0.2,function() h:CastAbilityNoTarget(mask,-1) end)
    add(0.8,function()
        sample("mask-requested");emit("sample",{label="mask-cast-state",cooldown=mask:GetCooldownTimeRemaining(),mana=h:GetMana()})
    end)
    -- Probe existing speed-path anchor positions only. No guessed spline volume.
    for _,p in ipairs({{-3500,-6112},{-4382.938965,-7512.179688},{-128,-3008},
                       {3248,-8456},{3968,7296},{32,2592},{-3500,-6112}}) do
        local pos={p[1],p[2]}
        add(0.1,function() at(pos) end)
        add(0.6,function() sample("map-position",pos) end)
    end
    -- Controlled tree destruction/regrowth query, not natural respawn timing.
    local tree
    add(0.1,function()
        local candidates=Entities:FindAllByClassname("ent_dota_tree")
        for _,t in ipairs(candidates) do
            local p=t:GetAbsOrigin()
            if (p-Vector(-2560,-8704,128)):Length2D()<1 then tree=t;break end
        end
        if not tree then emit("sample",{label="tree-probe",error="Selected source tree missing"});return end
        local p=tree:GetAbsOrigin()
        emit("sample",{label="tree-before",position=vec(p),standing=call(tree,"IsStanding"),
                      nearby=GridNav:IsNearbyTree(p,0,true)})
        tree:CutDown(DOTA_TEAM_GOODGUYS)
    end)
    add(0.3,function()
        if not tree then return end
        emit("sample",{label="tree-cut",standing=call(tree,"IsStanding"),nearby=GridNav:IsNearbyTree(tree:GetAbsOrigin(),0,true)})
        local ok,err=pcall(function() tree:GrowBack() end)
        emit("sample",{label="tree-grow-command",success=ok,error=not ok and tostring(err) or nil})
    end)
    add(0.3,function()
        if tree then emit("sample",{label="tree-regrown",standing=call(tree,"IsStanding"),nearby=GridNav:IsNearbyTree(tree:GetAbsOrigin(),0,true)}) end
    end)
    -- Mask expires before the subsequent equipment-only walking interval.
    add(8,function() at({-3500,-6112}) end)
    add(0.6,function() sample("walk-start");h:MoveToPosition(Vector(-2500,-6112,256)) end)
    for i=1,30 do add(0.1,function() sample("walk") end) end
    add(0.1,function() h:Stop();emit("end",{completed=true,naturalTreeRegrowthTimerTested=false,fullPhysicsCertified=false});UTIL_Remove(h) end)
    local index=0
    h:SetContextThink("coach_mechanics_audit",function()
        index=index+1
        local action=actions[index]
        if not action then return nil end
        local ok,err=pcall(action.fn)
        if not ok then emit("end",{error=tostring(err),completed=false});return nil end
        local nextAction=actions[index+1]
        return nextAction and nextAction.delay or nil
    end,actions[1].delay)
end
local ok,err=pcall(run)
if not ok then emit("end",{error=tostring(err),completed=false}) end
