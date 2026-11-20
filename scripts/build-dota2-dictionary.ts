import fs from 'node:fs/promises';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const __dirname = path.dirname(fileURLToPath(import.meta.url));
const ROOT_DIR = path.resolve(__dirname, '..');

// Comprehensive EN abbreviations + Chinese player spoken nicknames for all 127 DOTA2 heroes
const HERO_SLANG: Record<string, { enAliases?: string[]; zhSlang?: string[] }> = {
  npc_dota_hero_antimage: { enAliases: ['AM'], zhSlang: ['敌法', 'AM'] },
  npc_dota_hero_axe: { zhSlang: ['斧王'] },
  npc_dota_hero_bane: { zhSlang: ['痛苦之源', '祸乱'] },
  npc_dota_hero_bloodseeker: { enAliases: ['BS', 'Blood'], zhSlang: ['血魔', 'BS'] },
  npc_dota_hero_crystal_maiden: { enAliases: ['CM', 'Rylai'], zhSlang: ['冰女', 'CM'] },
  npc_dota_hero_drow_ranger: { enAliases: ['Drow', 'Traxex'], zhSlang: ['小黑', '卓尔'] },
  npc_dota_hero_earthshaker: { enAliases: ['ES', 'Shaker'], zhSlang: ['小牛', '牛头', '撼地神牛'] },
  npc_dota_hero_juggernaut: { enAliases: ['Jugg', 'Jug'], zhSlang: ['剑圣', 'JUGG'] },
  npc_dota_hero_mirana: { enAliases: ['PotM'], zhSlang: ['白虎', 'POM'] },
  npc_dota_hero_morphling: { enAliases: ['Morph'], zhSlang: ['水人'] },
  npc_dota_hero_nevermore: { enAliases: ['SF'], zhSlang: ['影魔', 'SF'] },
  npc_dota_hero_phantom_lancer: { enAliases: ['PL'], zhSlang: ['猴子', '幻刺长矛', 'PL'] },
  npc_dota_hero_puck: { zhSlang: ['精灵龙', '帕克'] },
  npc_dota_hero_pudge: { zhSlang: ['屠夫', '胖子'] },
  npc_dota_hero_razor: { zhSlang: ['电魂', '电棍'] },
  npc_dota_hero_sand_king: { enAliases: ['SK'], zhSlang: ['沙王', 'SK'] },
  npc_dota_hero_storm_spirit: { enAliases: ['Storm'], zhSlang: ['蓝猫'] },
  npc_dota_hero_sven: { zhSlang: ['斯温', '流浪剑客'] },
  npc_dota_hero_tiny: { zhSlang: ['小小', '山岭巨人'] },
  npc_dota_hero_vengefulspirit: { enAliases: ['VS', 'Venge'], zhSlang: ['VS', '复仇'] },
  npc_dota_hero_windrunner: { enAliases: ['WR', 'Wind'], zhSlang: ['风行', 'WR'] },
  npc_dota_hero_zuus: { zhSlang: ['宙斯'] },
  npc_dota_hero_kunkka: { zhSlang: ['船长', '昆卡'] },
  npc_dota_hero_lina: { zhSlang: ['火女', '莉娜'] },
  npc_dota_hero_lion: { zhSlang: ['莱恩', '恶魔巫师'] },
  npc_dota_hero_shadow_shaman: { enAliases: ['SS', 'Rhasta'], zhSlang: ['小Y', '暗影萨满'] },
  npc_dota_hero_slardar: { zhSlang: ['大鱼', '大鱼人'] },
  npc_dota_hero_tidehunter: { enAliases: ['Tide'], zhSlang: ['潮汐'] },
  npc_dota_hero_witch_doctor: { enAliases: ['WD'], zhSlang: ['巫医', 'WD'] },
  npc_dota_hero_lich: { zhSlang: ['巫妖'] },
  npc_dota_hero_riki: { enAliases: ['SA'], zhSlang: ['隐刺', '力丸', 'SA'] },
  npc_dota_hero_enigma: { zhSlang: ['谜团'] },
  npc_dota_hero_tinker: { enAliases: ['TK'], zhSlang: ['TK', '修补匠'] },
  npc_dota_hero_sniper: { zhSlang: ['火枪', '矮子'] },
  npc_dota_hero_necrolyte: { enAliases: ['Necro'], zhSlang: ['NEC', '死灵法', '瘟疫法师'] },
  npc_dota_hero_warlock: { enAliases: ['WL'], zhSlang: ['术士'] },
  npc_dota_hero_beastmaster: { enAliases: ['BM', 'Beast'], zhSlang: ['兽王'] },
  npc_dota_hero_queenofpain: { enAliases: ['QoP', 'Queen'], zhSlang: ['女王', '痛苦女王', 'QOP'] },
  npc_dota_hero_venomancer: { enAliases: ['Veno'], zhSlang: ['剧毒'] },
  npc_dota_hero_faceless_void: { enAliases: ['Void', 'FV'], zhSlang: ['虚空', 'FV'] },
  npc_dota_hero_skeleton_king: { enAliases: ['WK', 'Leoric'], zhSlang: ['骷髅王', '冥魂大帝', 'WK'] },
  npc_dota_hero_death_prophet: { enAliases: ['DP', 'Krob'], zhSlang: ['DP', '死亡先知'] },
  npc_dota_hero_phantom_assassin: { enAliases: ['PA', 'Mortred'], zhSlang: ['PA', '幻刺'] },
  npc_dota_hero_pugna: { zhSlang: ['骨法'] },
  npc_dota_hero_templar_assassin: { enAliases: ['TA', 'Lanaya'], zhSlang: ['TA', '圣堂'] },
  npc_dota_hero_viper: { zhSlang: ['毒龙', 'VIP'] },
  npc_dota_hero_luna: { zhSlang: ['月骑', '露娜'] },
  npc_dota_hero_dragon_knight: { enAliases: ['DK'], zhSlang: ['龙骑', 'DK'] },
  npc_dota_hero_dazzle: { zhSlang: ['暗牧', '戴泽'] },
  npc_dota_hero_rattletrap: { enAliases: ['Clock'], zhSlang: ['发条'] },
  npc_dota_hero_leshrac: { enAliases: ['Lesh'], zhSlang: ['老鹿', '拉席克', 'TS'] },
  npc_dota_hero_furion: { enAliases: ['NP', 'Furion', 'Prophet'], zhSlang: ['先知', 'NP'] },
  npc_dota_hero_life_stealer: { enAliases: ['LS', 'Naix'], zhSlang: ['小狗', '噬魂鬼', 'LS'] },
  npc_dota_hero_dark_seer: { enAliases: ['DS'], zhSlang: ['黑贤', '兔子', 'DS'] },
  npc_dota_hero_clinkz: { zhSlang: ['小骷髅', '骨弓'] },
  npc_dota_hero_omniknight: { enAliases: ['Omni'], zhSlang: ['全能'] },
  npc_dota_hero_enchantress: { enAliases: ['Ench'], zhSlang: ['小鹿'] },
  npc_dota_hero_huskar: { zhSlang: ['神灵', '哈斯卡'] },
  npc_dota_hero_night_stalker: { enAliases: ['NS', 'Balanar'], zhSlang: ['夜魔', 'NS'] },
  npc_dota_hero_broodmother: { enAliases: ['Brood'], zhSlang: ['蜘蛛'] },
  npc_dota_hero_bounty_hunter: { enAliases: ['BH', 'Gondar'], zhSlang: ['赏金', 'BH'] },
  npc_dota_hero_weaver: { zhSlang: ['蚂蚁'] },
  npc_dota_hero_jakiro: { enAliases: ['THD'], zhSlang: ['双头龙'] },
  npc_dota_hero_batrider: { enAliases: ['Bat'], zhSlang: ['蝙蝠'] },
  npc_dota_hero_chen: { zhSlang: ['陈'] },
  npc_dota_hero_spectre: { enAliases: ['Spec'], zhSlang: ['幽鬼', 'SPE'] },
  npc_dota_hero_ancient_apparition: { enAliases: ['AA'], zhSlang: ['冰魂', 'AA'] },
  npc_dota_hero_doom_bringer: { zhSlang: ['末日', 'DOOM'] },
  npc_dota_hero_ursa: { zhSlang: ['拍拍', '拍拍熊'] },
  npc_dota_hero_spirit_breaker: { enAliases: ['SB', 'Bara'], zhSlang: ['白牛', 'SB'] },
  npc_dota_hero_gyrocopter: { enAliases: ['Gyro'], zhSlang: ['飞机'] },
  npc_dota_hero_alchemist: { enAliases: ['Alch'], zhSlang: ['炼金', 'GA'] },
  npc_dota_hero_invoker: { enAliases: ['Invo', 'Carl'], zhSlang: ['卡尔'] },
  npc_dota_hero_silencer: { zhSlang: ['沉默'] },
  npc_dota_hero_obsidian_destroyer: { enAliases: ['OD'], zhSlang: ['黑鸟', 'OD'] },
  npc_dota_hero_lycan: { zhSlang: ['狼人'] },
  npc_dota_hero_brewmaster: { enAliases: ['Brew', 'Panda'], zhSlang: ['酒仙', '熊猫'] },
  npc_dota_hero_shadow_demon: { enAliases: ['SD'], zhSlang: ['毒狗', 'SD'] },
  npc_dota_hero_lone_druid: { enAliases: ['LD', 'Sylla'], zhSlang: ['德鲁伊', '熊德', 'LD'] },
  npc_dota_hero_chaos_knight: { enAliases: ['CK'], zhSlang: ['混沌', 'CK'] },
  npc_dota_hero_meepo: { zhSlang: ['米波', '地卜师'] },
  npc_dota_hero_treant: { enAliases: ['Treant', 'Tree'], zhSlang: ['大树'] },
  npc_dota_hero_ogre_magi: { enAliases: ['Ogre'], zhSlang: ['蓝胖'] },
  npc_dota_hero_undying: { enAliases: ['Dirge'], zhSlang: ['尸王'] },
  npc_dota_hero_rubick: { zhSlang: ['拉比克'] },
  npc_dota_hero_disruptor: { enAliases: ['Thrall'], zhSlang: ['萨尔'] },
  npc_dota_hero_nyx_assassin: { enAliases: ['Nyx', 'NA'], zhSlang: ['小强', 'NA'] },
  npc_dota_hero_naga_siren: { enAliases: ['Naga'], zhSlang: ['小娜迦'] },
  npc_dota_hero_keeper_of_the_light: { enAliases: ['KotL'], zhSlang: ['光法', 'KOTL'] },
  npc_dota_hero_wisp: { enAliases: ['Wisp'], zhSlang: ['小精灵', '精灵'] },
  npc_dota_hero_visage: { zhSlang: ['死灵龙', '维萨吉'] },
  npc_dota_hero_slark: { zhSlang: ['小鱼', '小鱼人'] },
  npc_dota_hero_medusa: { enAliases: ['Dusa'], zhSlang: ['美杜莎', '一姐', '大娜迦'] },
  npc_dota_hero_troll_warlord: { enAliases: ['Troll'], zhSlang: ['巨魔'] },
  npc_dota_hero_centaur: { enAliases: ['Cent', 'CW'], zhSlang: ['人马'] },
  npc_dota_hero_magnataur: { enAliases: ['Mag', 'RP'], zhSlang: ['猛犸'] },
  npc_dota_hero_shredder: { enAliases: ['Timber'], zhSlang: ['伐木机'] },
  npc_dota_hero_bristleback: { enAliases: ['BB', 'Bristle'], zhSlang: ['钢背'] },
  npc_dota_hero_tusk: { zhSlang: ['海民'] },
  npc_dota_hero_skywrath_mage: { enAliases: ['Sky'], zhSlang: ['天怒'] },
  npc_dota_hero_abaddon: { enAliases: ['Aba', 'LoA'], zhSlang: ['亚巴顿', 'LOA'] },
  npc_dota_hero_elder_titan: { enAliases: ['ET'], zhSlang: ['大牛', 'ET'] },
  npc_dota_hero_legion_commander: { enAliases: ['LC', 'Legion'], zhSlang: ['军团', 'LC'] },
  npc_dota_hero_techies: { zhSlang: ['炸弹人'] },
  npc_dota_hero_ember_spirit: { enAliases: ['Ember'], zhSlang: ['火猫'] },
  npc_dota_hero_earth_spirit: { enAliases: ['Earth'], zhSlang: ['土猫'] },
  npc_dota_hero_abyssal_underlord: { enAliases: ['Pitlord'], zhSlang: ['大屁股', '孽主'] },
  npc_dota_hero_terrorblade: { enAliases: ['TB'], zhSlang: ['TB', '魂守'] },
  npc_dota_hero_phoenix: { enAliases: ['Birb'], zhSlang: ['凤凰'] },
  npc_dota_hero_oracle: { zhSlang: ['神谕'] },
  npc_dota_hero_winter_wyvern: { enAliases: ['WW', 'Wyvern'], zhSlang: ['冰龙', 'WW'] },
  npc_dota_hero_arc_warden: { enAliases: ['Arc', 'Zet'], zhSlang: ['电狗'] },
  npc_dota_hero_monkey_king: { enAliases: ['MK'], zhSlang: ['大圣', '齐天大圣', 'MK'] },
  npc_dota_hero_dark_willow: { enAliases: ['DW', 'Willow', 'Mireska'], zhSlang: ['花仙子', '小仙女'] },
  npc_dota_hero_pangolier: { enAliases: ['Pango'], zhSlang: ['滚滚'] },
  npc_dota_hero_grimstroke: { enAliases: ['Grim'], zhSlang: ['墨客'] },
  npc_dota_hero_hoodwink: { enAliases: ['Hood'], zhSlang: ['小松鼠', '松鼠'] },
  npc_dota_hero_void_spirit: { enAliases: ['Inai'], zhSlang: ['紫猫'] },
  npc_dota_hero_snapfire: { enAliases: ['Snap'], zhSlang: ['老奶奶'] },
  npc_dota_hero_mars: { zhSlang: ['玛尔斯'] },
  npc_dota_hero_dawnbreaker: { enAliases: ['DB', 'Valora'], zhSlang: ['锤妹', '大锤'] },
  npc_dota_hero_marci: { zhSlang: ['玛西'] },
  npc_dota_hero_primal_beast: { enAliases: ['PB', 'Beast'], zhSlang: ['兽'] },
  npc_dota_hero_muerta: { zhSlang: ['奶绿', '琼英碧灵'] },
  npc_dota_hero_ringmaster: { enAliases: ['Ring'], zhSlang: ['百戏大王', '马戏团'] },
  npc_dota_hero_kez: { zhSlang: ['凯', '鸟人'] },
  npc_dota_hero_largo: { zhSlang: ['朗戈', '青蛙', '蛤蟆'] },
};

