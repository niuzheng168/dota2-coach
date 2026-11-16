-- Read-only map audit. Loaded explicitly into our own dedicated test process.
-- No joining matches, heroes, purchases, tree changes or map mutations.
local function quote(s) return string.format("%q", tostring(s)):gsub("\\\n", "\\n") end
local function encode(v)
    if v == nil then return "null" end
    if type(v) == "number" then
        if v ~= v or v == math.huge or v == -math.huge then return "null" end
        return string.format("%.9g", v)
    end
    if type(v) == "boolean" then return v and "true" or "false" end
    if type(v) == "string" then return quote(v) end
    local parts = {}
    if #v > 0 then
        for _, x in ipairs(v) do parts[#parts + 1] = encode(x) end
        return "[" .. table.concat(parts, ",") .. "]"
    end
    for k, x in pairs(v) do parts[#parts + 1] = quote(k) .. ":" .. encode(x) end
    table.sort(parts)
    return "{" .. table.concat(parts, ",") .. "}"
end
local function emit(kind, value) print("[COACH_MAP_AUDIT]" .. encode({kind=kind, data=value})) end
local function vector(v) return {v.x,v.y,v.z} end
local function call(e, method, ...)
    local args = {...}
    local ok, result = pcall(function() return e[method](e, unpack(args)) end)
    if ok then return result end
    return nil
end
if GetMapName() ~= "dota" then error("Expected default dota map") end
local trees = Entities:FindAllByClassname("ent_dota_tree")
local towers = Entities:FindAllByClassname("npc_dota_tower")
if #trees == 0 or #towers == 0 then return end
emit("begin", {map=GetMapName(), width=320,height=328,cellSize=64,origin={-10240,-10752},
              gameTime=GameRules:GetGameTime(), dotaTime=GameRules:GetDOTATime(false,false)})
for y=0,327 do
    local heights, walkable, blocked, trees0, trees24 = {}, {}, {}, {}, {}
    for x=0,319 do
        local p=Vector(-10240+(x+0.5)*64,-10752+(y+0.5)*64,0)
        heights[#heights+1]=GetGroundHeight(p,nil)
        walkable[#walkable+1]=GridNav:IsTraversable(p) and "1" or "0"
        blocked[#blocked+1]=GridNav:IsBlocked(p) and "1" or "0"
        trees0[#trees0+1]=GridNav:IsNearbyTree(p,0,true) and "1" or "0"
        trees24[#trees24+1]=GridNav:IsNearbyTree(p,24,true) and "1" or "0"
    end
    emit("row", {index=y, heights=heights,traversable=table.concat(walkable),blocked=table.concat(blocked),
                 nearbyTree0=table.concat(trees0),nearbyTree24=table.concat(trees24)})
end
for _,tree in ipairs(trees) do
    emit("tree",{index=tree:entindex(),position=vector(tree:GetAbsOrigin()),standing=call(tree,"IsStanding")})
end
for _,class in ipairs({"npc_dota_tower","npc_dota_neutral_spawner","path_corner",
                      "dota_movespeed_modifier_path","npc_dota_lotus_pool","npc_dota_unit_twin_gate"}) do
    for _,e in ipairs(Entities:FindAllByClassname(class)) do
        emit("entity", {class=class,name=e:GetName(),index=e:entindex(),position=vector(e:GetAbsOrigin()),
                       team=call(e,"GetTeamNumber"),hull=call(e,"GetHullRadius"),unitName=call(e,"GetUnitName"),
                       attackRange=call(e,"Script_GetAttackRange"),baseMoveSpeed=call(e,"GetBaseMoveSpeed"),
                       health=call(e,"GetMaxHealth"),dayVision=call(e,"GetDayTimeVisionRange"),
                       nightVision=call(e,"GetNightTimeVisionRange")})
    end
end
emit("end", {trees=#trees,towers=#towers})
