# Windows 本地监听助手

> **v0.2.0 已改为本地界面 + C# 独立音频后台。** 当前使用、结构、限制和测试请看 [`desktop-v020.md`](desktop-v020.md)；以下保留 v0.1.x 的部署与排障历史。v0.2.0 不再直接加载线上网页，也没有顶部服务地址栏。

## 使用方式

1. **线上服务已于 2026-11-14 部署桌面监听支持**，关闭并重新打开助手即可加载新页面。自部署用户仍需先更新并重启服务端，让网页和服务端都包含 `desktop-listener.js` / `ProcessListenEngine`；只更新助手不能更新服务端。
2. 把 `DotaCoach-v<版本号>-win-x64.zip`（旧版名称为 `DotaCoach-win-x64.zip`）**完整解压到可写目录**，运行 `DotaCoach.Desktop.exe`。需要 Windows 11 x64 和 Microsoft Edge WebView2 Evergreen Runtime；发布包包含 .NET 运行时，不需要另装 .NET，也不需要虚拟声卡或管理员权限。
3. 助手默认打开线上服务 `https://dota2.largeniu.com/`，确认地址后使用现有账号登录、创建/加入房间。顶部「连接默认服务」切回这个默认网址；「打开此地址」打开地址栏填写的网址（Enter 等效）。地址本来就是默认网址时，两者结果相同，**不需要依次点两个按钮**，也都不是开始监听的按钮。也可填写**你信任的其他同传服务地址**；公网地址要求 HTTPS。**无需在你的 Windows 电脑上运行服务端。** 只有本地开发、已启动服务端时才使用 `http://localhost:3210/`。邀请链接也可以填入地址栏，助手只保存成功连接的站点地址，不保存邀请链接到地址配置。
4. 原有 App 连麦按钮照常使用。展开 **「桌面双语字幕 · 外部程序监听」**：
   - 只需要 App 内的双语悬浮字幕：打开「置顶双语字幕」，不用开启进程采集。
   - 需要游戏/YY 监听：打开目标软件，刷新进程，选择 `dota2.exe` 或 YY 的实际音频进程；核对 PID。第一版一次监听一个进程树。
   - 确认已告知对方声音将上传到云端，点击「开始监听」，再在**原生确认弹窗**确认目标程序。
5. 选显示器，调整原文/译文颜色、文字/背景不透明度、字号、**字幕垂直位置**和停留时间，点「预览字幕」。在 v0.1.3 或更新助手中，`0%` 顶部、`50%` 居中、`100%` 底部；按字幕面板实际高度定位并保留 8 DIP 的上下安全边距。选择底部后，字体或换行变化时字幕仍保持底部对齐。
6. 游戏优先使用**无边框窗口模式**。字幕窗口置顶、鼠标穿透、不主动获取焦点；独占全屏、安全桌面以及某些全屏优化组合不保证能覆盖。这里不是注入游戏的 Overlay。

字幕设置保存在助手 WebView 的站点 localStorage；账号会话保存在该 WebView 独立的用户数据目录，与普通浏览器登录互不共享。助手本地数据目录为 `%LOCALAPPDATA%\DotaCoach`。

## v0.1.3：字幕完整垂直范围与清晰的地址按钮

- 修复原滑块、网页设置和原生助手三处上限均为 60%，最右端只能移动到屏幕中下部的问题。
- 新滑块范围为 0–100%，含当前百分比与顶部/居中/底部提示。新算法用选中显示器的可用高度减去**实际测量的字幕面板高度**，不再按固定的大透明窗口定位；100% 时面板底边贴近屏幕底部，不把文字推出屏幕。
- 文本变化、换行、字号、显示器或 DPI 变化后重新测量布局；保留置顶、鼠标穿透和不激活窗口的行为。几何回归覆盖 1366×768、1080p、1440p、4K、多种缩放与负坐标副屏，Windows 实际渲染仍需实机确认。
- 原「使用线上服务」改名「连接默认服务」；原「打开同传服务」改名「打开此地址」，按钮悬浮提示说明差别以及重新导航会停止采集。
- 完整底部定位需要**v0.1.3 助手和配套新网页同时更新**。新网页若连接旧助手，会明确显示旧版 0–60% 限制和升级提示，不冒充支持底部定位；暂时使用旧助手不会覆盖保存在本机的新版 100% 位置偏好。
- 安装包为 `/nas/share/DotaCoach-v0.1.3-win-x64.zip`，另附同名 `.sha256`，不覆盖 v0.1.2。关闭旧助手后完整解压新版再运行，窗口标题应显示 v0.1.3。