// High-frequency item abbreviations & Chinese player slang
const ITEM_SLANG: Record<string, { enAliases?: string[]; zhSlang?: string[] }> = {
  item_blink: { enAliases: ['Blink'], zhSlang: ['跳刀'] },
  item_overwhelming_blink: { enAliases: ['Str Blink'], zhSlang: ['力量跳刀', '大跳刀'] },
  item_swift_blink: { enAliases: ['Agi Blink'], zhSlang: ['敏捷跳刀'] },
  item_arcane_blink: { enAliases: ['Int Blink'], zhSlang: ['智力跳刀'] },
  item_black_king_bar: { enAliases: ['BKB'], zhSlang: ['BKB', '黑皇'] },
  item_ultimate_scepter: { enAliases: ['Aghs', 'Scepter'], zhSlang: ['A杖', '蓝杖'] },
  item_aghanims_shard: { enAliases: ['Shard'], zhSlang: ['魔晶'] },
  item_refresher: { enAliases: ['Refresher'], zhSlang: ['刷新球', '刷新'] },
  item_sheepstick: { enAliases: ['Hex', 'Sheep', 'Vyse'], zhSlang: ['羊刀'] },
  item_orchid: { enAliases: ['Orchid'], zhSlang: ['紫怨'] },
  item_bloodthorn: { zhSlang: ['血棘', '大紫怨'] },
  item_nullifier: { zhSlang: ['否决'] },
  item_sphere: { enAliases: ['Linkens', "Linken's"], zhSlang: ['林肯'] },
  item_lotus_orb: { enAliases: ['Lotus'], zhSlang: ['莲花', '清莲宝珠'] },
  item_force_staff: { enAliases: ['Force'], zhSlang: ['推推', '推推棒'] },
  item_hurricane_pike: { enAliases: ['Pike'], zhSlang: ['大推推', '飓风长戟'] },
  item_cyclone: { enAliases: ['Euls', "Eul's"], zhSlang: ['吹风', '小吹风'] },
  item_wind_waker: { zhSlang: ['大吹风'] },
  item_glimmer_cape: { enAliases: ['Glimmer'], zhSlang: ['微光'] },
  item_aeon_disk: { enAliases: ['Aeon'], zhSlang: ['盘子', '保命盘'] },
  item_ghost: { enAliases: ['Ghost'], zhSlang: ['绿杖'] },
  item_ethereal_blade: { enAliases: ['E-Blade', 'Eblade'], zhSlang: ['虚灵刀'] },
  item_dagon_5: { enAliases: ['Dagon'], zhSlang: ['红杖'] },
  item_manta: { enAliases: ['Manta'], zhSlang: ['分身斧', '分身'] },
  item_bfury: { enAliases: ['BF', 'Fury'], zhSlang: ['狂战', '狂战斧'] },
  item_hand_of_midas: { enAliases: ['Midas'], zhSlang: ['点金', '点金手'] },
  item_maelstrom: { zhSlang: ['小电锤', '电锤'] },
  item_mjollnir: { zhSlang: ['大电锤'] },
  item_gungir: { enAliases: ['Gleipnir', 'Atos Upgrade'], zhSlang: ['缚灵索', '电锤网'] },
  item_rod_of_atos: { enAliases: ['Atos'], zhSlang: ['阿托斯', '根须'] },
  item_skadi: { enAliases: ['Skadi'], zhSlang: ['冰眼'] },
  item_satanic: { zhSlang: ['撒旦'] },
  item_greater_crit: { enAliases: ['Daedalus', 'Crit'], zhSlang: ['大炮'] },
  item_lesser_crit: { enAliases: ['Crystalys'], zhSlang: ['小暴击', '水晶剑'] },
  item_monkey_king_bar: { enAliases: ['MKB'], zhSlang: ['金箍棒', 'MKB'] },
  item_butterfly: { enAliases: ['Butter'], zhSlang: ['蝴蝶'] },
  item_radiance: { enAliases: ['Rad'], zhSlang: ['辉耀'] },
  item_desolator: { enAliases: ['Deso'], zhSlang: ['黯灭', '暗灭'] },
  item_assault: { enAliases: ['AC', 'Cuirass'], zhSlang: ['强袭'] },
  item_shivas_guard: { enAliases: ['Shivas', "Shiva's"], zhSlang: ['希瓦', '冰甲'] },
  item_pipe: { enAliases: ['Pipe'], zhSlang: ['笛子'] },
  item_crimson_guard: { enAliases: ['Crimson'], zhSlang: ['赤红甲'] },
  item_blade_mail: { enAliases: ['BM'], zhSlang: ['刃甲'] },
  item_heart: { enAliases: ['Heart'], zhSlang: ['龙心'] },
  item_abyssal_blade: { enAliases: ['Abyssal'], zhSlang: ['大晕锤'] },
  item_basher: { enAliases: ['Basher'], zhSlang: ['晕锤', '小晕锤'] },
  item_heavens_halberd: { enAliases: ['Halberd'], zhSlang: ['天堂'] },
  item_diffusal_blade: { enAliases: ['Diffusal', 'Diffu'], zhSlang: ['散失', '小散失'] },
  item_disperser: { zhSlang: ['大散失', '散魂剑'] },
  item_sange_and_yasha: { enAliases: ['SnY', 'S&Y'], zhSlang: ['双刀', '散夜对剑'] },
  item_kaya_and_sange: { enAliases: ['KnS', 'K&S'], zhSlang: ['散慧', '法系双刀'] },
  item_yasha_and_kaya: { enAliases: ['YnK', 'Y&K'], zhSlang: ['慧夜', '夜慧对剑'] },
  item_armlet: { enAliases: ['Armlet'], zhSlang: ['臂章'] },
  item_mask_of_madness: { enAliases: ['MoM'], zhSlang: ['疯脸'] },
  item_echo_sabre: { enAliases: ['Echo'], zhSlang: ['连击刀'] },
  item_harpoon: { zhSlang: ['鱼叉'] },
  item_invis_sword: { enAliases: ['Shadow Blade', 'SB'], zhSlang: ['隐刀', '小隐刀'] },
  item_silver_edge: { enAliases: ['Silver Edge', 'SE'], zhSlang: ['大隐刀'] },
  item_octarine_core: { enAliases: ['Octarine'], zhSlang: ['玲珑心'] },
  item_spirit_vessel: { enAliases: ['Vessel'], zhSlang: ['大骨灰'] },
  item_urn_of_shadows: { enAliases: ['Urn'], zhSlang: ['小骨灰', '骨灰盒'] },
  item_solar_crest: { enAliases: ['Solar', 'Crest'], zhSlang: ['大勋章', '炎阳'] },
  item_vladmir: { enAliases: ['Vlads', "Vlad's"], zhSlang: ['祭品'] },
  item_ancient_janggo: { enAliases: ['Drums'], zhSlang: ['战鼓'] },
  item_boots_of_bearing: { enAliases: ['Bearing'], zhSlang: ['战鼓鞋', '宽容之靴'] },
  item_power_treads: { enAliases: ['Treads', 'PT'], zhSlang: ['假腿'] },
  item_phase_boots: { enAliases: ['Phase'], zhSlang: ['相位', '相位鞋'] },
  item_arcane_boots: { enAliases: ['Arcanes', 'Mana Boots'], zhSlang: ['秘法', '秘法鞋'] },
  item_tranquil_boots: { enAliases: ['Tranquils'], zhSlang: ['绿鞋'] },
  item_guardian_greaves: { enAliases: ['Greaves', 'GG Boots'], zhSlang: ['大鞋', '卫士胫甲'] },
  item_travel_boots: { enAliases: ['BoTs', 'Travels'], zhSlang: ['飞鞋'] },
  item_travel_boots_2: { enAliases: ['BoTs 2'], zhSlang: ['大飞鞋', '二级飞鞋'] },
  item_tpscroll: { enAliases: ['TP'], zhSlang: ['TP', '回城'] },
  item_ward_observer: { enAliases: ['Obs', 'Observer'], zhSlang: ['假眼'] },
  item_ward_sentry: { enAliases: ['Sentry', 'Sentries'], zhSlang: ['真眼'] },
  item_dust: { enAliases: ['Dust'], zhSlang: ['粉', '显影之尘'] },
  item_smoke_of_deceit: { enAliases: ['Smoke'], zhSlang: ['雾', '诡计之雾'] },
  item_gem: { enAliases: ['Gem'], zhSlang: ['宝石', '真视宝石'] },
  item_blood_grenade: { enAliases: ['Grenade'], zhSlang: ['血腥榴弹', '榴弹'] },
  item_flask: { enAliases: ['Salve'], zhSlang: ['大药', '药膏'] },
  item_tango: { zhSlang: ['吃树'] },
  item_clarity: { zhSlang: ['小蓝', '净化'] },
  item_enchanted_mango: { enAliases: ['Mango'], zhSlang: ['芒果'] },
  item_magic_stick: { enAliases: ['Stick'], zhSlang: ['小魔棒'] },
  item_magic_wand: { enAliases: ['Wand'], zhSlang: ['大魔棒'] },
  item_bottle: { zhSlang: ['瓶子', '魔瓶'] },
  item_infused_raindrop: { enAliases: ['Raindrops'], zhSlang: ['魂泪'] },
  item_faerie_fire: { zhSlang: ['仙灵火'] },
  item_bracer: { zhSlang: ['护腕'] },
  item_wraith_band: { zhSlang: ['系带', '怨灵系带'] },
  item_null_talisman: { enAliases: ['Null'], zhSlang: ['挂件', '空灵挂件'] },
  item_soul_ring: { zhSlang: ['魂戒'] },
  item_falcon_blade: { zhSlang: ['猎鹰战刃', '猎鹰刀'] },
  item_mage_slayer: { zhSlang: ['法师克星', '法克'] },
  item_phylactery: { zhSlang: ['灵匣'] },
  item_khanda: { zhSlang: ['坎达', '大灵匣'] },
  item_parasma: { zhSlang: ['帕拉斯玛', '大巫师之刃'] },
  item_witch_blade: { zhSlang: ['巫师之刃'] },
  item_eternal_shroud: { enAliases: ['Shroud'], zhSlang: ['永世法衣', '法衣'] },
  item_vanguard: { zhSlang: ['先锋盾'] },
  item_holy_locket: { enAliases: ['Locket'], zhSlang: ['圣洁吊坠', '奶棒'] },
  item_mekansm: { enAliases: ['Mek'], zhSlang: ['梅肯'] },
  item_pavise: { zhSlang: ['长盾', '小护盾'] },
  item_aether_lens: { enAliases: ['Aether', 'Lens'], zhSlang: ['以太', '以太之镜'] },
  item_kaya: { zhSlang: ['慧光'] },
  item_sange: { zhSlang: ['散华'] },
  item_yasha: { zhSlang: ['夜叉'] },
  item_moon_shard: { zhSlang: ['银月', '银月之晶'] },
  item_rapier: { enAliases: ['Rapier', 'Divine'], zhSlang: ['圣剑'] },
  item_cheese: { zhSlang: ['奶酪'] },
  item_aegis: { zhSlang: ['不朽盾', '盾'] },
  item_refresher_shard: { zhSlang: ['刷新碎片', '刷新球碎片'] },
  item_roshan_banner: { enAliases: ['Banner'], zhSlang: ['肉山战旗', '旗子'] },
  item_bloodstone: { enAliases: ['Bloodstone'], zhSlang: ['血精', '血精石'] },
  item_revenants_brooch: { enAliases: ['Brooch'], zhSlang: ['亡魂胸针', '胸针'] },
  item_orb_of_corrosion: { enAliases: ['Corrosion', 'OoC'], zhSlang: ['腐蚀之球', '大毒球'] },
  item_orb_of_venom: { enAliases: ['OoV'], zhSlang: ['淬毒之珠', '小毒球'] },
  item_blight_stone: { zhSlang: ['枯萎之石', '减甲球'] },
  item_quelling_blade: { enAliases: ['QB', 'Quelling'], zhSlang: ['补刀斧'] },
  item_branches: { enAliases: ['Branch', 'GG Branch'], zhSlang: ['树枝'] },
  item_veil_of_discord: { enAliases: ['Veil'], zhSlang: ['纷争', '纷争面纱'] },
  item_helm_of_the_dominator: { enAliases: ['Dom', 'HotD'], zhSlang: ['小支配', '支配头盔'] },
  item_helm_of_the_overlord: { enAliases: ['Overlord'], zhSlang: ['大支配', '统御头盔'] },
  item_meteor_hammer: { enAliases: ['Meteor Hammer', 'Meme Hammer'], zhSlang: ['陨星锤'] },
  item_dagon: { enAliases: ['Dagon 1'], zhSlang: ['一级红杖', '红杖'] },
  // 7.42 items. Bloodslinger, Ristul's Devotion, Skypiercer and Starforge have no Chinese name in the
  // datafeed or the client yet (the Chinese client shows them in English); these nicknames are the
  // client's own Chinese shop-search aliases (DOTA_SearchAlias_Ability_*).
  item_caduceus: { zhSlang: ['大灵匣'] },
  item_berserkers_sabatons: { zhSlang: ['大相位', '大臂章'] },
  item_starforge: { zhSlang: ['大陨星锤'] },
};

