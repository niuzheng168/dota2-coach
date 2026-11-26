# Dota Coach 双机教练

游戏机运行 `DotaCoach.GameClient`：在自己的 Dota 窗口看小兵血量、对手装备、眼位和路线。教练机运行 `DotaCoach.CoachClient`：接收本人 GSI，可接入 Dota Plus 队伍视角、调用模型，并在赛后复盘。两端通过配对的 TLS 连接通信，可在同一台 Windows 电脑上试用。

**v0.6.7** 兵线预测按录像校正：有 Plus 时，进雾的敌兵估计和按时钟推出的敌方一波，在两局真人比赛里分别有 85–91 % 和 98–99 % 落在真实兵线 600 以内（v0.6.6 为 40–51 %）；不再画己方理论波、空线基线和「若守」，交汇预测只画 5 秒内的。只改了教练端的推算（游戏端仅版本号）。**v0.6.6** 游戏端小地图上的眼位、兵线和离开视野标记默认缩到原来的六成、60% 不透明，不再挡住地图；大小和不透明度在游戏端「设置 → 游戏内显示」调，「预览 8 秒」会在小地图框里画示例标记。只改了游戏端（教练端仅版本号）。**v0.6.5** 用上 Plus 队伍视角里的英雄朝向：敌人离开视野后，游戏端小地图标出它最后的位置、面朝方向和前 10 秒大概所在的范围；离开视野字幕默认看不到 5 秒才发，带「朝…」，刚被我方击杀的不报。对手面板加上次读到的攻击、护甲、移速。推测的眼位画成黄色小眼睛，目击的守卫用空心菱形框住；兵线不必先确认小地图框。请两端一起升级（旧游戏端连新教练端时不显示属性和离开视野位置）。游戏端仍不传画面，对手装备面板保留到局末。

## 我想做什么？

