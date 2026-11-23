# 给代码代理的约定（Codex、Claude 等）

## 用户定下的双机行为（改之前先问用户）

下面每一条都是用户明确要求过、并且被改坏过的行为。每条都有测试；测试失败说明行为被改了，**不要改测试的预期去迁就代码**。

1. **游戏端不往教练端传画面。** 正式版游戏端只有一种连接：发本人 GSI 和游戏窗口尺寸，收字幕、对手装备面板、眼位和分钟路线。本机小兵血量独立开关，连接不会打开或关掉它。视频、诊断、"仅本人 GSI"等模式只在 `Features.VideoUpload`（UI 测试）和本地机器人局 E2E 下出现。
   - 测试：UI `--game-controls`、`--layout`；控制台 `AuthorizedBoardTests`（`Features.VideoUpload` 默认关）。
2. **对手装备面板一直显示到本局结束。** 教练端 `LastKnownBoard` 保留每个敌方英雄上次读到的等级和装备，读数时间只往上加；扫描写晚了、比本人 GSI 早一秒、扫描停了或删了、Plus 断包，都不能把面板撤掉。只有换局、换阵营、本局结束才清空。游戏端每次回复都显示到下一次回复。v0.6.1 曾因逐个读数做严格时间校验，一局 20 分钟撤回 378 次。
   - 测试：控制台 `AuthorizedBoardTests.KnownBoard`；UI `--team-intel` 里 "a scanner write a second ahead…"、"a stopped scanner…"、"a deleted scanner file…"。
3. **Plus 队伍视角比本人 GSI 早一秒是常态**（`TeamViewEvidence.MaxLeadSeconds = 2`），不是另一条时间线，不能因此重置队伍视角。v0.6.1 一局重置 1180 次（每个游戏秒一次）。
   - 测试：控制台 `TeamViewEvidenceTests`；UI `--team-intel` 里 "a Plus packet one second ahead…"。
4. **眼位和字幕不依赖画面。** 教练端为当前分辨率确认过小地图框时按框画眼位，否则按游戏端「设置 → 游戏内显示」的小地图位置和大小画。离开视野等不带标点的字幕、链路测试字幕经本人 GSI 连接送到游戏端。
   - 测试：UI `--participant-board`、`GameOverlay`；`--team-intel` 里 "an enemy leaving vision is pushed as a subtitle to the GSI-only game client"；控制台 `MinuteFarmProtocolTests`。
5. **界面：主页只放状态和常用开关，其余进「设置」分组。** 教练端分组：连接 / Plus 观战与扫描 / AI 模型 / 小地图校准 / 调试；游戏端：游戏内显示 / 小兵血量 / Dota 与 GSI / 调试（UI 测试另有「开发测试」）。新加的设置放进合适的分组，不要堆回主页；分组用 `desktop/TwoPc.SharedUi/AppPages.cs`。
   - 测试：UI `--layout`（同时把两端每一页渲染成 PNG 方便看）。

## 提交前

在仓库根目录运行（相对路径依赖根目录）：

```powershell
dotnet run --project desktop/DotaCoach.TwoPc.Tests -c Release
dotnet run --project desktop/DotaCoach.TwoPc.UiTests -c Release -- .tmp/twopc-ui-<新目录>
```

UI 测试会打开真实窗口；每次给一个**新的空输出目录**（目录里已有 `debug` 时 Debug 检查会失败）。玩家正在打比赛或观战时不要跑。单项：`-- --layout <目录>`、`-- --game-controls`、`-- --team-intel <目录>`、`-- --participant-board`。只改界面时至少跑前两项。

## 发布

`desktop/build-twopc.ps1` 会先跑全部测试再打包（不覆盖已有输出目录；先提交，`sourceDirty` 才是 false）。发布后把**解压的** `DotaCoach.GameClient\`、`DotaCoach.CoachClient\` 和 `manifest.json` 复制到 `Z:\dota2coach\twopc\vX.Y.Z\`，不放 zip，逐个文件核对 SHA-256，不覆盖已有版本目录。