export interface TacticalTerm {
  category:
    | '对线与野区 (Laning & Jungle)'
    | '视野与游走 (Vision & Rotations)'
    | '地图资源与建筑 (Objectives & Map)'
    | '团战与状态机制 (Teamfight & Mechanics)'
    | '位置与分路运营 (Roles & Macro)';
  en: string;
  enAliases: string[];
  zh: string;
  zhAliases: string[];
  note?: string;
}

export const TACTICAL_TERMS: TacticalTerm[] = [
  // Laning & Jungle
  {
    category: '对线与野区 (Laning & Jungle)',
    en: 'stack / stack camp',
    enAliases: ['stacking', 'stack neutral camp', 'double stack', 'triple stack'],
    zh: '堆野 / 屯野',
    zhAliases: ['拉双野', '屯双野', '叠野', '堆双野'],
    note: 'Never translate stack camp as 叠营地; always use 堆野 or 屯野.',
  },
  {
    category: '对线与野区 (Laning & Jungle)',
    en: 'stack ancients',
    enAliases: ['ancient stack', 'stack the triangle'],
    zh: '屯远古 / 堆远古',
    zhAliases: ['远古野', '清远古', '刷远古'],
    note: 'Never translate ancients (neutral creeps) as 古树; use 远古 / 远古野.',
  },
  {
    category: '对线与野区 (Laning & Jungle)',
    en: 'pull / pull the small camp',
    enAliases: ['single pull', 'pull wave', 'pull lane'],
    zh: '拉野 / 拉小野',
    zhAliases: ['拉兵线'],
  },
  {
    category: '对线与野区 (Laning & Jungle)',
    en: 'half-pull',
    enAliases: ['partial pull'],
    zh: '半拉野',
    zhAliases: ['拉半波兵'],
  },
  {
    category: '对线与野区 (Laning & Jungle)',
    en: 'chain pull / pull through',
    enAliases: ['connect the pull', 'pull hard camp'],
    zh: '拉双野贯通 / 连拉大野',
    zhAliases: ['拉大野', '接大野'],
  },
  {
    category: '对线与野区 (Laning & Jungle)',
    en: 'block camp / body block camp / sentry block',
    enAliases: ['camp blocked', 'unblock camp'],
    zh: '封野 / 肉身封野 / 真眼封野',
    zhAliases: ['解封野点', '反封野'],
  },
  {
    category: '对线与野区 (Laning & Jungle)',
    en: 'creep block',
    enAliases: ['block the wave', 'block first wave'],
    zh: '卡兵',
    zhAliases: ['卡第一波兵'],
  },
  {
    category: '对线与野区 (Laning & Jungle)',
    en: 'creep aggro / draw aggro / de-aggro',
    enAliases: ['pull aggro', 'drop tower aggro', 'aggro cooldown'],
    zh: '拉兵线仇恨 / A塔消仇恨',
    zhAliases: ['拉仇恨', '转移仇恨', '仇恨CD'],
  },
  {
    category: '对线与野区 (Laning & Jungle)',
    en: 'last hit / CS / deny',
    enAliases: ['creep score', 'secure ranged creep', 'secure flagbearer'],
    zh: '补刀（正补）/ 反补',
    zhAliases: ['补远程兵', '抢旗手兵'],
  },
  {
    category: '对线与野区 (Laning & Jungle)',
    en: 'creep equilibrium / freeze the wave',
    enAliases: ['lane equilibrium', 'hold the wave', 'static the lane'],
    zh: '控线 / 把兵线控在塔前',
    zhAliases: ['兵线平衡', '控住线'],
  },
  {
    category: '对线与野区 (Laning & Jungle)',
    en: 'shove the wave / push out the lane',
    enAliases: ['nuke the wave', 'clear the wave'],
    zh: '推线 / 清线 / 把线推出去',
    zhAliases: ['洗兵', '带过去'],
  },
  {
    category: '对线与野区 (Laning & Jungle)',
    en: 'cut the wave / creep skip',
    enAliases: ['cutting waves', 'drag the wave'],
    zh: '断兵线 / 断线 / 勾兵',
    zhAliases: ['绕后断兵'],
  },
  {
    category: '对线与野区 (Laning & Jungle)',
    en: 'double wave',
    enAliases: ['double waving'],
    zh: '囤双倍兵线进塔',
    zhAliases: ['两波兵进塔'],
  },
  {
    category: '对线与野区 (Laning & Jungle)',
    en: 'trade hits / harass / zone out',
    enAliases: ['right-click trade', 'zoning', 'out-trade'],
    zh: '换血 / 消耗 / 压出经验区',
    zhAliases: ['点人', '白嫖血量', '吃不到经验'],
  },
  {
    category: '对线与野区 (Laning & Jungle)',
    en: 'catapult wave / siege creep',
    enAliases: ['5-minute catapult', 'wagon wave'],
    zh: '投石车兵线 / 攻城车',
    zhAliases: ['车子波推进'],
  },

  // Vision & Rotations
  {
    category: '视野与游走 (Vision & Rotations)',
    en: 'deward / dewarding',
    enAliases: ['clear vision', 'kill their obs', 'cliff ward'],
    zh: '反眼 / 排眼',
    zhAliases: ['排视野', '拆真假眼', '高台眼'],
  },
  {
    category: '视野与游走 (Vision & Rotations)',
    en: 'ward / deep ward / aggressive vision / defensive vision',
    enAliases: ['plant obs', 'drop sentry', 'rosh ward'],
    zh: '插眼 / 深入进攻眼 / 保护眼',
    zhAliases: ['做视野', '铺真眼', '肉山眼'],
  },
  {
    category: '视野与游走 (Vision & Rotations)',
    en: 'pop smoke / smoke gank / break smoke',
    enAliases: ['smoke up', 'smoke broke', 'wrap around with smoke'],
    zh: '开雾 / 开雾抓人 / 破雾',
    zhAliases: ['诡计之雾绕后', '雾破了'],
  },
  {
    category: '视野与游走 (Vision & Rotations)',
    en: 'carry dust / pop dust',
    enAliases: ['bring detection', 'no detection'],
    zh: '带粉 / 撒粉',
    zhAliases: ['带反隐', '没反隐'],
  },
  {
    category: '视野与游走 (Vision & Rotations)',
    en: 'missing (mia / ss) / showing on wave',
    enAliases: ['mid missing', 'heroes off map', 'nobody showing'],
    zh: '不见了（Miss）/ 露线了',
    zhAliases: ['中路miss', '地图全黑没人露头'],
  },
  {
    category: '视野与游走 (Vision & Rotations)',
    en: 'gank / rotate / TP rotation / counter-gank',
    enAliases: ['TP in', 'backup mid', 'sit behind core'],
    zh: '抓人（Gank）/ 游走支援 / TP支援 / 反蹲',
    zhAliases: ['蹲大哥', 'T过去救'],
  },
  {
    category: '视野与游走 (Vision & Rotations)',
    en: 'tower dive',
    enAliases: ['diving T1', 'dive behind tower'],
    zh: '冲塔 / 越塔强杀',
    zhAliases: ['越一塔'],
  },
  {
    category: '视野与游走 (Vision & Rotations)',
    en: 'Twin Gate / portal rotation',
    enAliases: ['take the gate', 'portal gank'],
    zh: '双生门（传送门）支援',
    zhAliases: ['坐传送门抓边路'],
  },

  // Objectives & Map
  {
    category: '地图资源与建筑 (Objectives & Map)',
    en: 'Roshan / Rosh / Rosh timer / Aegis timing',
    enAliases: ['check Rosh', 'sneak Rosh', 'contest Roshan'],
    zh: '肉山 / 肉山刷新时间 / 带盾节奏',
    zhAliases: ['偷肉山', '看肉山', '接肉山团'],
  },
  {
    category: '地图资源与建筑 (Objectives & Map)',
    en: 'Tormentor',
    enAliases: ['20-minute Tormentor', 'take Tormentor'],
    zh: '痛苦魔方 / 魔方',
    zhAliases: ['打魔方拿魔晶'],
  },
  {
    category: '地图资源与建筑 (Objectives & Map)',
    en: 'Wisdom Rune / Bounty Rune / Water Rune / Power Rune',
    enAliases: ['7-minute wisdom', 'steal wisdom', '2-minute water rune', '4-minute rune', 'DD', 'Haste', 'Arcane', 'Regen', 'Illusion', 'Invis', 'Shield rune'],
    zh: '智慧神符（经验符）/ 赏金符 / 圣水符（水符）/ 河道强化神符',
    zhAliases: ['抢7分钟经验符', '控水符', '控符', '双倍符', '极速符', '奥术符', '恢复符', '幻象符', '隐身符', '护盾符'],
  },
  {
    category: '地图资源与建筑 (Objectives & Map)',
    en: 'Lotus Pool / Healing Lotus',
    enAliases: ['3-minute lotus', 'contest lotus'],
    zh: '莲花池 / 莲花',
    zhAliases: ['抢3分钟莲花'],
  },
  {
    category: '地图资源与建筑 (Objectives & Map)',
    en: 'Watcher / Outpost',
    enAliases: ['capture watcher', 'turn on watcher'],
    zh: '观察者（灯）/ 前哨',
    zhAliases: ['点灯', '开观察者'],
  },
  {
    category: '地图资源与建筑 (Objectives & Map)',
    en: 'high ground (HG) / siege high ground / defend high ground',
    enAliases: ['go HG', 'don’t force high ground', 'high ground defense'],
    zh: '高地 / 上高 / 守高地',
    zhAliases: ['别硬上高', '磨高地'],
  },
  {
    category: '地图资源与建筑 (Objectives & Map)',
    en: 'T1 / T2 / T3 / T4 tower / barracks (rax) / melee rax / ranged rax / mega creeps / Throne (Ancient)',
    enAliases: ['tier 1', 'tier 2', 'tier 3', 'tier 4', 'expose rax', 'hit throne', 'end the game'],
    zh: '一塔 / 二塔 / 高地塔（三塔）/ 门牙塔（四塔）/ 兵营 / 近战兵营 / 远程兵营 / 超级兵 / 基地（拆基地一波）',
    zhAliases: ['拆一路', '破三路超级兵', '拆门牙拆基地'],
  },
  {
    category: '地图资源与建筑 (Objectives & Map)',
    en: 'Glyph (of Fortification) / Scan',
    enAliases: ['pop glyph', 'multishot glyph', 'use scan'],
    zh: '塔防（加固）/ 扫描',
    zhAliases: ['开塔防清兵', '扫一下肉山'],
  },
  {
    category: '地图资源与建筑 (Objectives & Map)',
    en: 'triangle / dead lane / enemy jungle',
    enAliases: ['farm triangle', 'invade jungle', 'choke point'],
    zh: '三角区 / 死亡路（危险线）/ 对面野区',
    zhAliases: ['刷三角区', '压制野区', '狭窄路口'],
  },

  // Teamfight & Mechanics
  {
    category: '团战与状态机制 (Teamfight & Mechanics)',
    en: 'buyback / no buyback / dieback / save for buyback',
    enAliases: ['BB status', 'forced buyback', 'hold buyback gold'],
    zh: '买活 / 没买活 / 买活死 / 留买活钱',
    zhAliases: ['逼买活', '别花买活钱'],
  },
  {
    category: '团战与状态机制 (Teamfight & Mechanics)',
    en: 'cooldown (CD) / ult on CD / BKB timing / item timing / power spike',
    enAliases: ['play around cooldowns', 'hit timing', 'strong timing'],
    zh: '技能冷却（CD）/ 大招CD / BKB第一波强势期 / 关键装时间点 / 强势期',
    zhAliases: ['等大招CD', '卡关键装节奏'],
  },
  {
    category: '团战与状态机制 (Teamfight & Mechanics)',
    en: 'initiate / jump the backline / counter-initiate / kite / peel / disengage / reset',
    enAliases: ['go on sniper', 'kite BKB', 'back up', 'B!'],
    zh: '先手开团 / 切后排 / 反手 / 拉扯（风筝）/ 保后排 / 拉开脱战 / 重整撤退（B！）',
    zhAliases: ['溜他BKB', '先拉扯一下', '撤'],
  },
  {
    category: '团战与状态机制 (Teamfight & Mechanics)',
    en: 'burst / focus fire / chain stun / hex / silence / mute / break / dispel (basic / strong dispel) / root / leash / taunt',
    enAliases: ['overlap stuns', 'layer control', 'purge it', 'break passive', 'debuff immunity'],
    zh: '爆发秒人 / 集火 / 接控制（连控别重叠）/ 变羊 / 沉默 / 锁物品（Mute）/ 破坏被动（Break）/ 驱散（弱驱散 / 强驱散）/ 缠绕 / 缚灵束缚 / 嘲讽',
    zhAliases: ['减益免疫', '魔免', '解状态'],
  },
  {
    category: '团战与状态机制 (Teamfight & Mechanics)',
    en: 'juke in trees / bait / TP cancel /high-ground miss',
    enAliases: ['cut trees', 'cancel his TP', 'playing fog'],
    zh: '绕树林 / 卖破绽勾引 / 留打断断TP / 高低坡丢失视野',
    zhAliases: ['卡视野', '断他TP'],
  },

  // Roles & Macro
  {
    category: '位置与分路运营 (Roles & Macro)',
    en: 'Pos 1 (Carry / Safe lane) / Pos 2 (Mid) / Pos 3 (Offlane) / Pos 4 (Soft Support) / Pos 5 (Hard Support)',
    enAliases: ['safelaner', 'midlaner', 'offlaner', 'roamer', 'hard five'],
    zh: '一号位（大哥 / 优势路C）/ 二号位（中单）/ 三号位（劣单）/ 四号位（游走辅助）/ 五号位（纯辅助 / 酱油）',
    zhAliases: ['优势路', '中路', '劣势路', '双辅助'],
  },
  {
    category: '位置与分路运营 (Roles & Macro)',
    en: 'split push / rat Dota / 5-man group up / play the map / farm pattern / net worth lead',
    enAliases: ['connect waves', 'make space', 'dodge fights', 'choke the map'],
    zh: '带线牵制 / 偷家带球 / 五人抱团 / 控图压缩空间 / 刷钱路线 / 经济领先',
    zhAliases: ['拉扯空间', '避战刷钱', '把他们堵在高地'],
  },
  {
    category: '地图资源与建筑 (Objectives & Map)',
    en: '1st Rosh / 2nd Rosh / 3rd Rosh / Aegis expires (5 min) / Rosh pit / Radiant / Dire',
    enAliases: ['first Roshan', 'second Roshan', 'third Roshan', 'Aegis reclaim', 'top pit', 'bot pit'],
    zh: '一代肉山 / 二代肉山 / 三代肉山 / 盾消（5分钟到时间）/ 肉山坑 / 天辉 / 夜魇',
    zhAliases: ['上路肉山坑', '下路肉山坑', '盾快消了'],
  },
  {
    category: '团战与状态机制 (Teamfight & Mechanics)',
    en: 'tread switching / armlet toggle / Manta dodge / BKB-TP / Glimmer save / Force save / Lotus dispel / Nullifier counters Ghost-Glimmer-Force-Eul',
    enAliases: ['switch treads to int', 'dodge with Manta', 'BKB TP out', 'pop Lotus'],
    zh: '切假腿 / 切臂章 / 分身躲技能 / BKB回城（魔免TP）/ 微光救人 / 推推救人 / 莲花解状态 / 否决破绿杖微光推推吹风',
    zhAliases: ['切智力腿放技能', '切力量腿撑血'],
  },
  {
    category: '团战与状态机制 (Teamfight & Mechanics)',
    en: 'Facet / Innate ability / Talent tree / Neutral item token / Enchantment / Backpack swap',
    enAliases: ['level 10 talent', 'level 20 talent', 'put branch in backpack', 'swap item cooldown'],
    zh: '命石 / 先天技能 / 天赋树 / 中立道具代币 / 中立附魔 / 切背包（塞背包6秒CD）',
    zhAliases: ['左命石', '右命石', '点天赋'],
  },
  {
    category: '位置与分路运营 (Roles & Macro)',
    en: 'Secret Shop / Courier (crow) / snipe courier / quickbuy / reliable gold / recover in jungle',
    enAliases: ['fly out regen', 'kill courier', 'win lane', 'lose lane', 'lane matchup', 'counter-pick', 'last pick'],
    zh: '神秘商店 / 信使（小鸡）/ 杀鸡 / 快捷购买 / 可靠金钱 / 钻野补经济',
    zhAliases: ['运补给', '线优', '线劣', '对线优劣', '克制选人', '最后一手Counter'],
  },
];

