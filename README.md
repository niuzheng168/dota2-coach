# Dota Coach 双机教练

两台 Windows 电脑配合打 Dota 2：

- **游戏机**（自己打游戏的那台）运行游戏端 `DotaCoach.GameClient`：在 Dota 画面上标出能补的兵，显示对手的等级、装备和提醒。
- **教练机**（另一台）运行教练端 `DotaCoach.CoachClient`：用另一个账号对游戏账号做 Dota Plus 实时观战，读出对手的情况，加密推送给游戏机。

只用游戏机自己的 GSI、自己的画面，以及 Plus 观战时己方队伍看得到的信息；不读内存，不注入游戏进程，不绕过战争迷雾。

当前版本 v0.4.8，各版说明见文末“版本”。

## 功能

### 游戏端

| 功能 | 说明 | 详细说明 |
|---|---|---|
| 小兵血量数字 | 半血以下的兵在血条上方显示血量（友方绿字、敌方白字）。你的英雄一刀必死时变红，提醒正补/反补；攻击力由自身 GSI 的英雄、等级、加点、天赋和装备推算，已算兵的护甲和回血。塔下的兵要先垫刀时，血条右边的黄色数字是最少要垫几刀（按塔和英雄的伤害、攻速算）。全在本机显卡上识别和绘制，不需要连教练端 | [game-creep-hp.md](docs/game-creep-hp.md) |
| 对手面板 | 画面左侧一直显示敌方英雄的头像、等级、六格装备和中立物品；读取旧了标出几秒（几分）前 | [twopc-plus-alerts.md](docs/twopc-plus-alerts.md) |
| 字幕和小地图标记 | 敌方离开视野时的字幕（带最后出现的地点）；敌方守卫变少时推测的眼位，在 Dota 小地图上圈出 1–3 处 | [twopc-enemy-wards.md](docs/twopc-enemy-wards.md) |
| 游戏内显示设置 | 字幕和对手面板的开关、位置、大小、颜色、透明度，改了立即在 Dota 上预览 | [twopc-game-overlay.md](docs/twopc-game-overlay.md) |
| 不传画面 | 只把自身 GSI 和 Dota 窗口尺寸经配对的 TLS 连接发给教练端，用来保持连接、接收字幕和面板 | [twopc-status-only.md](docs/twopc-status-only.md) |

### 教练端

| 功能 | 说明 | 详细说明 |
|---|---|---|
| 接收和配对 | 在局域网端口（默认 38471）接收游戏端；配对码含连接密钥，可随时撤销 | [twopc-v043.md](docs/twopc-v043.md) |
| Plus 实时观战 | 一键写入本机 Dota 的观战 GSI 配置，显示观战数据和它比游戏机慢多少 | [twopc-v031-plus.md](docs/twopc-v031-plus.md) |
| 视觉扫描 | 点“启动视觉扫描”：让观战的 Dota 轮流选中敌方英雄，从屏幕底部 HUD 读等级、装备和守卫数量，供对手面板和插眼推测使用 | [hero-scan.md](docs/hero-scan.md) |
| 推送开关 | 对手等级和装备、离开视野、插眼推测分别开关；状态行写明推送了什么、没推送的原因 | [twopc-plus-alerts.md](docs/twopc-plus-alerts.md) |
| Debug | 两端都可开启本地记录；“发送测试提醒”检查游戏机能否收到字幕 | [twopc-v021-debug.md](docs/twopc-v021-debug.md) |

离开视野和插眼的地点按 7.42 地图命名（野怪营地、塔、肉山、神符等），见 [coach-map-742.md](docs/coach-map-742.md)。

## 需要

