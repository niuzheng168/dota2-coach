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

## 免切屏本局阵容自动锁定（Dual-Channel Auto-Draft）

开局前打开网页与教练连麦后，进入 DOTA2 选人/对局无需切屏手动点选英雄：

1. **官方 Valve `Game State Integration (GSI)`（零封号风险 · 自动感知开局/换局）**
   - 点击页面左侧 **⬇ GSI .cfg** 下载配置文件并放入 `steamapps/common/dota 2 beta/game/dota/cfg/gamestate_integration/`。
   - 自动检测新 `matchid` / `HERO_SELECTION` 清空上一局阵容；玩家位自动上报本机英雄，**Dota Plus 实时观战位 / 教练位**自动实时上报 10 人阵容并热更新同传会话。
2. **浏览器窗口顶栏视觉识阵（`gpt-6-luna` Vision · 10 英雄全识别）**
   - 开局前点击一次 **🖥️ 开启窗口自动识阵** 共享 DOTA2 窗口，网页后台每 12 秒仅裁取顶部 `18%` 头像栏并通过本地像素差分门控过滤静止画面，在阵容变化时自动识别天辉/夜魇 10 位英雄并 **0ms 不断麦热更新**全技能词表。

---

## DOTA2 全量双语词典与双层领域注入

仓库内置从 Valve 官方接口（`dota2.com/datafeed` 英文 + 简体中文）拉取并结合中美高分局常用语音黑话整理的完整双语词典：

- **人类可读完整对照文档**：[`docs/dota2-bilingual-dictionary.md`](docs/dota2-bilingual-dictionary.md)
- **结构化词典数据**：[`data/dota2-dictionary.json`](data/dota2-dictionary.json)
  - **127 位全英雄** + 英文缩写与中文常用外号（含 `猴子=Phantom Lancer` vs `大圣=Monkey King`、`小牛/大牛/白牛`、`小鱼/大鱼`、`四猫`、`黑鸟/小黑/黑贤` 等严格消歧规则）
  - **734 个全英雄技能**（含先天技能 Innate 及最新英雄如 `Largo 朗戈`、`Kez 凯`、`Ringmaster 百戏大王` 全技能）
  - **402 件全商店装备、消耗品、肉山掉落物、中立道具与附魔**
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
   - 同传设置创建后固定；房主可随时在「管理允许加入的用户」调整名单、复制邀请链接。
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

权限回归测试使用临时数据目录和假的语音引擎，不调用 Foundry 或产生模型费用。

### 语音就绪与独立 GPT-Live 资源

- 房间已连接不等于语音服务已就绪；麦克风按钮会等待自己的引擎确认后才启用。首次初始化失败自动重试至多两次。
- 麦克风下方显示采集/音量/服务器接收状态；关闭开麦会释放输入设备。
- 可设置 `GPT_LIVE_FOUNDRY_ENV_FILE`，让 GPT-Live 单独使用另一份 Foundry 端点和密钥。其他同传引擎、TTS 和视觉识阵继续使用主资源，不共享或覆盖这份 key。
- `DEPLOYMENT_GPT_LIVE` 必须填写目标资源的实际部署名；仅在部署列表中可见不代表 Live 会话一定能启动。
- 排查步骤与诊断字段见 [`docs/voice-troubleshooting.md`](docs/voice-troubleshooting.md)。日志仅记录状态及帧计数，不记录录音、字幕或凭据。