// Iconic abilities with well-known player callout nicknames
const ICONIC_ABILITY_SLANG: Record<string, { enAliases?: string[]; zhSlang?: string[] }> = {
  enigma_black_hole: { enAliases: ['Black Hole', 'Hole'], zhSlang: ['黑洞', '拉大'] },
  tidehunter_ravage: { enAliases: ['Ravage'], zhSlang: ['毁灭', '潮汐踩大'] },
  faceless_void_chronosphere: { enAliases: ['Chrono'], zhSlang: ['时间结界', '罩大'] },
  faceless_void_time_walk: { enAliases: ['Time Walk'], zhSlang: ['时间漫游', '虚空跳回血'] },
  magnataur_reverse_polarity: { enAliases: ['RP'], zhSlang: ['两极反转', '猛犸拱大', 'RP'] },
  magnataur_skewer: { enAliases: ['Skewer'], zhSlang: ['巨角冲撞', '拱回来'] },
  earthshaker_echo_slam: { enAliases: ['Echo', 'Echo Slam'], zhSlang: ['回音击', '跳大'] },
  earthshaker_fissure: { enAliases: ['Fissure'], zhSlang: ['沟壑', '封路F'] },
  mars_arena_of_blood: { enAliases: ['Arena'], zhSlang: ['竞技场', '框大'] },
  mars_spear: { enAliases: ['Spear'], zhSlang: ['战神迅矛', '钉墙'] },
  legion_commander_duel: { enAliases: ['Duel'], zhSlang: ['决斗'] },
  legion_commander_press_the_attack: { enAliases: ['PTA'], zhSlang: ['强攻', '解状态W'] },
  doom_bringer_doom: { enAliases: ['Doom Ult'], zhSlang: ['末日大'] },
  silencer_global_silence: { enAliases: ['Global'], zhSlang: ['全屏沉默', '大招沉默'] },
  pudge_meat_hook: { enAliases: ['Hook'], zhSlang: ['钩子', '出钩'] },
  pudge_dismember: { enAliases: ['Dismember', 'Bite'], zhSlang: ['肢解', '咬住'] },
  mirana_arrow: { enAliases: ['Sacred Arrow', 'Arrow'], zhSlang: ['月神箭', '射箭'] },
  mirana_invis: { enAliases: ['Moonlight Shadow', 'PotM Ult'], zhSlang: ['月之暗面', '白虎大群体隐身'] },
  invoker_sun_strike: { enAliases: ['Sunstrike', 'SS'], zhSlang: ['天火'] },
  invoker_tornado: { zhSlang: ['吹风'] },
  invoker_emp: { enAliases: ['EMP'], zhSlang: ['磁暴'] },
  invoker_chaos_meteor: { enAliases: ['Meteor'], zhSlang: ['陨石'] },
  invoker_deafening_blast: { enAliases: ['Blast'], zhSlang: ['推波', '超震声波'] },
  invoker_cold_snap: { zhSlang: ['急速冷却'] },
  axe_berserkers_call: { enAliases: ['Call', 'Axe Call'], zhSlang: ['吼', '狂战士之吼'] },
  axe_culling_blade: { enAliases: ['Cull', 'Dunk'], zhSlang: ['淘汰斩'] },
  dazzle_shallow_grave: { enAliases: ['Grave'], zhSlang: ['薄葬'] },
  oracle_false_promise: { enAliases: ['Promise'], zhSlang: ['神谕大', '妄诺'] },
  winter_wyvern_winters_curse: { enAliases: ['Curse'], zhSlang: ['寒冬诅咒', '冰龙大'] },
  winter_wyvern_cold_embrace: { enAliases: ['Embrace'], zhSlang: ['冰箱', '极寒之拥'] },
  disruptor_glimpse: { enAliases: ['Glimpse'], zhSlang: ['瞥视', '恶魔瞥视', 'D回去'] },
  disruptor_static_storm: { enAliases: ['Static Storm'], zhSlang: ['萨尔框大'] },
  obsidian_destroyer_astral_imprisonment: { enAliases: ['Astral'], zhSlang: ['星体禁锢', '黑鸟关'] },
  shadow_demon_disruption: { enAliases: ['Disruption'], zhSlang: ['崩裂禁锢', '毒狗关'] },
  lion_voodoo: { enAliases: ['Hex'], zhSlang: ['变羊', '妖术'] },
  lion_finger_of_death: { enAliases: ['Finger'], zhSlang: ['死亡一指', '莱恩大'] },
  lina_laguna_blade: { enAliases: ['Laguna'], zhSlang: ['神灭斩', '火女大'] },
  shadow_shaman_voodoo: { enAliases: ['Hex'], zhSlang: ['变羊', '妖术'] },
  shadow_shaman_shackles: { enAliases: ['Shackles'], zhSlang: ['枷锁', '拉住'] },
  bane_nightmare: { enAliases: ['Nightmare'], zhSlang: ['噩梦', '睡'] },
  bane_fiends_grip: { enAliases: ['Grip'], zhSlang: ['魔爪', '拉大'] },
  batrider_flaming_lasso: { enAliases: ['Lasso'], zhSlang: ['火焰枷锁', '蝙蝠拉'] },
  vengefulspirit_nether_swap: { enAliases: ['Swap'], zhSlang: ['移形换位', 'VS换'] },
  puck_dream_coil: { enAliases: ['Coil', 'Dream Coil'], zhSlang: ['梦境缠绕', '帕克大'] },
  slark_dark_pact: { enAliases: ['Dark Pact'], zhSlang: ['黑暗契约', '小鱼C解状态'] },
  slark_pounce: { enAliases: ['Pounce'], zhSlang: ['突袭', '小鱼跳栓'] },
  slark_shadow_dance: { enAliases: ['Shadow Dance'], zhSlang: ['暗影之舞', '小鱼大'] },
  antimage_counterspell: { enAliases: ['Counterspell'], zhSlang: ['法术反制', '敌法盾反'] },
  antimage_mana_void: { enAliases: ['Mana Void'], zhSlang: ['法力虚空', '敌法大'] },
  phantom_lancer_doppelganger: { enAliases: ['Doppel', 'Doppelganger'], zhSlang: ['神行百变', '猴子W躲技能'] },
  ember_spirit_sleight_of_fist: { enAliases: ['Sleight', 'SoF'], zhSlang: ['无影拳', '火猫T'] },
  storm_spirit_ball_lightning: { enAliases: ['Zip', 'Ball Lightning'], zhSlang: ['球状闪电', '蓝猫滚'] },
  dark_seer_vacuum: { enAliases: ['Vacuum'], zhSlang: ['真空', '黑贤拉墙'] },
  kunkka_x_marks_the_spot: { enAliases: ['X Marks'], zhSlang: ['X标记'] },
  kunkka_ghostship: { enAliases: ['Boat', 'Ghostship'], zhSlang: ['幽灵船', '船油减伤'] },
  tusk_snowball: { enAliases: ['Snowball'], zhSlang: ['雪球', '雪球救人'] },
  tiny_toss: { enAliases: ['Toss'], zhSlang: ['投掷', '丢回来'] },
  terrorblade_metamorphosis: { enAliases: ['Meta'], zhSlang: ['变身', '魔化'] },
  terrorblade_sunder: { enAliases: ['Sunder'], zhSlang: ['换血', '魂断'] },
  juggernaut_blade_fury: { enAliases: ['Spin'], zhSlang: ['剑刃风暴', '转'] },
  juggernaut_omni_slash: { enAliases: ['Omnislash'], zhSlang: ['无敌斩'] },
  life_stealer_rage: { enAliases: ['Rage'], zhSlang: ['狂暴', '魔免'] },
  life_stealer_infest: { enAliases: ['Infest'], zhSlang: ['钻人', '感染'] },
  wisp_relocate: { enAliases: ['Relocate'], zhSlang: ['降临', '带人飞'] },
  spectre_haunt: { enAliases: ['Haunt'], zhSlang: ['鬼影重重', '开大传'] },
  furion_teleportation: { zhSlang: ['传送', '先知飞'] },
  zuus_thundergods_wrath: { enAliases: ['Zeus Ult'], zhSlang: ['雷神之怒', '劈大'] },
  phoenix_supernova: { enAliases: ['Egg', 'Supernova'], zhSlang: ['变蛋', '超新星'] },
  undying_tombstone: { enAliases: ['Tomb', 'Tombstone'], zhSlang: ['墓碑'] },
  shadow_shaman_mass_serpent_ward: { enAliases: ['Wards', 'Serpent Wards'], zhSlang: ['蛇棒'] },
  warlock_rain_of_chaos: { enAliases: ['Rock', 'Golem'], zhSlang: ['砸地狱火'] },
  bounty_hunter_track: { enAliases: ['Track'], zhSlang: ['标记', '追踪术'] },
  slardar_amplify_damage: { enAliases: ['Corrosive Haze', 'Amp'], zhSlang: ['点灯', '减甲'] },
  rubick_spell_steal: { zhSlang: ['偷技能'] },
  morphling_morph_agi: { enAliases: ['Attribute Shift'], zhSlang: ['转属性', '转敏转力'] },
  necrolyte_reapers_scythe: { enAliases: ['Scythe', 'Necro Ult'], zhSlang: ['死神镰刀', '斩'] },
  monkey_king_wukongs_command: { enAliases: ['MK Ring', 'Wukong'], zhSlang: ['猴子猴孙', '大圣摆阵'] },
};