- 两台 Windows 11 x64，在同一局域网（同机测试可用 `127.0.0.1`）。教练端要能被游戏机访问：Windows 防火墙只为这个程序或端口允许专用网络入站。
- Steam 里 Dota 的启动选项含 `-gamestateintegration`（两台都要）。
- **游戏机**：Dota 用无边框窗口（或窗口）、默认血条。
- **教练机**：
  - 第二个 Steam 账号，有 Dota Plus，和游戏账号互为好友。
  - 视觉扫描在源码仓库里运行，不在安装包内：需要仓库、`.tmp\vision-tools` 里的 Python 环境（NVIDIA 显卡 + CUDA）和从本机 Dota 提取的识别素材，准备步骤见下文。Dota 用 1920×1080 窗口、默认 HUD，观战快捷键“选择玩家 1–10”保持 `1`–`0`。
- **构建**：.NET 10 SDK；生成数据和提取素材的脚本用 Node.js 24（直接运行 `.ts`）。

## 构建

在仓库根目录：

```powershell
powershell -ExecutionPolicy Bypass -File desktop/build-twopc.ps1
```

- 先跑协议测试和两端窗口的 UI 测试，通过后把两端发布成自带 .NET 运行时的 win-x64 包，放到 `dist/twopc/v<版本>/`：两个 ZIP、各自的 `.sha256` 和 `manifest.json`。版本号取两个 csproj 的 `<Version>`（必须相同）。
- 输出目录已存在时拒绝构建，不覆盖已发布的包；`-OutputDirectory` 可以换目录。
- 本机有 `.tmp\vision-assets` 时，游戏端包会带上对手面板用的英雄和物品图标；没有时游戏端会向教练端要。
- 包里附带使用说明（`README.md` 是这一版的说明，`USAGE.md` 是完整步骤，其余是各功能的说明）。

开发时直接运行和测试：

```powershell
dotnet run --project desktop/DotaCoach.CoachClient
dotnet run --project desktop/DotaCoach.GameClient

dotnet run --project desktop/DotaCoach.TwoPc.Tests -c Release            # 要在仓库根目录运行
dotnet run --project desktop/DotaCoach.TwoPc.UiTests -c Release -- .tmp/twopc-ui-<新目录名>
.tmp/vision-tools/Scripts/python.exe -I tests/hud-vision.test.py
```

### 随包数据（Dota 更新后重建）

| 数据 | 用途 | 重建 |
|---|---|---|
| `data/dota2-hero-attack.json` | 英雄攻击力、攻速、物品属性、塔的数值（小兵红字和垫刀） | `npm run build:hero-attack`，见 [game-creep-hp.md](docs/game-creep-hp.md) |
| `data/dota2-coach-map.json` | 7.42 地图地点和小地图投影 | `npm run map:coach -- <Dota 目录> <Source2Viewer-CLI.exe>`，见 [coach-map-742.md](docs/coach-map-742.md) |
| `data/dota2-ward-spots-742.json` | 7.42 常见眼位 | `npm run build:ward-spots`，见 [twopc-enemy-wards.md](docs/twopc-enemy-wards.md) |
| `data/dota2-dictionary.json` | 英雄、物品中文名 | `npm run build:dict` |

### 视觉扫描的一次性准备（教练机）

1. Python 环境：按 [`scripts/vision/requirements.txt`](scripts/vision/requirements.txt) 建 `.tmp\vision-tools` 虚拟环境（文件开头写了 CUDA 和 rapidocr 的安装顺序）。
2. 识别素材：`node scripts/vision/extract-assets.ts` 从本机 Dota 提取物品/技能图标、英雄小头像和中文名到 `.tmp\vision-assets`。**Dota 每次更新后都要重新提取**（先备份里面的标定文件 `hud-1080.npz`、`hud-digits-1080.npz`，提取完放回去）。
3. HUD 标定文件不提交；新机器上的生成方法见 [hero-scan.md](docs/hero-scan.md)。

## 使用

### 教练机

