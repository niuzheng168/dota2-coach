# Dota Coach 双机教练

游戏机运行 `DotaCoach.GameClient`：在自己的 Dota 窗口看小兵血量、对手装备、眼位和路线。教练机运行 `DotaCoach.CoachClient`：接收本人 GSI，可接入 Dota Plus 队伍视角、调用模型，并在赛后复盘。两端通过配对的 TLS 连接通信，可在同一台 Windows 电脑上试用。

**v0.6.2** 保留 v0.4.8 的小兵补刀提示、7.42 地图和守卫推测。游戏端不传画面：实战只用游戏机的本人 GSI，以及同局同阵营当时可见的 Plus 信息。对手装备面板一直显示到本局结束，读不到时只把读取年龄往上加。不读内存、不注入游戏、不代操作；赛后回放全图不回填实战。

## 我想做什么？

| 任务 | 从这里开始 |
|---|---|
| 只在游戏机看补刀提示 | [小兵血量与塔下垫刀](docs/game-creep-hp.md)：半血以下，友方绿字、敌方白字；按自身攻击力、兵的护甲和回血估算，一刀可收时变红。塔下黄字是建议先垫的刀数。本机显卡识别和绘制，不要求配对、Plus 或模型。 |
| 配对、记档，同局看小兵血量和对手装备／眼位 | 游戏端只有一种连接：只传本人 GSI，不传画面，接收字幕、装备面板、眼位和分钟路线。小兵血量在本机识别，连接前后都可在「显示小兵血量」开关。见[不传画面的连接](docs/twopc-status-only.md)。 |
| 看上次读到的对手装备、离开视野和守卫 | [Plus 实时观战与推送](docs/twopc-plus-alerts.md) · [视觉扫描](docs/hero-scan.md) · [眼位规则与实测](docs/twopc-enemy-wards.md)。装备标读取年龄；守卫目击与按携带量减少推测的 1–3 个候选点分开显示。地名见 [7.42 教练地图](docs/coach-map-742.md)。 |
| 调游戏里的字幕、装备面板和小地图 | [游戏内显示](docs/twopc-game-overlay.md)：改位置、大小、颜色和透明度，用「预览 8 秒」看样式。分钟路线要在教练端确认与游戏 HUD 一致的小地图框；眼位没确认框时按这里的小地图位置和大小画。 |
| 让模型按分钟选线或野 | [7.42 分钟打钱](docs/twopc-minute-farm-742.md)：露娜／巨魔，显式开启，核对标准地图、build 6989 和小地图框。每个游戏分钟最多尝试一次；可弃答，不续用上分钟的路线，不保证提高 GPM。无需 Plus 或传画面。 |
| 选择模型 | [Foundry 或 Codey（GitHub Copilot）](docs/twopc-model-providers.md)：普通画面建议和分钟规划共用教练机选择的服务。Codey 的 Astra / Sol 先查本机目录，失败不自动换模型或服务；可能计费的推理与查目录是两回事。 |
| 查一局当时知道什么，赛后对照录像 | [对局档案与复盘](docs/twopc-match-information.md)：记录本人 GSI 不要求开 Debug 或模型。在教练机查看「当时已知」，可信终局封存后可下载或导入同局录像，再看「录像全图」或并排对照。 |

## 第一次连接

1. 两端使用同版本的 Windows 11 x64 客户端。教练端启动接收，确认「本机地址」是游戏机可达的局域网 IP，而非 WSL 虚拟网卡；同机用 `127.0.0.1`。默认端口 `38471`，防火墙只为程序或端口允许专用网络入站。点「复制配对码」私下发到游戏机；怀疑泄露时点「撤销配对」。
2. 游戏端在「设置 → Dota 目录」核对 Dota 安装目录，粘贴配对码并连接（不传画面）。首次写入或更新 GSI 配置后，保留原 Steam 启动选项，加入 `-gamestateintegration`，完全退出并重开 Dota 一次。游戏用无边框或窗口模式，识别小兵用默认血条。
3. **只想记自己的对局**：不必运行扫描或配置模型；有新鲜的本人对局号、英雄、阵营和游戏时钟后，教练端开始记档。**需要团队信息**：在教练端「Plus 实时观战」一键配置观战 GSI，教练机的 Dota 也加上述启动参数并重启。用另一个有 Dota Plus、与玩家互为好友的账号实时观战。
4. **要读装备和守卫数量**：在教练端填本机素材工作目录和 Python，点「启动视觉扫描」。保持观战窗口在前台；首次写入扫描专用 GSI 配置后按提示重启 Dota。按钮显示进程状态，再点可停。开启要推给游戏端的项目；记录离开视野或实际目击不需要扫描。
5. **需要模型建议时**再配置模型。游戏端不传画面，普通画面模型不会推理；分钟模式只发请求时的结构化输入。分钟模型默认不收团队信息，必须另行授权。密钥只放教练机。只测连接：连上后在教练端 Debug 点「发送测试提醒」，看游戏端窗口的字幕行；不调用模型。两端可按需开启本地 Debug（[旧版调试字段说明](docs/twopc-v021-debug.md)）。

## 7.42 打钱研究（离线）

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
.\desktop\verify-match-release.ps1 -Directory .\dist\twopc\v0.6.1
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