async function fetchJson(url: string, retries = 3): Promise<any> {
  for (let attempt = 1; attempt <= retries; attempt++) {
    try {
      const res = await fetch(url, {
        headers: { 'User-Agent': 'dota2-coach-dictionary-builder/1.0' },
      });
      if (!res.ok) throw new Error(`HTTP ${res.status} for ${url}`);
      return await res.json();
    } catch (err) {
      if (attempt === retries) throw err;
      await new Promise((r) => setTimeout(r, 400 * attempt));
    }
  }
}

async function mapConcurrent<T, R>(
  items: T[],
  concurrency: number,
  fn: (item: T, idx: number) => Promise<R>,
): Promise<R[]> {
  const results = new Array<R>(items.length);
  let cursor = 0;
  async function worker() {
    while (true) {
      const i = cursor++;
      if (i >= items.length) return;
      results[i] = await fn(items[i], i);
    }
  }
  await Promise.all(Array.from({ length: Math.min(concurrency, items.length) }, () => worker()));
  return results;
}

function cleanHtml(s: string): string {
  return s.replace(/<[^>]+>/g, '').replace(/\s+/g, ' ').trim();
}

const ITEM_ZH_OVERRIDES: Record<string, string> = {
  item_black_grimoire: '黑暗魔典（术士）',
};

