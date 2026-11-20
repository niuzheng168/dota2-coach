# DOTA2 Bilingual Voice Coaching Bridge (中英同传语音室)

专为 **英文 DOTA2 教练 ↔ 中文学员** 实时连麦设计的低延迟双语同传语音室，对接 **Microsoft Foundry / Azure Voice Live**。登录后，由有权限的账号通过创建弹窗选择 **三种主流语音同传架构**，同时保留 **免切屏双通道自动识阵**：

1. **Voice Live · Cascaded (`VoiceLiveEngine` · `gpt-6-luna`)**
   - 基于 `/voice-live/realtime?api-version=v1` 服务端统一编排：`azure_semantic_vad` + DOTA2 词表偏置 ASR（可选 `azure-speech` / `mai-transcribe-2-streaming` / `mai-transcribe-2`）→ `gpt-6-luna` 流式同传 → 流式 Azure TTS（`24kHz PCM`）。
2. **Voice Live · Realtime 2.1 (`VoiceLiveEngine` · `gpt-realtime-2.1`)**
   - 与 Cascaded 共用同一套 `/voice-live/realtime` 协议、`azure_semantic_vad` 与**同一组可选 Azure 标准/DragonHD 音色**（如 `zh-CN-XiaoxiaoMultilingualNeural`、`en-US-Andrew:DragonHDLatestNeural`），支持 100% 公平同音色延迟对比，并自动滚动裁剪历史上下文防止 45 分钟长局膨胀。
3. **GPT-Live 1 (`GptLiveEngine` · `gpt-live-1`)**
   - 基于 `/openai/v1/live/sessions` 原生全双工流式同传，60ms 实时音频时钟驱动，支持边听边译的真同传体验，并在 BP 阶段通过 `session.instructions.append` 实现 **0ms 不断麦动态追加本局英雄技能词表**。

> 注：仓库同时完整保留了纯 WebSocket `v2` Text-Streaming TTS 客户端（[`src/tts-client.ts`](src/tts-client.ts)，含 `Path:text.piece`、`Path:text.flush` 与首逗号转句号加速，零 Speech SDK 依赖）及本地自建 [`CascadeEngine`](src/engines/cascade-engine.ts)（已标记 `@deprecated`）供基准测试对照。

---

## Windows 桌面工作台 v0.2.0

**游戏端小兵血量：** [血条上方显示血量数字](docs/game-creep-hp.md)。本机显卡识别和绘制，画面不传给教练端；显卡有余量时每帧刷新，被游戏占满时限 60 帧。旧的塔刀提示仍暂停（[历史实验记录](docs/game-last-hit-practice.md)）。

桌面端现在使用**随包发布的 WebView2 界面 + C# 独立音频后台**：工作台、对话、字幕和设置分区；麦克风/游戏采集、上传和译音播放不再经由网页。切换界面不停止音频，离线也能打开字幕设置和预览。兼容现有生产服务，无需同步部署新网页。

使用、构建、功能边界及测试见 [`docs/desktop-v020.md`](docs/desktop-v020.md)。版本化包固定交付到 `/nas/share/DotaCoach-v0.2.0-win-x64.zip`，保留旧版本。

### 本地采集与置顶双语字幕

保留现有 App 连麦，新增 **Windows 11 x64 本地助手**：

- App 内原文/译文可以显示到原生置顶、鼠标穿透、不抢焦点的字幕窗口。
- 可另外监听 Dota 2 或 YY 等所选进程及其子进程：自动语言识别 → 英语筛选 → 中文翻译。
- 外部监听仅追加本人聊天记录与字幕，**不朗读、不代发游戏语音、不广播其他房间成员**。
- 支持显示器、原文/译文颜色、文字/背景透明度、字号、顶部距离与停留时间设置。
- 第一版一次监听一个进程树；字幕覆盖建议用无边框窗口。不能保证从混合游戏声音中只分离教练人声。

构建、使用和实机验收清单见 [`docs/windows-listener.md`](docs/windows-listener.md)。Windows 源码位于 [`desktop/`](desktop/)；`desktop/build.ps1` 生成包含 .NET 运行时的 x64 测试包。需要先更新服务端；编译/模拟测试不等于 Win11 实机验收。

---

## 免切屏本局阵容自动锁定（Dual-Channel Auto-Draft）

开局前打开网页与教练连麦后，进入 DOTA2 选人/对局无需切屏手动点选英雄：