1. 运行 `DotaCoach.CoachClient.exe`，它自动开始接收。核对“本机地址”是游戏机能访问的局域网 IP（不要选 WSL 虚拟网卡）；要改地址先点“停止接收”。
2. 点“复制配对码”，私下发到游戏机。怀疑泄露就点“撤销配对”再重新复制。
3. 展开“Plus 实时观战（实验）”：填本机 Dota 安装目录，点“一键配置观战 GSI”，按提示完全重启本机 Dota 一次。
4. 用有 Dota Plus 的账号对游戏账号开始实时观战。
5. 点上方的“**启动视觉扫描**”，观战的 Dota 保持在前台。
   - 从仓库里运行的教练端会自己找到仓库目录和 Python；用安装包时，在“Plus 实时观战”里填一次“代码仓库目录”和“Python”。
   - 第一次启动会写入扫描用的 GSI 配置，提示后再重启一次 Dota。
   - 按钮下面显示扫描状态；再点一次停止，关掉教练端时扫描也一起停。
6. “Plus 实时观战”里的三个开关（对手等级和装备、离开视野、插眼）默认都开，下面的状态行写明推送了几名敌方英雄，或者为什么没推送。

### 游戏机

1. 运行 `DotaCoach.GameClient.exe`，粘贴配对码，点“连接”。第一次会写入自身 GSI 配置，提示后完全重启 Dota 一次。
2. 要看小兵血量，展开“小兵血量：本机实时显示”，点“显示小兵血量”。不连教练端也能用。
3. 字幕和对手面板的位置、大小、颜色在“游戏内显示”里调。

测试连接：开着 Dota（菜单里也行），在教练端 Debug 里点“发送测试提醒”，游戏机上应出现字幕。

## 目录

| 位置 | 内容 |
|---|---|
| `desktop/DotaCoach.GameClient` | 游戏端（WPF） |
| `desktop/DotaCoach.CoachClient` | 教练端（WPF） |
| `desktop/DotaCoach.TwoPc.Core` | 两端共用：配对和 TLS 连接、GSI、小兵血量识别和绘制、攻击力和垫刀、对手面板、离开视野、插眼推测、视觉扫描进程 |
| `desktop/DotaCoach.TwoPc.Tests`、`desktop/DotaCoach.TwoPc.UiTests` | 协议和规则测试；两端真实窗口的 UI 测试 |
| `desktop/build-twopc.ps1` | 测试并打包 |
| `scripts/vision/` | 视觉扫描：`hero_scan.py`（扫描）、`hud_recognizer.py`（HUD 识别）、`wgc.py`（窗口采集）、`extract-assets.ts`（提取素材） |
| `scripts/client/` | 从本机 Dota 客户端生成数据 |
| `data/` | 随包数据 |
| `docs/` | 各功能说明和版本说明 |

`src/`、`public/`、`deploy/`，以及 `scripts/`、`tests/` 里与上面无关的内容，是早期的网页实验，双机教练不依赖它们。

## 版本

| 版本 | 内容 |
|---|---|
| [v0.4.8](docs/twopc-v048.md) | 插眼推测更准：敌人插眼时在视野里，圈画在它当时站的地方；扫描读取时间精确到零点几秒、带守卫的敌人读得更勤；其余按 47 场录像拟合的打分挑眼位 |
| [v0.4.7](docs/twopc-v047.md) | 教练端“启动视觉扫描”按钮；反掉的眼不再留圈；扫描不再把敌人误当成队友而整局不扫（卡尔没装备）；插眼推测按真人 Plus 局修正，看不见 15 秒以上只发字幕不画圈 |
| [v0.4.6](docs/twopc-v046.md) | 小兵血量只标半血以下，一刀必死变红，塔下黄字提示垫几刀 |
| [v0.4.5](docs/twopc-v045.md) | 对手插眼推测（小地图标记），7.42 地图地点 |
| [v0.4.4](docs/twopc-v044.md) | 对手面板改用独立小窗口，只在内容变化时重画 |
| [v0.4.3](docs/twopc-v043.md) | 游戏端不再传画面；小兵血量、对手面板、离开视野字幕、游戏内显示设置 |

更早的版本（视频传输、AI 打钱路线等，代码保留但默认关闭）见 `docs/twopc-*.md`。

还没验证的：在真实对局里跟着红字和黄字补刀；游戏机（4K/240 Hz）上的显示。