async function main() {
  console.log('Fetching official Valve DOTA2 hero & item lists (english + schinese)...');
  const [heroListZhRes, itemListZhRes] = await Promise.all([
    fetchJson('https://www.dota2.com/datafeed/herolist?language=schinese'),
    fetchJson('https://www.dota2.com/datafeed/itemlist?language=schinese'),
  ]);

  const rawHeroes: any[] = heroListZhRes.result.data.heroes;
  rawHeroes.sort((a, b) => a.name_english_loc.localeCompare(b.name_english_loc));
  const portraitFiles = new Set(await fs.readdir(path.join(ROOT_DIR, 'public/heroes')).catch(() => []));

  console.log(`Fetching detailed ability rosters for all ${rawHeroes.length} heroes...`);
  const heroes = await mapConcurrent(rawHeroes, 14, async (h) => {
    const [enData, zhData] = await Promise.all([
      fetchJson(`https://www.dota2.com/datafeed/herodata?language=english&hero_id=${h.id}`),
      fetchJson(`https://www.dota2.com/datafeed/herodata?language=schinese&hero_id=${h.id}`),
    ]);
    const hEn = enData.result.data.heroes[0];
    const hZh = zhData.result.data.heroes[0];

    const zhAbilityById = new Map<number, any>();
    for (const ab of hZh.abilities || []) {
      zhAbilityById.set(ab.id, ab);
    }

    const seenAbilities = new Set<string>();
    const abilities = [];
    for (const abEn of hEn.abilities || []) {
      const abZh = zhAbilityById.get(abEn.id);
      const enName = cleanHtml(abEn.name_loc || '');
      const zhName = cleanHtml(abZh?.name_loc || '');
      if (!enName || !zhName || seenAbilities.has(abEn.name)) continue;
      seenAbilities.add(abEn.name);
      const slang = ICONIC_ABILITY_SLANG[abEn.name];
      abilities.push({
        key: abEn.name,
        en: enName,
        zh: zhName,
        isInnate: Boolean(abEn.ability_is_innate),
        isShard: Boolean(abEn.ability_has_shard),
        isScepter: Boolean(abEn.ability_has_scepter),
        enAliases: slang?.enAliases || [],
        zhSlang: slang?.zhSlang || [],
      });
    }

    const slang = HERO_SLANG[h.name] || {};
    const portraitName = h.name.replace(/^npc_dota_hero_/, '');
    const portrait = [`${portraitName}.jpg`, `${portraitName}.png`].find(name => portraitFiles.has(name));
    return {
      id: h.id,
      key: h.name,
      en: cleanHtml(hEn.name_loc),
      zh: cleanHtml(hZh.name_loc),
      enAliases: slang.enAliases || [],
      zhSlang: slang.zhSlang || [],
      abilities,
      primaryAttr: h.primary_attr,
      ...(portrait ? { portrait: `/heroes/${portrait}` } : {}),
    };
  });

  // Deduplicate & structure items
  const rawItems: any[] = itemListZhRes.result.data.itemabilities;
  const seenItemPairs = new Set<string>();
  const shopItems = [];
  const neutralItems = [];

  for (const it of rawItems) {
    if (!it.name || it.name.startsWith('item_recipe_')) continue;
    const en = cleanHtml(it.name_english_loc || '');
    const zh = ITEM_ZH_OVERRIDES[it.name] || cleanHtml(it.name_loc || '');
    if (!en || !zh) continue;
    const pairKey = `${en.toLowerCase()}::${zh}`;
    if (seenItemPairs.has(pairKey)) continue;
    seenItemPairs.add(pairKey);

    const slang = ITEM_SLANG[it.name] || {};
    const entry = {
      id: it.id,
      key: it.name,
      en,
      zh,
      neutralTier: typeof it.neutral_item_tier === 'number' ? it.neutral_item_tier : -1,
      isEnhancement: it.name.startsWith('item_enhancement_'),
      enAliases: slang.enAliases || [],
      zhSlang: slang.zhSlang || [],
    };
    if (entry.neutralTier >= 0 || entry.isEnhancement) {
      neutralItems.push(entry);
    } else {
      shopItems.push(entry);
    }
  }

  shopItems.sort((a, b) => a.en.localeCompare(b.en));
  neutralItems.sort((a, b) => a.neutralTier - b.neutralTier || a.en.localeCompare(b.en));

  const totalAbilities = heroes.reduce((acc, h) => acc + h.abilities.length, 0);
  const dictionary = {
    generatedAt: new Date().toISOString(),
    source: 'Valve Official Dota 2 Datafeed (english + schinese) + Community Coaching Slang',
    counts: {
      tacticalTerms: TACTICAL_TERMS.length,
      heroes: heroes.length,
      abilities: totalAbilities,
      shopItems: shopItems.length,
      neutralAndEnhancements: neutralItems.length,
    },
    tacticalTerms: TACTICAL_TERMS,
    heroes,
    shopItems,
    neutralItems,
  };

  const dataDir = path.join(ROOT_DIR, 'data');
  const docsDir = path.join(ROOT_DIR, 'docs');
  await fs.mkdir(dataDir, { recursive: true });
  await fs.mkdir(docsDir, { recursive: true });

  const jsonPath = path.join(dataDir, 'dota2-dictionary.json');
  await fs.writeFile(jsonPath, JSON.stringify(dictionary, null, 2), 'utf8');

  // Generate comprehensive human + LLM Markdown document
  const mdLines: string[] = [
    '# DOTA2 中英双语全量术语词典（英雄 · 全技能 · 全装备 · 战术黑话）',
    '',
    `> 数据源：Valve 官方实时双语数据库（\`dota2.com/datafeed\` \`english\` + \`schinese\`）+ 中美高分局语音常用黑话与简称。`,
    `> 统计：**${heroes.length}** 位英雄 · **${totalAbilities}** 个英雄技能（含先天技能）· **${shopItems.length}** 件商店/消耗/特殊物品 · **${neutralItems.length}** 件中立道具与附魔 · **${TACTICAL_TERMS.length}** 组高频对线/视野/团战术语。`,
    '',
    '## 0. 极易翻错的跨语种外号消歧表（Critical Disambiguation Rules）',
    '',
    '- **猴子 = Phantom Lancer (PL / 幻影长矛手)**；**大圣 = Monkey King (MK / 齐天大圣)**（切勿把中文“猴子”译成 Monkey King！）',
    '- **小牛 = Earthshaker (ES / 撼地者)**；**大牛 = Elder Titan (ET / 上古巨神)**；**白牛 = Spirit Breaker (SB / 裂魂人)**',
    '- **小鱼 = Slark (斯拉克)**；**大鱼 = Slardar (斯拉达)**；**小娜迦 = Naga Siren**；**大娜迦/一姐 = Medusa (美杜莎)**',
    '- **蓝猫 = Storm Spirit**；**火猫 = Ember Spirit**；**土猫 = Earth Spirit**；**紫猫 = Void Spirit**',
    '- **小黑 = Drow Ranger (卓尔游侠)**；**黑鸟 = Outworld Destroyer (OD / 殁境神蚀者)**；**黑贤 = Dark Seer (DS / 黑暗贤者)**',
    '- **小鹿 = Enchantress (魅惑魔女)**；**老鹿 = Leshrac (拉席克)**',
    '- **小骷髅/骨弓 = Clinkz (克林克兹)**；**骷髅王 = Wraith King (WK / 冥魂大帝)**；**骨法 = Pugna (帕格纳)**',
    '- **小狗 = Lifestealer (LS / 噬魂鬼)**；**毒狗 = Shadow Demon (SD / 暗影恶魔)**；**电狗 = Arc Warden (天穹守望者)**；**毒龙 = Viper (冥界亚龙)**',
    '- **堆野 / 屯野 = stack camp**（绝不翻成“叠营地”）；**屯远古 = stack ancients**（绝不翻成“叠古树”）',
    '- **反眼 / 排眼 = deward**；**假眼 = Observer Ward (obs)**；**真眼 = Sentry Ward (sentry)**；**粉 = Dust**；**开雾 = pop smoke**',
    '',
    '---',
    '',
    '## 1. 常用对线、野区、视野、资源与团战术语（Tactical & Coaching Slang）',
    '',
    '| 分类 | 英文标准表达 (教练常说) | 英文同义/缩写 | 中文地道表达 (学员常说) | 中文同义黑话 |',
    '|---|---|---|---|---|',
  ];

  for (const t of TACTICAL_TERMS) {
    mdLines.push(
      `| ${t.category} | **${t.en}** | ${t.enAliases.join(', ') || '-'} | **${t.zh}** | ${t.zhAliases.join(' / ') || '-'} |`,
    );
  }

  mdLines.push(
    '',
    '---',
    '',
    `## 2. 全部 ${heroes.length} 位英雄中英对照与外号（All Heroes & Nicknames）`,
    '',
    '| 英文名 (Official EN) | 英文简称 | 中文官译 (Official ZH) | 中文常用外号/简称 |',
    '|---|---|---|---|',
  );

  for (const h of heroes) {
    mdLines.push(
      `| **${h.en}** | ${h.enAliases.join(', ') || '-'} | **${h.zh}** | ${h.zhSlang.join(' / ') || '-'} |`,
    );
  }

  mdLines.push(
    '',
    '---',
    '',
    `## 3. 商店装备、消耗品与中立道具中英对照（All ${shopItems.length + neutralItems.length} Items）`,
    '',
    '### 3.1 商店装备、合成件与掉落物（Shop & Roshan Items）',
    '',
    '| 英文名 (Official EN) | 英文简称 | 中文官译 (Official ZH) | 中文常用黑话 |',
    '|---|---|---|---|',
  );

  for (const it of shopItems) {
    mdLines.push(
      `| **${it.en}** | ${it.enAliases.join(', ') || '-'} | **${it.zh}** | ${it.zhSlang.join(' / ') || '-'} |`,
    );
  }

  mdLines.push(
    '',
    '### 3.2 中立道具与附魔词条（Neutral Items & Enchantments）',
    '',
    '| Tier | 英文名 (Official EN) | 中文官译 (Official ZH) |',
    '|---|---|---|',
  );

  for (const it of neutralItems) {
    const tierLabel = it.isEnhancement ? '附魔 (Enchantment)' : `Tier ${it.neutralTier + 1}`;
    mdLines.push(`| ${tierLabel} | **${it.en}** | **${it.zh}** |`);
  }

  mdLines.push(
    '',
    '---',
    '',
    `## 4. 全部 ${heroes.length} 位英雄全技能中英对照（All ${totalAbilities} Hero Abilities）`,
    '',
  );

  for (const h of heroes) {
    const nickEn = h.enAliases.length ? ` (${h.enAliases.join('/')})` : '';
    const nickZh = h.zhSlang.length ? `（${h.zhSlang.join('/')}）` : '';
    mdLines.push(`### ${h.en}${nickEn} ⇔ ${h.zh}${nickZh}`);
    for (const ab of h.abilities) {
      const tags: string[] = [];
      if (ab.isInnate) tags.push('先天/Innate');
      if (ab.enAliases.length) tags.push(`EN: ${ab.enAliases.join('/')}`);
      if (ab.zhSlang.length) tags.push(`俗称: ${ab.zhSlang.join('/')}`);
      const suffix = tags.length ? ` — *${tags.join(' · ')}*` : '';
      mdLines.push(`- **${ab.en}** ⇔ **${ab.zh}**${suffix}`);
    }
    mdLines.push('');
  }

  const mdPath = path.join(docsDir, 'dota2-bilingual-dictionary.md');
  await fs.writeFile(mdPath, mdLines.join('\n'), 'utf8');

  console.log(`Generated JSON: ${jsonPath}`);
  console.log(`Generated Markdown: ${mdPath} (${mdLines.length} lines)`);
  console.log('Counts:', dictionary.counts);
}

main().catch((err) => {
  console.error(err);
  process.exit(1);
});