1. **官方 Valve `Game State Integration (GSI)`（自动感知开局/换局）**
   - 点击页面左侧 **⬇ GSI .cfg** 下载配置文件并放入 `steamapps/common/dota 2 beta/game/dota/cfg/gamestate_integration/`。
   - 自动检测新 `matchid` / `HERO_SELECTION` 清空上一局阵容；玩家位自动上报本机英雄，**Dota Plus 实时观战位 / 教练位**自动实时上报 10 人阵容并热更新同传会话。
2. **浏览器窗口顶栏视觉识阵（`gpt-6.1-terra` Vision · 最多 10 英雄识别）**
   - 开局前点击一次 **🖥️ 开启窗口自动识阵** 共享 DOTA2 窗口，网页后台每 12 秒仅裁取顶部 `18%` 头像栏并通过本地像素差分门控过滤静止画面，在阵容变化时自动识别天辉/夜魇 10 位英雄并 **0ms 不断麦热更新**全技能词表。
   - `DEPLOYMENT_GPT_VISION` 必须是**主资源中实际存在的部署名**（默认 `gpt-6.1-terra`），不会使用 GPT-Live 独立资源，也不会改变语音同传模型。也可设置为 `gpt-6.1-sol`，其推理强度使用 `low`（不支持 `none`）。若出现 `DeploymentNotFound / HTTP 404`，请核对主资源端点与部署名，不要自行添加 `-datazone-standard` 等后缀；修改 `.env` 后重启服务。识别失败不会缓存该帧，下一轮仍会重试。
   - 2026-11-14 同一张合成 10 英雄顶栏图各测试 3 次：Terra 和 Sol 均为每次 10/10；平均耗时分别约 2.8 秒、7.1 秒。这是单张合成图的接口实测，不代表不同分辨率、饰品或真实游戏画面的准确率保证。

### 手动点选英雄

房主点击 **「选择英雄」** 打开游戏风格的头像面板，按 **力量 / 敏捷 / 智力 / 全才** 分组。点击头像加入本局阵容，再点取消；最多 10 位，立即同步给所有成员并热更新词表，不重连语音。支持中文、英文与外号搜索（如「猴子」「PL」）、只看已选、清空，以及键盘操作。非房主仅查看阵容；未连接时不会假装保存更改。GSI 和视觉识别的结果也会同步到已打开的面板。

头像与属性图标来自 Valve，保存在 `public/heroes/`，打开面板时不请求外部图片站。出处见该目录的 `sources.json`。更新本地英雄属性和图片可运行 `npm run build:heroes`；不改变技能、翻译与黑话词表。重建整个词典后也可运行该命令补齐新增英雄图片。

---

## DOTA2 全量双语词典与双层领域注入

仓库内置从 Valve 官方接口（`dota2.com/datafeed` 英文 + 简体中文）拉取并结合中美高分局常用语音黑话整理的完整双语词典：

- **人类可读完整对照文档**：[`docs/dota2-bilingual-dictionary.md`](docs/dota2-bilingual-dictionary.md)
- **结构化词典数据**：[`data/dota2-dictionary.json`](data/dota2-dictionary.json)
  - **127 位全英雄** + 英文缩写与中文常用外号（含 `猴子=Phantom Lancer` vs `大圣=Monkey King`、`小牛/大牛/白牛`、`小鱼/大鱼`、`四猫`、`黑鸟/小黑/黑贤` 等严格消歧规则）
  - **736 个全英雄技能**（含先天技能 Innate 及最新英雄如 `Largo 朗戈`、`Kez 凯`、`Ringmaster 百戏大王` 全技能）
  - **419 件全商店装备、消耗品、肉山掉落物、中立道具与附魔**（已更新到 7.42）
  - **43 组高频对线/野区/视野/地图资源/团战术语**

---

## 快速启动（零硬编码密钥）

1. 复制环境变量模板并填入你的 Microsoft Foundry 资源名：
   ```bash
   cp .env.example .env
   ```
2. 执行 `az login`（默认通过 `AzureCliCredential` / `DefaultAzureCredential` 自动获取 Entra ID 令牌，无需配置任何静态 API Key），或在本地环境设置 `AZURE_API_KEY`，然后安装依赖：
   ```bash
   npm ci
   ```
3. 首次部署时，在**服务停止期间**创建账号。`npm run create-user` 从标准输入读取 JSON：
   ```json
   {"username":"your-user","password":"your-chosen-password","canCreateRooms":true}
   ```
   可通过受保护的临时文件传入（不要提交密码文件，完成后删除）：
   ```bash
   npm run create-user < /secure/path/account.json
   ```
   普通受邀账号设置 `canCreateRooms: false`。程序没有公开注册或默认密码。
