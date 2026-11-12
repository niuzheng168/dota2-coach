# DOTA2 Bilingual Voice Coaching Bridge (中英同传语音室)

专为 **英文 DOTA2 教练 ↔ 中文学员** 实时连麦设计的低延迟双语同传语音室，对接 **Microsoft Foundry / Azure Voice Live**，支持在网页内一键热切换 **三种主流语音同传架构** 与 **免切屏双通道自动识阵**：

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
2. 执行 `az login`（默认通过 `AzureCliCredential` / `DefaultAzureCredential` 自动获取 Entra ID 令牌，无需配置任何静态 API Key）：
   ```bash
   npm install
   npm start
   ```
3. 打开浏览器访问：
   - **学员端（说中文或基础英语 → 听中文）**：`http://localhost:3210/?room=roshan-pit&role=student`
   - **教练端（说英文 → 听英文）**：`http://localhost:3210/?room=roshan-pit&role=coach`