## v0.1.2：配套监听服务端上线

- 2026-11-14 上线 `listener-v0.1.2-20261114-r2`，包含网页监听面板、桌面消息桥、私有音频 WebSocket 和转录/翻译引擎。不再只发布本地助手而遗漏线上网页。
- **v0.1.1 助手仍兼容**：关闭并重新打开助手，连接默认线上地址，进入房间，展开「桌面双语字幕 · 外部程序监听」。正常握手后原来的黄色兼容性提示会消失。
- 新版包为 `/nas/share/DotaCoach-v0.1.2-win-x64.zip`，附同名 `.sha256`；窗口标题和包版本统一由项目 `<Version>` 生成，保留旧包。
- 服务端必须先发送 `listener.config`，明确协议版本、私有通道、纯文字输出和采样格式，网页才允许本地助手开始采音。误连旧服务端时直接阻止采集，避免外部声音被当作普通房间麦克风广播。
- 修复实际接口测试发现的 `DeploymentNotFound`：监听文字翻译默认使用已验证的 `gpt-6-luna`，可用 `DEPLOYMENT_LISTENER_TEXT` 覆盖，不再沿用旧 Cascade 默认的 `gpt-6-luna-datazone-standard`。
- 本地与生产候选镜像各 47 项测试通过；三套本地浏览器回归通过。公网实际 HTML/JS/CSS 验证了桌面握手、监听入口、字幕设置和私有聊天配对；该浏览器验收模拟原生桥/API/账号，不操作真实业务房间。
- 使用生产资源的只读配置、隔离容器进行真实云端验证：三种 ASR 均建立转录会话；合成英语音频完成「ASR → 英语筛选 → 英文原文/中文译文」，合成中文音频被过滤。未读取或修改生产账号/房间数据。
- Windows 真实进程采音、游戏中鼠标穿透与多显示器表现仍需实机验收，不能把云端合成音频测试等同于 Dota 2/YY 全流程实测。

## v0.1.1：修复首次启动连接 localhost

- 旧包把开发地址 `http://localhost:3210/` 作为默认地址，且连接前就保存了它。用户的 Windows 电脑没有运行服务端时，会显示 `ERR_CONNECTION_REFUSED`；这不是 exe 无法启动或缺少 WebView2。
- **旧包立即恢复方法**：把顶部地址改成 `https://dota2.largeniu.com/`，点击「打开同传服务」并确认。
- v0.1.1 默认连接线上服务；顶部和错误页均提供「使用线上服务」，地址栏支持 Enter。网络、HTTP 或证书错误会显示助手自己的说明与重试入口，不再只显示浏览器错误页。
- 升级时自动跳过旧 `server.txt` 中误存的 `localhost:3210` 默认值，其他自定义服务地址保留。新版本只在页面成功加载后保存到 `connected-server.txt`；显式命令行地址和新版已验证的本地开发地址仍优先。
- 页面加载成功但没有响应桌面消息桥时，助手会提示可能需要更新服务端或检查页面脚本；**网页能打开不等于外部监听功能已上线**。
- 2026-11-14 本次修复核对：线上主页与登录会话检查返回 200，但线上尚无 `desktop-listener.js`。本次只更新本地助手包，未部署监听服务端。
- 更新时先关闭旧助手，把 `/nas/share/DotaCoach-win-x64.zip` 完整解压到新目录再运行；标题应显示 **v0.1.1**。不要只替换 exe，也无需删除账号或浏览器数据。

## 第一版的边界