4. 启动服务后访问 `http://localhost:3210/`：
   ```bash
   npm start
   ```
   - 登录后，有创建权限的账号可在弹窗中选择引擎、中文/英文音色、ASR 和允许加入的账号。
   - 房主可在房间内「修改同传设置」调整引擎、中文/英文音色和 ASR；保存实际更改后，全员旧连接和语音会话会重置并自动重连。语音就绪后需重新开麦；未改变设置不会打断连接。
   - 同传设置持久化，房间号、邀请链接、允许名单和本局阵容不受更改影响；房主仍可在「管理允许加入的用户」调整名单、复制邀请链接。
   - 其他账号必须同时满足**登录、有效邀请链接、位于允许名单内**才能加入，无法通过猜测房间号进入。
   - 取消授权会立即断开连接；退出登录会注销该会话并关闭关联连接。
   - 我的身份和自身翻译监听属于个人设置，仍可在房间内调整；本局阵容由房主/GSI 动态更新。

### 本地数据与部署

- Azure VM 的线上部署、HTTPS、持久化目录和更新/回退命令见 [`docs/azure-deployment.md`](docs/azure-deployment.md)。
- 账号的加盐 scrypt 密码哈希、会话和房间配置保存在 `.local/access.json`（文件权限 `0600`，已忽略提交）。也可通过 `APP_DATA_DIR` 指定绝对数据目录。
- 服务重启后账号、房间、邀请和允许名单仍保留；实时字幕和本局阵容不持久化。旧的匿名房间链接不再可用，需要登录并重新创建房间。
- 存储适用于单服务进程；运行账号管理脚本前请停止服务，完成后启动，避免多个进程同时写入。
- 对外部署请使用 HTTPS，并设置 `COOKIE_SECURE=1`；通过反向代理访问时可设置 `PUBLIC_ORIGIN=https://your-domain`，供 GSI 配置生成使用。不要公开分享个人登录密码。
- GSI 下载仅限房主，每个房间使用独立 GSI token，不能用邀请 token 替代；旧的无认证 GSI 配置需要重新下载。

### 验证

```bash
npm run typecheck
npm test
```

可选浏览器回归（需已安装 Playwright 和 Chromium；也可用 `PLAYWRIGHT_MODULE` / `PLAYWRIGHT_EXECUTABLE_PATH` 指向现有安装）：

```bash
node tests/room-settings.browser.mjs
node tests/hero-picker.browser.mjs
```

使用临时账号和模拟语音引擎验证房主改设置、全员自动重连、媒体清理及设置持久化，不访问生产房间。

英雄面板浏览器回归验证头像加载、分组、点选/键盘取消、搜索、人数上限、成员只读、GSI/模拟视觉同步、开麦不中断、手机布局与离线保护；截图默认保存在 `.tmp/hero-picker-*.png`。

权限回归测试使用临时数据目录和假的语音引擎，不调用 Foundry 或产生模型费用。

### 语音就绪与独立 GPT-Live 资源

- 房间已连接不等于语音服务已就绪；麦克风按钮会等待自己的引擎确认后才启用。首次初始化失败自动重试至多两次。
- 麦克风下方显示采集/音量/服务器接收状态；关闭开麦会释放输入设备。
- 可设置 `GPT_LIVE_FOUNDRY_ENV_FILE`，让 GPT-Live 单独使用另一份 Foundry 端点和密钥。其他同传引擎、TTS 和视觉识阵继续使用主资源，不共享或覆盖这份 key。
- `DEPLOYMENT_GPT_LIVE` 必须填写目标资源的实际部署名；仅在部署列表中可见不代表 Live 会话一定能启动。
- 排查步骤与诊断字段见 [`docs/voice-troubleshooting.md`](docs/voice-troubleshooting.md)。日志仅记录状态及帧计数，不记录录音、字幕或凭据。

### 刷野路线规划（实验版）

**100 场离线研究：** 已完整解析 100 场 7.41f Luna（50 场带职业标识），并通过独立 Astra API 逐场复盘；`/farming-replays?study=100` 提供分页地图、装备技能、线野分项收益与模型证据定位。详见 [`docs/luna-hundred-study.md`](docs/luna-hundred-study.md)。这是观察性研究，不是已启用的实战最高收益规划器。