| 任务 | 从这里开始 |
|---|---|
| 只在游戏机看补刀提示 | [小兵血量与塔下垫刀](docs/game-creep-hp.md)：半血以下，友方绿字、敌方白字；按自身攻击力、兵的护甲和回血估算，一刀可收时变红。塔下黄字是建议先垫的刀数。本机显卡识别和绘制，不要求配对、Plus 或模型。 |
| 配对、记档，同局看小兵血量和对手装备／眼位 | 游戏端只有一种连接：只传本人 GSI，不传画面，接收字幕、装备面板、眼位和分钟路线。小兵血量在本机识别，连接前后都可在「显示小兵血量」开关。见[不传画面的连接](docs/twopc-status-only.md)。 |
| 看上次读到的对手装备、离开视野和守卫 | [Plus 实时观战与推送](docs/twopc-plus-alerts.md) · [视觉扫描](docs/hero-scan.md) · [眼位规则与实测](docs/twopc-enemy-wards.md)。装备标读取年龄；守卫目击与按携带量减少推测的 1–3 个候选点分开显示。地名见 [7.42 教练地图](docs/coach-map-742.md)。 |
| 看兵线、迷雾估计和交汇倒计时 | [兵线小地图图例](docs/twopc-game-overlay.md#兵线小地图)。标准 7.42 / build 6989，按游戏秒更新，不需要模型。空心点是最近所见小兵的估计，不是确认它还活着。 |
| 调游戏里的字幕、装备面板和小地图 | [游戏内显示](docs/twopc-game-overlay.md)：改位置、大小、颜色和透明度，用「预览 8 秒」看样式。分钟路线要在教练端确认与游戏 HUD 一致的小地图框；眼位和兵线没确认框时按这里的小地图位置和大小画。 |
| 让模型按分钟选线或野 | [7.42 分钟打钱](docs/twopc-minute-farm-742.md)：露娜／巨魔，显式开启，核对标准地图、build 6989 和小地图框。每个游戏分钟最多尝试一次；可弃答，不续用上分钟的路线，不保证提高 GPM。无需 Plus 或传画面。 |
| 选择模型 | [Foundry 或 Codey（GitHub Copilot）](docs/twopc-model-providers.md)：普通画面建议和分钟规划共用教练机选择的服务。Codey 的 Astra / Sol 先查本机目录，失败不自动换模型或服务；可能计费的推理与查目录是两回事。 |
| 查一局当时知道什么，赛后对照录像 | [对局档案与复盘](docs/twopc-match-information.md)：记录本人 GSI 不要求开 Debug 或模型。在教练机查看「当时已知」，可信终局封存后可下载或导入同局录像，再看「录像全图」或并排对照。 |

## 第一次连接

两端窗口打开是**主页**：连接状态和常用开关（教练端：复制配对码、视觉扫描、推送到游戏机的三项、自动教练和分钟打钱、对局复盘；游戏端：配对、小兵血量、游戏里显示哪些内容）。只设一次的项目在右上角「**设置**」里按组排列（教练端：连接 / Plus 观战与扫描 / AI 模型 / 小地图校准 / 调试；游戏端：游戏内显示 / 小兵血量 / Dota 与 GSI / 调试）。

1. 两端使用同版本的 Windows 11 x64 客户端。教练端随启动开始接收；在「设置 → 连接」确认「本机地址」是游戏机可达的局域网 IP，而非 WSL 虚拟网卡；同机用 `127.0.0.1`。默认端口 `38471`，防火墙只为程序或端口允许专用网络入站。在主页点「复制配对码」私下发到游戏机；怀疑泄露时在「设置 → 连接」点「撤销配对」。
2. 游戏端在「设置 → Dota 与 GSI」核对 Dota 安装目录，粘贴配对码并连接（不传画面）。首次写入或更新 GSI 配置后，保留原 Steam 启动选项，加入 `-gamestateintegration`，完全退出并重开 Dota 一次。游戏用无边框或窗口模式，识别小兵用默认血条。
3. **只想记自己的对局**：不必运行扫描或配置模型；有新鲜的本人对局号、英雄、阵营和游戏时钟后，教练端开始记档。**需要团队信息**：在教练端「设置 → Plus 观战与扫描」一键配置观战 GSI，教练机的 Dota 也加上述启动参数并重启。用另一个有 Dota Plus、与玩家互为好友的账号实时观战。
4. **要读装备和守卫数量**：在教练端「设置 → Plus 观战与扫描」填本机素材工作目录和 Python，回主页点「启动视觉扫描」。保持观战窗口在前台；首次写入扫描专用 GSI 配置后按提示重启 Dota。按钮显示进程状态，再点可停。在主页勾选要推给游戏端的项目；记录离开视野或实际目击不需要扫描。
5. **需要模型建议时**再在「设置 → AI 模型」配置。游戏端不传画面，普通画面模型不会推理；分钟模式只发请求时的结构化输入。分钟模型默认不收团队信息，必须另行授权。密钥只放教练机。只测连接：连上后在教练端「设置 → 连接」点「发送测试提醒」，看游戏端窗口的字幕行；不调用模型。两端可按需开启本地 Debug（[旧版调试字段说明](docs/twopc-v021-debug.md)）。

## 7.42 打钱研究（离线）

**录像存储（Windows）：** D2PT 离线研究默认复用 `Z:\dota2coach\data\replays`。下载包、解压的 `.dem`、解析临时文件和复盘缓存都在 Z 盘；7.42 专项仍用 `data\farm742*` 子目录。盘未挂载时不会改写 C 盘。覆盖方法和桌面端的区别见[录像研究说明](docs/luna-hundred-study.md#录像工作目录)。

**每日教练记忆：** Codex 本地计划任务「Dota 2 每日录像复盘」在香港时间 00:05 研究当前补丁的露娜、巨魔战将、斯温，每英雄目标 20 场；02:05、04:05 补查。按游戏时钟逐分钟复盘，每分钟比较前后两个 30 秒的线野收益和实录补刀。用 `npm run replay:memory -- --status` 查看今天的实数；[任务管理、选样及按分钟读取长期记忆](docs/daily-replay-memory.md)。

这些资料解释路线依据，不是开启实战悬浮层的步骤。

- [研究总览](docs/farming-742-field-guide.md)与[分钟证据](docs/farming-742-minute-evidence.md)：D2PT 分钟记录由 101 场扩到 **105 场**（露娜 69、巨魔 36）。其中 **16 场**有可定位的 Valve 回放，支持 240 个 5–20 分钟的录像分钟；其余 89 场不能据补刀曲线编造走位。
- [新版地标和四段实录](docs/farming-742.md) · [露娜](docs/farming-742-luna.md) · [巨魔](docs/farming-742-troll.md)。在源码根目录运行 `npm ci`、`npm run sandbox`，从终端打印的本机地址进入 `/farming-742`（保留链接中的 `#token=…`）可切换回放；网页不会给游戏端下发路线。
- 查估算依据：[地形与步行时间](docs/farming-742-terrain.md)、[营地、赏金和清营时间](docs/farming-742-camps.md)；[影子教练回测](docs/farming-742-shadow.md)和[本地输入桥](docs/farm-shadow-bridge.md)是独立的研究工具，不把回放全图当成玩家视野。

## 从源码运行、测试和打包

Windows 构建需要 .NET 10 SDK；数据与沙盒脚本用 Node.js 24。在仓库根目录运行（两个窗口分别启动）：

```powershell
dotnet run --project desktop/DotaCoach.CoachClient -c Release
dotnet run --project desktop/DotaCoach.GameClient -c Release

# 核心协议、规则和真实窗口 UI；每次给 UI 测试一个新输出目录
dotnet run --project desktop/DotaCoach.TwoPc.Tests -c Release
dotnet run --project desktop/DotaCoach.TwoPc.UiTests -c Release -- .tmp/twopc-ui-01
```

[打包脚本](desktop/build-twopc.ps1)先验证已固定哈希的赛后解析器，运行协议、对局生命周期、复盘和 UI 测试，再生成两个自带运行时的 win-x64 ZIP、各自的 `.sha256` 和 `manifest.json`。构建前按[赛后解析器说明](scripts/replays/postgame/README.md#offline-build-and-regression)准备 `.tmp/postgame-replay-v2`，或用 `-ReplayToolsDirectory` 指定已核验的目录。只有教练端包带解析器，解压包的用户不必为复盘安装 Python 或 Node。

```powershell
.\desktop\build-twopc.ps1
.\desktop\verify-match-release.ps1 -Directory .\dist\twopc\v0.6.7
```

版本取两端项目一致的 `<Version>`；默认输出 `dist/twopc/v<版本>/`，已存在即拒绝覆盖，可另选 `-OutputDirectory`。包内 `README.md` 是对局档案手册，`MODEL-PROVIDERS.md` 和 `MINUTE-FARM.md` 分别讲模型和分钟路线。另附 `NO-VIDEO.md`、`CREEP-HP.md`、`PLUS-ALERTS.md`、`ENEMY-WARDS.md`、`GAME-OVERLAY.md`；`USAGE.md` 保留 v0.4.3 的历史说明。本机已提取图标时，游戏端包可带图标（仅供自用）；未附的图标向教练端请求。

### 视觉扫描和数据准备

- 教练端带扫描代码，**不带**本机视觉 Python、识别素材和 HUD 标定。在素材工作目录按 [requirements.txt](scripts/vision/requirements.txt) 建 `.tmp/vision-tools` 环境（NVIDIA/CUDA），用 `node scripts/vision/extract-assets.ts` 提取本机游戏素材。Dota 更新后重新提取；先备份 `hud-1080.npz` 和守卫数字模板 `hud-digits-1080.npz`。观战用 1920×1080 默认 HUD，玩家 1–10 的快捷键为 `1`–`0`。标定和离线验证见[视觉扫描](docs/hero-scan.md)。
- [英雄、装备攻击与塔的数值](docs/game-creep-hp.md)：`npm run build:hero-attack`；[7.42 地点和小地图投影](docs/coach-map-742.md)：`npm run map:coach -- <Dota 目录> <Source2Viewer-CLI.exe>`；[常见眼位](docs/twopc-enemy-wards.md)：`npm run build:ward-spots`。英雄与物品中文名用 `npm run build:dict`。
- 分钟模拟资产可用 `node scripts/client/build-minute-farm-742.ts --check` 核对。只提交已筛选的研究数据和示例；不要把原始 DEM、GSI、HUD 帧、私人档案、调试日志、配对码或模型密钥加入 Git。

源码分工：`desktop/` 是两端、共享核心与原生测试；`scripts/vision/` 是 HUD 扫描与素材提取；`scripts/client/`、`scripts/replays/` 是数据构建与录像工具；`data/` 是版本化的数据，`docs/` 是手册。桌面程序不依赖启动旧语音网页或研究沙盒。

## 版本与验证范围

| 版本 | 内容 |
|---|---|
| [v0.6.7](docs/twopc-minute-farm-742.md#读实时兵线不用开模型) | 兵线预测按录像校正，两局真人比赛对照见文中表格；有 Plus 时只画可信的推测。 |
| [v0.6.6](docs/twopc-game-overlay.md#小地图黄色眼睛是推测空心菱形是目击) | 小地图标记默认更小、半透明，可调大小和不透明度。 |
| [v0.6.5](docs/twopc-enemy-wards.md#敌人离开视野字幕和小地图位置2026-11-24) | 离开视野的小地图位置和朝向，字幕默认 5 秒、不报刚阵亡的；对手面板攻击／护甲／移速；推测眼位改黄色眼睛，兵线不必先校准小地图框。 |
| [v0.6.4](docs/twopc-game-overlay.md#兵线小地图) | 游戏端兵线、进雾估计和交汇倒计时；另有[按补丁、英雄、分钟保存的离线录像复盘](docs/daily-replay-memory.md)。 |
| [v0.6.1](docs/twopc-model-providers.md) | 教练端选择 Foundry 或 Codey / GitHub Copilot。 |
| [v0.6.0](docs/twopc-match-information.md) | 对局档案、按授权显示装备与眼位、赛后回放对照。 |
| [v0.5.0](docs/twopc-minute-farm-742.md) | 7.42 露娜／巨魔分钟规划，不依赖游戏机上传画面。 |
| [v0.4.8](docs/twopc-v048.md) | 带守卫的敌人优先扫描，亚秒读取；持续可见时按敌人当时站位和走向标首选点，其余用 47 场录像拟合的眼位规则。 |
| [v0.4.7](docs/twopc-v047.md) | 视觉扫描启动按钮、反眼撤圈、修正卡尔槽位误判；失去视野 15 秒以上不画圈。 |
| [v0.4.6](docs/twopc-v046.md) | 半血过滤、一刀可收变红、塔下黄字提示垫刀。 |
| [v0.4.5](docs/twopc-v045.md) | 7.42 地图地点和对手插眼推测。 |
| [v0.4.4](docs/twopc-v044.md) | 对手面板用独立小窗口，只在内容变化时重画。 |
| [v0.4.3](docs/twopc-v043.md) | 当时的纯状态连接、对手面板、离开视野字幕和游戏内设置。 |

各版实测不能互相代替：v0.4.8 有真人 Plus 局的记录重放，不等于新版在新真人局的命中率；小兵红／黄字尚无按提示实际补中的整局验证，4K/240 Hz 的装备面板测试也不是小兵识别性能测试。分钟模式的模拟、离线回放和原生 UI 测试都不证明排位收益。
