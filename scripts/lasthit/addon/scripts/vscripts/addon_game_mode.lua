-- Coach last-hit arena: a custom game on the default dota map for the creep HP timing. scripts/lasthit/lab.ts copies it into
-- game/dota_addons/coach_lasthit for one run of our OWN local -insecure game and removes it afterwards; it is never used in
-- matchmaking. Rounds of lane waves clash in the mid lane (in the open, under the Radiant tier-1 tower, under the Dire one)
-- with the player's hero behind the Radiant creeps. Every server frame the creeps' health, positions and targets are printed,
-- with every damage instance and kill, and every attack's swing and attack point, as [COACH_LASTHIT] JSON lines.
--   mode "record": the hero tries last hits and denies itself, ordering when the engine says the hit lands at or below its
--                  weakest roll, with the others' incoming damage scaled at random so some orders come early, some late.
--   mode "loop":   the hero only stands behind its creeps; the coach_attack console command (sent by the test harness when
--                  the game client's number turns red, after a simulated reaction) orders its attack. Server-side only.
--   vision:        record mode while the runner saves the screen for training a model on creep attacks; each round at a
--                  random time of day.
-- coach_config.lua (written by the runner) sets COACH_CONFIG = { hero, level, mode, rounds, seed, vision }.
pcall(require, "coach_config")
local cfg = COACH_CONFIG or {}
local HERO, LEVEL, MODE, ROUNDS, VISION = cfg.hero or "npc_dota_hero_luna", cfg.level or 1, cfg.mode or "record", cfg.rounds or 60, cfg.vision
LinkLuaModifier("modifier_coach_watch", "modifiers/modifier_coach_watch", LUA_MODIFIER_MOTION_NONE)
-- Without loading it here too the engine reports the modifier type unknown (custom game started from an uncompiled addon).
require("modifiers/modifier_coach_watch")
math.randomseed(cfg.seed or 1)

local function out(s) print("[COACH_LASTHIT]" .. s) end
local function num(v) return v == nil and "null" or string.format("%.6g", v) end
local function call(e, method, ...)
    local args = { ... }
    local ok, value = pcall(function() return e[method](e, unpack(args)) end)
    if ok then return value end
    return nil
end
local function try(label, fn)
    local ok, err = pcall(fn)
    if not ok then out(string.format('{"k":"warn","what":"%s","message":%q}', label, (tostring(err):gsub("[%c\\]", " ")))) end
end
local function now() return GameRules:GetGameTime() end
local function clock() return GameRules:GetDOTATime(false, false) end
local function index(e) return e and not e:IsNull() and e:entindex() or -1 end
local function armorShare(u)
    local armor = u:GetPhysicalArmorValue(false)
    return 1 - .06 * armor / (1 + .06 * math.abs(armor))
end

local SIDES = { [DOTA_TEAM_GOODGUYS] = "goodguys", [DOTA_TEAM_BADGUYS] = "badguys" }
function Precache(context)
    for _, side in pairs(SIDES) do
        for _, kind in ipairs({ "melee", "ranged", "flagbearer" }) do PrecacheUnitByNameSync("npc_dota_creep_" .. side .. "_" .. kind, context) end
        PrecacheUnitByNameSync("npc_dota_" .. side .. "_siege", context)
    end
end

local hero, heroIndex, team, fountain
local canAttack -- the hero's attack capability, given back only while an ordered attack is under way
local healths = {} -- creep -> its health over the last second (30 frames), for coach_attack
local R, D, lane, length -- Radiant and Dire mid tier-1 towers, the unit vector from R to D, their distance
local busy                -- the hero's attack in flight: target, order time, landed time
local round, roundUnits, roundEnd, nextRound, finished = 0, {}, nil, 0, false
local lastMove, frames, standoff, lastLand = -1, 0, 140, -100

-- Records this unit's attacks (modifier_coach_watch); true when the modifier took.
local function watch(u) return pcall(function() return u:AddNewModifier(u, nil, "modifier_coach_watch", {}) end) end

local function creeps()
    local list = {}
    for _, class in ipairs({ "npc_dota_creep_lane", "npc_dota_creep_siege" }) do
        for _, e in ipairs(Entities:FindAllByClassname(class)) do
            if not e:IsNull() and e:IsAlive() then list[#list + 1] = e end
        end
    end
    return list
end

-- Lane creeps gain 12 health per 7.5 minutes of game clock (siege creeps none); the game client assumes it, so spawned
-- creeps get it too.
local function grow(u)
    if u:GetUnitName():find("siege") then return end
    local steps = math.floor(math.max(0, clock()) / 450)
    if steps <= 0 then return end
    local m = u:GetMaxHealth() + 12 * steps
    u:SetBaseMaxHealth(m); u:SetMaxHealth(m); u:SetHealth(m)
end

local function spawn(teamNumber, at, direction, siege)
    local side = SIDES[teamNumber]
    local names = { "npc_dota_creep_" .. side .. "_melee", "npc_dota_creep_" .. side .. "_melee", "npc_dota_creep_" .. side .. "_flagbearer",
        "npc_dota_creep_" .. side .. "_ranged" }
    if siege then names[#names + 1] = "npc_dota_" .. side .. "_siege" end
    local across = Vector(-direction.y, direction.x, 0)
    for i, name in ipairs(names) do
        local behind = name:find("ranged") and 220 or name:find("siege") and 320 or 0
        local p = at - direction * behind + across * ((i - 2.5) * 70 + RandomInt(-20, 20))
        local u = CreateUnitByName(name, GetGroundPosition(p, nil), true, nil, nil, teamNumber)
        if u then
            grow(u)
            watch(u)
            ExecuteOrderFromTable({ UnitIndex = u:entindex(), OrderType = DOTA_UNIT_ORDER_ATTACK_MOVE, Position = at + direction * 4000 })
            roundUnits[#roundUnits + 1] = u
        end
    end
end

local function remove(e)
    out(string.format('{"k":"gone","t":%.4f,"id":%d}', now(), e:entindex()))
    UTIL_Remove(e)
end

local KINDS = { "open", "ours", "theirs" }
local function startRound()
    round = round + 1
    if round > ROUNDS then finished = true; return end
    for _, e in ipairs(creeps()) do remove(e) end
    roundUnits, roundEnd = {}, nil
    local kind = KINDS[(round - 1) % #KINDS + 1]
    if VISION then try("daytime", function() GameRules:SetTimeOfDay(math.random()) end) end
    local clash = kind == "ours" and R + lane * 450 or kind == "theirs" and D - lane * 450 or R + lane * (.38 * length)
    spawn(DOTA_TEAM_GOODGUYS, clash - lane * 650, lane, round % 3 == 0)
    spawn(DOTA_TEAM_BADGUYS, clash + lane * 650, lane * -1, round % 3 == 0)
    busy = nil
    hero:Stop()
    FindClearSpaceForUnit(hero, GetGroundPosition(clash - lane * (650 + standoff), hero), true)
    out(string.format('{"k":"round","t":%.4f,"n":%d,"kind":"%s","x":%.0f,"y":%.0f,"clock":%.2f,"daytime":%s}', now(), round, kind, clash.x, clash.y, clock(),
        num(call(GameRules, "GetTimeOfDay"))))
end

local function record(t, list)
    local parts = {}
    local function flush() out(string.format('{"k":"f","t":%.4f,"c":[%s]}', t, table.concat(parts, ","))); parts = {} end
    for _, e in ipairs(list) do
        local p = e:GetAbsOrigin()
        local h = healths[e:entindex()] or {}
        h[#h + 1] = e:GetHealth()
        if #h > 30 then table.remove(h, 1) end
        healths[e:entindex()] = h
        parts[#parts + 1] = string.format("[%d,%d,%d,%.0f,%.0f,%d]", e:entindex(), e:GetHealth(), e:GetMaxHealth(), p.x, p.y, index(call(e, "GetAttackTarget")))
        -- Short console lines: a frame's creeps go out in chunks of 12.
        if #parts == 12 then flush() end
    end
    if #parts > 0 then flush() end
    local p, f = hero:GetAbsOrigin(), hero:GetForwardVector()
    out(string.format('{"k":"h","t":%.4f,"h":[[%d,"%s",%d,%.0f,%.0f,%.3f,%d,%d]]}', t, heroIndex, HERO, team, p.x, p.y, math.atan2(f.y, f.x),
        index(call(hero, "GetAttackTarget")), call(hero, "IsAttacking") and 1 or 0))
end

-- Entity indices are reused by later waves: a unit is described again whenever its index holds a new entity.
local named = {}
local function describe(e)
    local id = e:entindex()
    if named[id] == e then return end
    named[id] = e
    healths[id] = nil
    out(string.format('{"k":"unit","t":%.4f,"id":%d,"name":"%s","team":%d,"armor":%s,"dmin":%s,"dmax":%s,"spa":%s,"point":%s,"proj":%s}',
        now(), id, e:GetUnitName(), e:GetTeamNumber(), num(call(e, "GetPhysicalArmorValue", false)), num(call(e, "GetBaseDamageMin")),
        num(call(e, "GetBaseDamageMax")), num(call(e, "GetSecondsPerAttack", false) or call(e, "GetSecondsPerAttack")),
        num(call(e, "GetAttackAnimationPoint")), num(call(e, "GetProjectileSpeed"))))
end

local function attackState()
    local spa = call(hero, "GetSecondsPerAttack", false) or call(hero, "GetSecondsPerAttack")
    local speed = call(hero, "GetAttackSpeed", false) or call(hero, "GetAttackSpeed")
    if speed and speed > 5 then speed = speed / 100 end -- a percentage on some builds
    return spa, speed, call(hero, "GetAttackAnimationPoint"), call(hero, "GetProjectileSpeed") or 0,
        call(hero, "Script_GetAttackRange") or 0
end

local function order(target, t, kind, tag)
    local here = hero:GetAbsOrigin()
    local to = target:GetAbsOrigin() - here
    local f = hero:GetForwardVector()
    local turn = math.abs(math.atan2(f.x * to.y - f.y * to.x, f.x * to.x + f.y * to.y))
    local spa, speed, point, proj, range = attackState()
    if canAttack then hero:SetAttackCapability(canAttack) end
    ExecuteOrderFromTable({ UnitIndex = heroIndex, OrderType = DOTA_UNIT_ORDER_ATTACK_TARGET, TargetIndex = target:entindex(), Queue = false })
    busy = { target = target:entindex(), at = t }
    -- sinceLand: seconds since the hero's last hit landed; an order inside its attack interval waits for the cooldown.
    out(string.format('{"k":"order","t":%.4f,"kind":"%s","tag":%s,"target":%d,"hp":%d,"dist":%.1f,"turn":%.4f,"spa":%s,"point":%s,' ..
        '"proj":%s,"range":%s,"dmin":%s,"dmax":%s,"level":%d,"sinceLand":%.4f}', t, kind, tag and string.format("%q", tag) or "null",
        target:entindex(), target:GetHealth(), to:Length2D(), turn, num(spa), num(point), num(proj), num(range), num(call(hero, "GetDamageMin")),
        num(call(hero, "GetDamageMax")), hero:GetLevel(), t - lastLand))
end

-- Expected damage landing on a creep within `eta` seconds from everything that targets it now (engine knowledge).
local function incoming(target, eta, list)
    local total = 0
    local function add(u)
        if u:IsNull() or not u:IsAlive() or u == hero or call(u, "GetAttackTarget") ~= target then return end
        local spa = call(u, "GetSecondsPerAttack", false) or call(u, "GetSecondsPerAttack") or 1
        total = total + ((call(u, "GetBaseDamageMin") or 0) + (call(u, "GetBaseDamageMax") or 0)) / 2 * eta / math.max(.2, spa)
    end
    for _, u in ipairs(list) do add(u) end
    for _, u in ipairs(Entities:FindAllByClassname("npc_dota_tower")) do add(u) end
    return total * armorShare(target)
end

-- Record mode: last hits and denies by engine knowledge, deliberately early or late.
local function tryLastHit(t, list)
    local spa, speed, point, proj, range = attackState()
    local swing = point and speed and point / speed or .4
    local here, reach = hero:GetAbsOrigin(), range + hero:GetHullRadius() + 16
    local dmin = call(hero, "GetDamageMin") or 0
    local best
    for _, c in ipairs(list) do
        local d = (c:GetAbsOrigin() - here):Length2D()
        if d <= reach + 60 and (c:GetTeamNumber() ~= team or c:GetHealth() < c:GetMaxHealth() / 2) then
            local eta = swing + (proj > 0 and d / proj or 0)
            local mine = dmin * armorShare(c) * (c:GetClassname() == "npc_dota_creep_siege" and .5 or 1)
            if c:GetHealth() - incoming(c, eta, list) * (.2 + 1.2 * math.random()) <= mine and (not best or c:GetHealth() < best:GetHealth()) then best = c end
        end
    end
    if best and math.random() < .7 then order(best, t, "record") end
end

-- Stand behind the clash: the nearest pair of opposing creeps.
local function position(t, list)
    if t - lastMove < .5 then return end
    local a, b, nearest
    for _, x in ipairs(list) do
        if x:GetTeamNumber() == team then
            for _, y in ipairs(list) do
                if y:GetTeamNumber() ~= team then
                    local d = (x:GetAbsOrigin() - y:GetAbsOrigin()):Length2D()
                    if d < 1200 and (not nearest or d < nearest) then a, b, nearest = x, y, d end
                end
            end
        end
    end
    if not a then return end
    local front = (a:GetAbsOrigin() + b:GetAbsOrigin()) * .5
    local spot = front + (fountain - front):Normalized() * standoff
    if (spot - hero:GetAbsOrigin()):Length2D() > 120 then hero:MoveToPosition(GetGroundPosition(spot, hero)); lastMove = t end
end

local function control(t, list)
    if busy then
        local target = EntIndexToHScript(busy.target)
        local gone = not target or target:IsNull() or not target:IsAlive()
        if not (busy.landed or gone or t - busy.at > 2.5) then return end
        out(string.format('{"k":"done","t":%.4f,"target":%d,"landed":%s,"gone":%s}', t, busy.target,
            busy.landed and string.format("%.4f", busy.landed) or "null", gone and "true" or "false"))
        hero:Stop(); busy = nil
        if canAttack then hero:SetAttackCapability(DOTA_UNIT_CAP_NO_ATTACK) end
    end
    -- An attack nobody ordered (the player's auto-attack setting can override idle acquisition).
    if call(hero, "GetAttackTarget") then
        out(string.format('{"k":"unordered","t":%.4f,"target":%d}', t, index(call(hero, "GetAttackTarget"))))
        hero:Stop()
    end
    -- Record mode: the hero attacks in odd rounds only, so even rounds keep every last-hit chance for the evaluation.
    if MODE == "record" and round % 2 == 1 then tryLastHit(t, list) end
    if not busy then position(t, list) end
end

-- coach_attack <x> <y> <unit name> <tag> <health shown>: the harness's "player" attacks the creep with the red number. Like a
-- player clicking it, it knows which one: of that name near the (GSI, slightly stale) position, the one whose health in the
-- last second came closest to the health shown.
local function cue(x, y, name, tag, shown)
    local t = now()
    if MODE ~= "loop" or not hero then return end
    local best, score
    for _, c in ipairs(creeps()) do
        local d = (c:GetAbsOrigin() - Vector(x, y, 0)):Length2D()
        local s = d / 20
        if shown then
            local closest = math.abs(c:GetHealth() - shown)
            for _, h in ipairs(healths[c:entindex()] or {}) do closest = math.min(closest, math.abs(h - shown)) end
            s = s + closest
        end
        if c:GetUnitName() == name and d < 450 and (not score or s < score) then best, score = c, s end
    end
    if not best or busy then
        out(string.format('{"k":"cue","t":%.4f,"tag":%q,"found":%s,"busy":%s}', t, tag or "", best and "true" or "false", busy and "true" or "false"))
        return
    end
    order(best, t, "cue", tag)
end
Convars:RegisterCommand("coach_attack", function(_, x, y, name, tag, shown)
    local ok, err = pcall(cue, tonumber(x), tonumber(y), name, tag, tonumber(shown))
    if not ok then out(string.format('{"k":"error","t":%.4f,"message":%q}', now(), (tostring(err):gsub("[%c\\]", " ")))) end
end, "Coach last-hit lab: the hero attacks the named creep nearest x y", 0)

local function setup()
    hero = PlayerResource:GetSelectedHeroEntity(0)
    heroIndex, team = hero:entindex(), hero:GetTeamNumber()
    fountain = team == DOTA_TEAM_GOODGUYS and Vector(-7000, -6500, 0) or Vector(7000, 6400, 0)
    while hero:GetLevel() < LEVEL do hero:HeroLevelUp(false) end
    hero:AddNewModifier(hero, nil, "modifier_invulnerable", {})
    local _, watched = watch(hero)
    out(string.format('{"k":"watch","added":%s}', tostring(watched ~= nil)))
    -- Never auto-attack: no idle acquisition (control() also stops any attack it did not order).
    hero:SetIdleAcquire(false)
    try("acquire", function() hero:SetAcquisitionRange(0) end)
    try("capability", function() canAttack = hero:GetAttackCapability(); hero:SetAttackCapability(DOTA_UNIT_CAP_NO_ATTACK) end)
    try("camera", function() PlayerResource:SetCameraTarget(0, hero) end)
    SendToServerConsole("dota_creeps_no_spawning 1")
    local spa, speed, point, proj, range = attackState()
    standoff = proj > 0 and math.max(140, math.min(range, 550) - 80) or 120
    for _, t in ipairs(Entities:FindAllByClassname("npc_dota_tower")) do
        local p = t:GetAbsOrigin()
        if t:GetUnitName() == "npc_dota_goodguys_tower1_mid" then R = p end
        if t:GetUnitName() == "npc_dota_badguys_tower1_mid" then D = p end
        watch(t)
        out(string.format('{"k":"tower","id":%d,"name":"%s","team":%d,"x":%.0f,"y":%.0f}', t:entindex(), t:GetUnitName(), t:GetTeamNumber(), p.x, p.y))
    end
    if not R or not D then error("Mid tier-1 towers not found") end
    lane = Vector(D.x - R.x, D.y - R.y, 0):Normalized()
    length = Vector(D.x - R.x, D.y - R.y, 0):Length2D()
    out(string.format('{"k":"begin","t":%.4f,"dota":%.3f,"hero":"%s","team":%d,"level":%d,"mode":"%s","rounds":%d,"tick":%s}',
        now(), clock(), HERO, team, hero:GetLevel(), MODE, ROUNDS, num(FrameTime())))
    ListenToGameEvent("entity_killed", function(ev)
        out(string.format('{"k":"kill","t":%.4f,"v":%d,"a":%d}', now(), ev.entindex_killed or -1, ev.entindex_attacker or -1))
    end, nil)
    try("filter", function()
        GameRules:GetGameModeEntity():SetDamageFilter(function(_, ev)
            local victim = ev.entindex_victim_const and EntIndexToHScript(ev.entindex_victim_const)
            out(string.format('{"k":"dmg","t":%.4f,"v":%d,"a":%d,"d":%s,"type":%s,"inflictor":%d,"hp":%s}', now(), ev.entindex_victim_const or -1,
                ev.entindex_attacker_const or -1, num(ev.damage), num(ev.damagetype_const), ev.entindex_inflictor_const or -1,
                victim and num(victim:GetHealth()) or "null"))
            if busy and ev.entindex_attacker_const == heroIndex and ev.entindex_victim_const == busy.target and not ev.entindex_inflictor_const then
                busy.landed = now(); lastLand = now()
            end
            return true
        end, {})
    end)
end

local function think()
    if not hero then
        local h = PlayerResource:GetSelectedHeroEntity(0)
        if not h or clock() < 0 then return .25 end
        setup()
        nextRound = now() + 1
    end
    local t = now()
    local list = creeps()
    for _, e in ipairs(list) do describe(e) end
    record(t, list)
    control(t, list)
    if finished then
        out(string.format('{"k":"end","t":%.4f,"frames":%d,"rounds":%d}', t, frames, round - 1))
        return nil
    end
    -- A round ends a second after one side is wiped out (or after 45 s); the survivors are removed.
    local sides = {}
    for _, u in ipairs(roundUnits) do if not u:IsNull() and u:IsAlive() then sides[u:GetTeamNumber()] = true end end
    if round > 0 and not roundEnd and (not sides[DOTA_TEAM_GOODGUYS] or not sides[DOTA_TEAM_BADGUYS] or t - nextRound > 45) then
        roundEnd = t + 1
    end
    if round == 0 and t >= nextRound or roundEnd and t >= roundEnd then
        nextRound = t
        startRound()
    end
    frames = frames + 1
    return 1 / 30
end

function Activate()
    local mode = GameRules:GetGameModeEntity()
    try("setup", function()
        GameRules:EnableCustomGameSetupAutoLaunch(true)
        GameRules:SetCustomGameSetupAutoLaunchDelay(0)
        GameRules:SetCustomGameTeamMaxPlayers(DOTA_TEAM_GOODGUYS, 1)
        GameRules:SetCustomGameTeamMaxPlayers(DOTA_TEAM_BADGUYS, 0)
        GameRules:SetHeroSelectionTime(1)
        GameRules:SetStrategyTime(0)
        GameRules:SetShowcaseTime(0)
        GameRules:SetPreGameTime(1)
        mode:SetCustomGameForceHero(HERO)
    end)
    try("world", function()
        mode:SetFogOfWarDisabled(true)
        mode:SetDaynightCycleDisabled(true)
        mode:SetAnnouncerDisabled(true)
    end)
    -- The hero stays at the configured level.
    try("levels", function()
        local xp = {}
        for i = 0, 30 do xp[i] = i * 1000000 end
        mode:SetUseCustomHeroLevels(true)
        mode:SetCustomXPRequiredToReachNextLevel(xp)
    end)
    mode:SetContextThink("coach_lasthit", function()
        local ok, result = pcall(think)
        if ok then return result end
        out(string.format('{"k":"error","t":%.4f,"message":%q}', now(), (tostring(result):gsub("[%c\\]", " "))))
        return 1 / 30
    end, .5)
    out(string.format('{"k":"activate","map":"%s","mode":"%s","hero":"%s"}', GetMapName(), MODE, HERO))
end
