# Plus 观战 GSI：开源实现交叉复核与归因边界

2026-11-16，香港时间。用户质疑首轮空字段可能是配置/处理错误，并提供
`antonpup/Dota2GSI`。本次没有重新启动 Dota、没有重新实机采样，没有调用付费模型。

## 修正结论

**不能说“Plus 观战没有有效 GSI 数据”，也不能把一次空字段直接归因成 Plus 的产品限制。**

目前证据支持：

1. 首轮确实保存了有效地图信息：时钟/比分、单位名称/阵营/坐标、部分建筑血量等。
2. 752 个对局包的 `hero` 都是 `{"id":0}`，`items` 和 `abilities` 都是 `{}`；
   `player.team_name` 为 `spectator`，不是所选英雄。
3. 不存在被我们漏读的 `hero/items/abilities/player.team2/team3/playerN`。
   同样检查了 `previously` 和 `added`，没有找到历史英雄/装备/技能数据。
4. 同一批**脱敏样本**交给另一实现，仍得不到所选英雄状态；这排除了
   “仅仅是我们解释 JSON 的方式不对”的主要可能，但不能追溯证明每个采集前步骤都无误。
5. 我们的记录器确实有过 couriers 误脱敏、事件内容过度脱敏和组件配置未完全对齐；
   这些是自己的问题，不能都归咎于 Dota。
6. 第一轮未保存未处理的 HTTP 正文或脱敏前摘要，因此**不能声称已完成原始接收层取证**。
   需要相同配置下的普通观战/Plus 对照，才足以进一步归因。

## 开源实现到底做了什么

已实际克隆并固定代码：

```text
Repository: https://github.com/antonpup/Dota2GSI
Commit: c253a7c26a831c4055eb2a29a290498116aad462
Commit date: 2024-02-07
```

重点文件：

- `Dota2GSI/GameStateListener.cs`：接收 HTTP 正文，`JObject.Parse` 后构造 `GameState`。
  没有从 Steam 拉取缺失英雄数据，也不改变游戏发送的字段。
- `Dota2GSI/Nodes/Hero.cs`、`Player.cs`、`Abilities.cs`：兼容扁平状态与
  `teamN/playerN` 多玩家状态。
- `Dota2GSI/Dota2GSIFile.cs`：配置生成器。
- `Dota2GSI/GameState.cs`：`IsSpectating` 根据是否有 team 分组判断，不是订阅/权限证明。

README 的“观战时可有全部玩家数据”是一般性说明，不是 **7.41f Plus 好友实时观战**
每种场景的接口保证。

## 实际运行的独立解析对照

没有只阅读 README 下结论。使用未修改的 `GameState.cs` 和全部 `Nodes/*.cs`
编译了一个只读控制台检查器，依赖本机 .NET SDK 的 Newtonsoft.Json 13.0.3。
不执行上游构建脚本、不运行其 HTTP 监听器、不生成游戏配置。

输入是首轮固定证据的两份日志快照，逐文件校验 SHA256。
由于记录里的 `map.matchid` 是隐私占位字符串，上游 `Convert.ToInt64` 无法读取，
因此仅在内存移除了这个占位字段。`hero/player/items/abilities/minimap` 五块用
`JToken.DeepEquals` 确认未改动。

| 检查 | 结果 |
|---|---|
| 对局包数 | 752 |
| Player/Hero/Items/Abilities 的 team 分组数 | 全部 0 |
| Hero 本地 ID/Name/Health | 每包 `0 / "" / -1`，其中 `-1` 是该库的缺失值 |
| 本地库存、技能列表 | 每包为空 |
| Player.Team | 每包 `Spectator` |
| Minimap.Elements | 每包 145–211，和我们独立统计一致 |

### 阳性对照，防止“解析器本来就坏了”

另外构造明确包含信息的**合成测试输入**，不冒充实机数据：

- 扁平 Luna：血量 700、1 件装备、1 个技能均能解析。
- 嵌套天辉/夜魇：Luna 血量 700、Axe 血量 1500、Luna 金币 1000、
  1 个技能均能解析，`IsSpectating=true`。