- **只监听 / 显示，不代发语音。** 外部声音的中文翻译不做 TTS，不写入游戏麦克风，也不转给教练。原有 App 内语音翻译/播放照常工作。
- 所选进程与其子进程由 Windows 原生 process-loopback 接口一并采集。不抓网络包、不读取游戏内存、不注入、不安装驱动；这不是对任何反作弊规则或所有系统组合的兼容性保证。
- 不会自动降级为全系统录音。失败会显示错误并停止，避免意外采集其他软件。
- 采集的是**程序混合输出**：队友、教练、英雄台词、音乐、音效都可能包含其中。语音识别 + 语言筛选不能可靠区分真实人声与游戏角色英文台词，也不能可靠识别某一位教练。
- ASR 不强制英文，先自动识别语言，再通过文字模型筛选主要为英语的发言、生成中文翻译。非英语和未分类的中间转录不进入记录/字幕。误识别和误筛选仍可能发生。
- **外部监听按短句/短块更新，不是逐字即时同传。** 当前使用 200ms 前置音频、约 550ms 静音切句、最长 4 秒强制切块。静音/短噪声门控为能量启发式，不是神经人声分离。持续游戏背景声音会触发最长切块，增加延迟并可能截断句子；建议先降低游戏音乐/音效实测。
- App 字幕沿用原有增量转录/译文，外部监听则在语言筛选和翻译完成后，配对追加英文原文与中文译文。
- **监听记录只返回发起监听的连接，不广播房间成员。** 本页聊天标注「外部监听（仅自己可见）」。第一版不保存录音、不持久化聊天；离开房间清空聊天，单页最多保留 300 张对话卡。停止监听会保留本房间已显示的聊天，但清除悬浮字幕。
- 退出软件、目标进程退出、网页导航/刷新、离开房间、退出登录、撤销房间授权或更改房间同传设置都会结束监听。网络/识别/翻译失败或积压时停止，不悄悄自动重新开启录音；恢复后手动重新开始。
- 进程重启导致 PID/启动时间变化，需要刷新并重新选择。助手排除自己的进程树及祖先进程，避免选中父进程后把自身声音采入。

## 服务端依赖与费用

外部监听单独创建一个鉴权 WebSocket，**不替换房间的麦克风连接**：

```text
目标进程树
  → Windows ActivateAudioInterfaceAsync / IAudioCaptureClient
  → 24kHz 单声道 PCM16（Windows 自动采样率转换，60ms 数据包）
  → 受信任站点的 WebView2 消息桥（不开放 localhost HTTP/WS 监听端口）
  → /ws?inputKind=process（现有账号、邀请、房间授权）
  → Voice Live transcription-only（自动语言检测，无 TTS）
  → 英语筛选 + 中文翻译
  → 本人聊天记录 + 原生置顶字幕
```

- ASR 使用房间选择的 `azure-speech` / `mai-transcribe-2-streaming` / `mai-transcribe-2`，通过主资源的 Voice Live transcription-only 端点访问。
- 筛选与翻译使用主资源 `DEPLOYMENT_LISTENER_TEXT`（默认 `gpt-6-luna`）的 Chat Completions，要求支持 JSON 对象输出；Dota 词表及本局英雄上下文继续注入。独立于旧 Cascade 的 `DEPLOYMENT_GPT_TEXT`，不要自行附加 `-datazone-standard` 等部署名后缀；指定的名称必须在资源中实际存在。
- 无论 App 语音房间选择哪种引擎，**外部监听固定走上述文字管线**，不会转用 GPT-Live 独立资源。
- 原始采集音频会发送到云端 ASR。监听是额外的 ASR/文字模型使用，可能产生费用；无音频不等于完全没有会话费用。服务端不把密钥发到桌面端。
- 本地消息与 WebSocket 都设了积压边界；ASR 最多两个未完成提交，翻译等待队列最多三段。积压时停止而非积累几十秒的过期字幕。

## 构建

开发者安装 .NET 10 SDK，在仓库根目录的 PowerShell 中执行：

```powershell
./desktop/build.ps1
```

输出为 `dist/windows-listener/DotaCoach-v<版本号>-win-x64.zip`，以及同名 `.sha256` 校验文件。这是**未签名的测试包**，不建议把绕过系统安全检查作为常规安装方式；公开发布前应完成代码签名和 Windows 实机验收。