**当前交互模拟台：** `npm run sandbox` 的默认首页现在是 200ms/帧的可操作地图。无英雄时兵塔自动交战；可加入 Luna、编辑六件装备和属性、攻击兵/野/塔、获取击杀金币，并拖动时间轴回退/分支。详见 [`docs/interactive-map-simulator.md`](docs/interactive-map-simulator.md)。原地图核验页仍在 `/map-audit`。这不是完整 Dota 物理或实战路线建议。

**本机引擎交叉核验：** 已直接核对用户客户端，并实测全图 104960 点地形、2475 棵树和局部移动/砍树机制。`map:client-audit` 可复查源文件与报告；网页新增 18 格通行差异和实际移速。静态点查询不是完整动态物理，仍不恢复路线建议。详见 [`docs/installed-client-map-validation.md`](docs/installed-client-map-validation.md)。

**地图核验入口：** 已实际下载/完整解析三场 V-Tune / Pure Luna 原始回放，核对固定 build、28 营地/刷新框和 22 塔坐标。带真实回放时间线的核验页保留在 `/map-audit`，旧示意机制页在 `/sandbox`。完整客户端级动态物理仍未全部通过；新的交互模拟不等于实战路线验收。复现与证据见 [`docs/map-replay-validation.md`](docs/map-replay-validation.md)。

**路线撤回 / 区域未核验：** 线野示例第 13 步实际触及夜魇营地；只查营地编号和最低血量不能证明路线安全。两个对照均已撤下，`sandbox:reason` 在云调用前拒绝，路线加载返回 409。`npm run sandbox` 只保留手动机制测试；`npm run sandbox:audit -- --check` 无云复现问题。详见 [`docs/sandbox-own-area-audit.md`](docs/sandbox-own-area-audit.md)。

**v2 线野目标：** 已增加兵线补刀、现存兵线、追逐归位、实际占框刷新、拉野/堆野；曾用真实模型比较提案。历史分数不能用来判定职业路线优劣或现实 GPM，等待允许活动区域与实机校验。保留的原始结果见 [`docs/dota-sandbox-economy.md`](docs/dota-sandbox-economy.md)。

**规划方式更新：** 运行链路现默认由独立大模型提出营地顺序、施法时机和目标优先级；没有模型配置/调用失败时不回退到规则搜索。新增 watson、Pakazs 等录像案例、配置与验证边界见 [`docs/luna-multi-replay-reasoning.md`](docs/luna-multi-replay-reasoning.md)。下文保留数值原型背景，旧测试包不代表这条新链路已部署或已实机验收。

2026-11-15 Windows 接续：新增本机 HUD 视觉校验接口、需标定的小地图鼠标穿透悬浮层、假腿属性确认、疯脸/主攻击吸血和逐单位死亡计钱。**这是可测试原型，不是完整技能建模或最高收益保证**；本地模型、真实游戏对齐与收益尚未验收。详见 [`docs/jungle-live-prototype.md`](docs/jungle-live-prototype.md) 和实际播放的 [`Ame Luna 录像记录`](docs/luna-pro-741f-observations.md)。

- 房间中新增默认关闭的刷野面板；房主/学员可开启，不影响同传连接。请重新下载 GSI cfg，以启用技能、物品字段。
- 使用固定 `7.41f` 快照中的营地坐标、刷新框、步行网格和战斗数据，模拟英雄攻击/部分技能、野怪护甲血量、蓝耗与承伤，按 **180 秒时间窗口**搜索连续路线，不限制为 3 个营地；支持整分刷新后多轮回访，已计入 Luna 月刃和 Sven 分裂。
- 优化目标是**安全约束内预计掉落总金钱最多**，不是清野次数最多；经验不折算成收益分数。页面显示总金钱，以及每个营地和沿途累计的预计金钱；限宽搜索不保证全局最优，实际收益取决于野点状态和数值模型。
- 支持手动标记已清空、按整分刷新、低血量停止和过期数据撤回；页面内 `Alt+J` 重新规划，可选本地中文音色朗读下一步。
- **尚未实机验收**：营地组合为代表样本，复杂技能/野怪技能和动态地形未完整建模；只用于自定义局验证，不保证竞技安全或精确收益。第一版不调用大模型进行规划，不使用观战隐藏信息。
- 来源、重建、GSI 私有录制/回放、模型边界见 [`docs/jungle-route-planner.md`](docs/jungle-route-planner.md)。
- 换机器继续 E2E、录像核验及尚未接通的数据/接口，先读 [`docs/jungle-e2e-handoff.md`](docs/jungle-e2e-handoff.md)；不要将检查点误认为完整装备技能模型。