- 我们自己的脱敏器另用完整 10 玩家结构测试，英雄、装备、技能的整个 JSON
  与输入一致，天辉/夜魇各 5 人结构不被扁平化。

### 也发现了该库自身的两个边界

这不是把责任转给开源项目，而是说明不能盲信“换库”：

1. 对本次扁平 `team_name=spectator`，上游报告 `IsSpectating=false`、
   `IsLocalPlayer=true`。这个辅助属性不能作为“这是本人可用数据”的依据。
2. 固定版本的 `Items.cs` 在进入 team 对象后，用外层 `parsed_data`
   而非该 team 的 `obj` 查 `playerN`。嵌套装备阳性对照期望 1 件，实际 0 件。
   因此**不能仅凭该库输出 0 件来证明游戏没发送装备**；首轮结论依赖的是保存的 JSON
   本身就是空对象，而不是这个有缺陷的嵌套装备解析分支。

## 配置差异及已修复内容

该库生成器的 `data` 共 16 项。首轮请求 14 项，少了：

- `league`
- `data.auth`

**首轮并非没有鉴权**：根级 `auth.token` 存在，接收器已校验它。
`data.auth` 是数据组件请求，与根级 token 配置不能混淆。
英雄、玩家、装备和技能的请求项首轮都已启用；目前没有证据证明缺少这两项
导致它们为空，但为了排除差异，现已补齐 16 项。
缓冲/采样间隔仍用有界审计设置，不宣称时间参数也与上游示例逐字相同。

记录器 v3 的改动：

- 保留已修复的 couriers 字段，保留引擎图标和类名。
- 保留上游文档中的已知事件枚举；遇到 JSON 字符串事件数据，仅提取已知游戏数值/
  布尔字段并标注 `__auditSourceType: "json-string"`。未知文本、聊天和身份信息继续隐藏。
- **在脱敏函数执行之前**记录字段类型、对象成员数、team/player 分组数、
  白名单标量及 hero/items/abilities 的 SHA256，之后记录对应落盘结果用于比对。
- 新证据能区分 `missing`、`{}`、`null`、明确数值 0 和处理前后变化。
  不保存 auth/账号/昵称原值或完整 HTTP 正文。
- 新增真实回环 HTTP 检查，验证摘要来自处理前，而不是从处理后结果反推。

这些是**代码与合成测试通过**，不是已完成新的 Plus 实测；
不能用 v3 的验证结果补写第一轮不存在的原始证据。

## 下一次最有区分力的测试

使用对齐后的组件请求，分别观察：

1. 自己操控英雄的演示/受控局，验证正常英雄、装备、技能能收到。
2. 普通观战，验证是否返回 `teamN/playerN` 分组。
3. Plus 好友实时观战，与前两种作对照。

每种模式需同时记录接收层摘要、脱敏后包、独立解析结果及模式截图；
若增加第二监听器，应让它独立接收 Dota 的 HTTP，而不是再次解析我们的清洗结果。
暂不要求用户重开游戏，也不将待证的观战数据接入普通教练。

## 验证与本机复现

- **198 项 Node 测试、TypeScript 类型检查通过**。
- 12 项审计专项检查，包括 10 玩家结构、处理前摘要、事件脱敏、
  HTTP 鉴权、CLI 清理和热接续。
- 上游解析器离线检查已实际成功运行。
- 外部源码、检查器与报告在被忽略的 `.tmp/`，不随 Git 迁移：

```text
.tmp/references/Dota2GSI-20261116/
.tmp/gsi-forensics-20261116/ReferenceProbe.csproj
.tmp/gsi-forensics-20261116/Program.cs
.tmp/gsi-forensics-20261116/reference-probe-result.json
.tmp/gsi-forensics-20261116/structure-and-config.json
```

在当前机器的仓库目录重跑：

```powershell
dotnet run --project .tmp/gsi-forensics-20261116/ReferenceProbe.csproj --configuration Release -- .tmp/plus-observer-audit-20261116-230445-v2/e2e-evidence-BLYBa3 .tmp/gsi-forensics-20261116/reference-probe-result.json
node --test tests/observer-audit.test.ts
npm run typecheck
```
