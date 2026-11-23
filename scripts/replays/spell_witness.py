"""Record a team's witnessed casts, damage to allies, and the focal body's own defensive resources.

Never inspect an enemy's abilities, inventory or cooldowns. The engine's rank
annotation on a visible cast is kept for a separately labelled audit, not for the
pre-decision reviewer. A visible event does not disclose its caster's location.
"""
import math
import re


HERO = re.compile(r"npc_dota_hero_[a-z0-9_]+")
KEY = re.compile(r"[a-z][a-z0-9_]{0,120}")


def witness(e, team, own, opponent_labels):
    typ = str(e.log_type)
    if (e.visible_to(team) is not True or not HERO.fullmatch(e.attacker_name or "") or str(e.source) != "s2_direct"):
        return None
    actor, spell = e.attacker_name, e.inflictor_name or ""
    if spell and not KEY.fullmatch(spell):
        return None
    if typ in ("ABILITY", "ITEM") and spell:
        # In real S2 ABILITY records both team fields are often absent even with an explicit visibility bit.
        # Use only a previously established enemy roster LABEL in that case, never hidden entity state.
        if e.attacker_team != 5 - team and not (e.attacker_team is None and actor in opponent_labels):
            return None
        return ["cast", e.tick, actor, spell, e.ability_level if 1 <= e.ability_level <= 30 and typ == "ABILITY" else None,
                "recorded-team" if e.attacker_team is not None else "roster-label"]
    if e.attacker_team != 5 - team or e.target_team != team or not HERO.fullmatch(e.target_name or ""):
        return None
    if typ == "DAMAGE" and e.target_is_hero_present and e.target_is_hero and 0 < e.value < 1_000_000:
        # For the focal body, never combine incoming damage to an image / unknown actor with its own HP.
        body = e.target_name == own and e.target_is_illusion_present and not e.target_is_illusion
        return ["hit", e.tick, actor, spell, e.target_name, e.value,
                e.damage_type if e.damage_type in ("magical", "physical", "pure") else "unknown", bool(body)]
    if (typ in ("MODIFIER_ADD", "MODIFIER_REMOVE") and e.target_name == own and e.target_is_hero_present and
            e.target_is_hero and e.target_is_illusion_present and not e.target_is_illusion and spell.startswith("modifier_")):
        stun = e.stun_duration
        return ["add" if typ == "MODIFIER_ADD" else "remove", e.tick, actor, spell,
                round(stun, 3) if type(stun) in (int, float) and math.isfinite(stun) and 0 < stun < 120 else None]
    return None


def visible_sheet(looks, state_of):
    visible = [u for u in looks if state_of(u) == "visible"]
    if len(visible) != 1 or visible[0].get("m_lifeState") != 0:
        return None
    # Only the single visible appearance's public HUD stats. It may still be an image.
    level, mana = visible[0].get("m_iCurrentLevel"), visible[0].get("m_flMana")
    return [level if type(level) is int and 1 <= level <= 30 else None,
            math.floor(mana) if type(mana) in (float, int) and math.isfinite(mana) and mana >= 0 else None]


def own_items(parser, hero):
    names = parser.string_tables.get_by_name("EntityNames")
    rows = []
    for i in [*range(6), 15, 16]:
        handle = hero.get(f"m_hItems.{i:04d}")
        item = parser.entity_manager.find_by_handle(handle) if handle not in (None, 0xffffff) else None
        if item is None or names is None:
            continue
        for field in ("m_pEntity.m_nameStringTableIndex", "m_pEntity.m_nameStringableIndex"):
            index = item.get(field)
            name = names.items[index][0] if index in names.items else None
            if name and KEY.fullmatch(name) and name.startswith("item_"):
                charges, cd = item.get("m_iCurrentCharges"), item.get("m_fCooldown")
                rows.append([i, name, charges if type(charges) is int and 0 <= charges <= 65535 else None,
                             round(cd, 3) if type(cd) in (float, int) and math.isfinite(cd) and cd >= 0 else None])
                break
    return rows