**包交付目录固定为 `/nas/share`。以后每次生成 Windows 安装/测试包，都要把 ZIP 和对应 `.sha256` 校验文件复制到这里。** `desktop/build.ps1` 默认自动复制并验证 SHA256；共享目录未挂载时会报错，不会静默跳过。若在 Windows 上构建，请用 `-ShareDirectory` 指定同一 NAS 共享的 UNC 路径或映射盘路径。

**文件名必须带版本号，每次新发布递增版本，不再用无版本文件名覆盖旧包。** 版本统一读取 `desktop/DotaCoach.Desktop/DotaCoach.Desktop.csproj` 的 `<Version>`，窗口标题也从该版本生成。例如 `0.1.3` 交付为 `/nas/share/DotaCoach-v0.1.3-win-x64.zip` 和 `DotaCoach-v0.1.3-win-x64.sha256`。共享目录已有同版本文件时，脚本拒绝覆盖，保留历史版本；发布前先更新 `<Version>`。

也可单独编译或调试：

```powershell
dotnet run --project desktop/DotaCoach.CoreTests -c Release
dotnet build desktop/DotaCoach.Desktop -c Release
dotnet run --project desktop/DotaCoach.Desktop -- https://your-coach-service.example
```

跨平台构建机器可以编译/发布该 Windows 目标，但不能据此声称 Windows 运行行为已验收。完整发布包须保持文件结构，不能只复制 exe。

## 自动化验证

```bash
npm run typecheck
npm test
node tests/desktop-listener.browser.mjs
node tests/room-settings.browser.mjs
dotnet run --project desktop/DotaCoach.CoreTests -c Release
dotnet build desktop/DotaCoach.Desktop -c Release
```

浏览器测试需要 Playwright/Chromium，也支持仓库现有 `PLAYWRIGHT_MODULE` / `PLAYWRIGHT_EXECUTABLE_PATH`。它模拟本地消息桥和语音服务，覆盖同页连麦与监听并存、进程切换、原文译文配对、设置、迟到帧丢弃和退出清理，**不替代真实 Windows 音频/桌面测试**。

服务端测试验证未登录/跨站/未授权拒绝、监听记录隔离、无 TTS、独立麦克风路由、撤权和设置变更清理；引擎测试不访问云端。C# 核心测试验证消息来源策略、PCM 分包和 Windows ABI 结构布局。

## Windows 实机验收清单（尚需执行）

- [ ] 在助手内登录后能正常 App 连麦；普通浏览器功能仍可用。
- [ ] Dota 2 与其子进程的实际声音能采到；另一程序同时发声不会进入同一采集。
- [ ] YY 若音频由独立兄弟进程播放，改选正确进程或共同父进程后可采到。
- [ ] 英文人声显示原文 + 中文，纯中文/静音不生成无关记录；实测噪声与游戏台词干扰。
- [ ] App 麦克风与外部监听同时运行；停止其中一个不停止另一个。
- [ ] 1080p / 1440p / 4K、100% / 150% / 200% 缩放、副屏负坐标下字幕位置正常。
- [ ] 在无边框 Dota 2 中，点击字幕所在区域仍操作游戏，键盘焦点不被抢走。
- [ ] 全屏模式、Alt-Tab、最小化助手后真实测试；不支持的组合给出使用无边框窗口的建议。
- [ ] 断网、关进程、退出登录、关闭窗口、撤权后不再采集，恢复后不自动重新录音。
- [ ] 使用实际 Azure 部署验收 transcription-only、语言筛选、翻译结果与延迟。

## 官方接口依据

实现前核对的主要文档：

```text
Microsoft: Application loopback audio capture sample
https://learn.microsoft.com/en-us/samples/microsoft/windows-classic-samples/applicationloopbackaudio-sample/
Microsoft: Voice Live real-time transcription
https://learn.microsoft.com/en-us/azure/ai-services/speech-service/how-to-voice-live-realtime-transcription
Microsoft: WebView2 security
https://learn.microsoft.com/en-us/microsoft-edge/webview2/concepts/security
```