```bash
npm run build:jungle
npm run replay:gsi -- /private/path/to/capture.jsonl
node tests/jungle.browser.mjs
```

## 双机客户端原型

**教练机视觉扫描（实验，独立工具）：** [说明](docs/hero-scan.md)。观战时让 Dota 每帧切换一次选中的英雄，从底部 HUD 读双方英雄的等级、血量、魔法、存活和装备，并从本机 GSI 小地图记最后出现位置；状态每名英雄约 0.3 秒刷新一次，装备默认每 3 秒一次（`--items-every` 可调，6 秒时状态约 0.17 秒）。结果按时间轴记录，可在本机沙盒 `/hero-scan` 回放并与完整观战真值对照验收。只用观战客户端自己可见的信息，不接入自动教练；Plus 好友实时观战尚未用真人对局验收。

**Plus 观战对手面板和提醒（未发布）：** [说明](docs/twopc-plus-alerts.md)。教练机 Plus 实时观战时，把敌方英雄的等级和装备（需同时运行视觉扫描）作为小图标面板一直显示在游戏机画面左侧，敌方离开视野时推送字幕；只用队伍视角可见的信息，旧的读取标出几秒前。面板需要新版游戏端。尚未用真人 Plus 对局验收。

**游戏端字幕和对手面板设置（未发布）：** [说明](docs/twopc-game-overlay.md)。游戏端可分别隐藏 AI 建议和离开视野的字幕，调整字幕的位置、字号、最大宽度、文字/标题/背景颜色和透明度，以及对手面板的开关、高度、大小和透明度；改动立即生效，在 Dota 画面上预览 8 秒，只存在游戏机本地。

**v0.4.0 视频与路线：** [说明](docs/twopc-v040.md)。游戏端可选 30/60/90/120fps（默认60）和传输分辨率（含原生，交给教练机缩放）。默认用窗口采集：Dota 在其他窗口后面也继续传 Dota 自己的画面。裁剪、缩放和转色在显卡上完成，优先用显卡硬件编码（NVENC/AMD/Intel），失败自动回退 CPU。教练端字幕默认显示3秒（可调），模型超时默认15秒；未校准时用默认左下小地图框；模型给出1–3个己方地标组成路线，游戏内画编号圆圈和“下一步”。远程桌面会话下系统只按约30Hz合成桌面，帧率会被限制。两端需同步升级。

**v0.3.1 Plus 观战（实验）：** [说明](docs/twopc-v031-plus.md)。教练端一键配置本机Dota的观战GSI，显示Plus实时观战的小地图数据，并用教练机自己的时钟测量它比游戏机慢多少；游戏端检查启动参数，并统计玩家模式有没有小地图。Plus数据只显示/记录，不进入自动教练；尚未用真实Plus观战验收。

**v0.3.0持续视频：** [架构和使用说明](docs/twopc-v030-video.md)。游戏端目标30fps H.264持续传输，教练端解码、独立预览与按需模型/Debug抽帧；不再在游戏端每秒上传JPEG。保留4K原始坐标（视频最大1600×900）与时效校验，两端需同步升级。软件编解码，不宣称NVENC或已通过双机4K实战性能验收。

**v0.2.1 Debug：** [调试记录说明](docs/twopc-v021-debug.md)。可分别开启两端的抽帧与状态落盘，保存模型输入/结果及提醒链路，凭会话和帧号追溯问题。默认关闭Debug，正常自动教练仍默认开启；不打包/上传调试图片、密钥或原始GSI。

**v0.2.0：** [正常使用与4K设置](docs/twopc-v020.md)。自动模型推理、字幕和地图目标默认开启，支持普通对局；首次配对、选择教练机私有模型配置、加载GSI，并确认实际小地图框后即可使用，无需验收脚本或Arm。保留过期撤回、调用预算及停止控制，不承诺最优路线或已完成跨机4K真人局验收。

v0.1.2历史验收（2026-11-16）：两个独立 Windows 程序——游戏机运行 `DotaCoach.GameClient`，后台机运行 `DotaCoach.CoachClient`。本地 Luna 机器人局已连续3次跑通真实画面/GSI → Foundry模型 → 游戏字幕与校准地图目标 → 过期清除；有实际屏幕像素证据。该版本普通实战自动路线关闭，低延迟、双物理机器和GPM效果未验收。见 [双机原型](docs/twopc-prototype.md)、[真实模型E2E报告](docs/twopc-model-e2e.md)。
