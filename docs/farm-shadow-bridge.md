# 7.42 本地影子输入桥：本人 GSI ＋ 亲眼目击

**用途：在自定义局或本人视角复盘演练中，检查“此刻看到了什么、影子研究是否应当弃答”。不是游戏控制器。** 页面可以显示本体的英雄、阵营、游戏时钟、坐标、等级、血蓝、正补、已上报的主栏装备与技能；操作员另行标记可见兵线/营地、现场为空或未知、局部区域及危险。没有区域证明就不提供走法，即使显示历史重合，`action` 也始终为 `null`。

**本次验收到真实 HTTP 和浏览器为止，发送者是合成夹具，没有接入一场真实 Dota 对局。** 不需要 Steam 登录、云密钥、现有沙盒或桌面助手。程序不找安装目录、不改 GSI 配置、不抓屏、不读内存、不发键鼠、不起 overlay；不记录原始包或 token。

## 一条命令试通（无需游戏）

在仓库根目录，用仓库已有的 Node 运行：

```powershell
node scripts/farm-shadow-bridge.ts --demo
```

它自己申请一个空闲的 `127.0.0.1` 端口，生成两枚新 token，发送合成玩家 GSI，确认演练，提交 `lane:bot` 现场有兵、`good_1` 现场为空，然后读取经鉴权的影子结果。最后停发超过 5 秒，验证 `STALE_GSI`、当前本体/影子输出清空，关闭**自己的**监听。不能指定转发目的地，不会接触已有端口。`--demo` 固定使用 16 场归档，只检验数据链路，不评估策略表现。

## 实际操作网页：PowerShell

**终端 A** 启动一个独立实例：

```powershell
node scripts/farm-shadow-bridge.ts --mode rehearsal --port 0 --corpus fixed
```

打开打印的 `http://127.0.0.1:<端口>/`，在页中输入 **Operator token**。终端同时打印的 **GSI token** 只给本人 GSI 发送端。两者不能互换；都不要放 URL、聊天、截图或持久文件。新进程有新 token。端口被占用会失败，不会接管旧进程。

**终端 B** 以下是连续约 90 秒的**合成发送者**，给操作员留出点页面的时间。也可随时 Ctrl+C 只停发包。示例的 build 和开局时间是为虚构场景给定的值，**不能拿来确认真实对局**。

```powershell
$portNo = [int](Read-Host '终端 A 打印的端口')
$base = "http://127.0.0.1:$portNo"
$gsi = Read-Host '本次 GSI token'
$fakeStart = [DateTimeOffset]::UtcNow.ToUnixTimeSeconds() - 660
Write-Host "仅此合成演练：build=6986，matchStartUnix=$fakeStart"
for ($i = 0; $i -lt 180; $i++) {
  $sec = [DateTimeOffset]::UtcNow.ToUnixTimeSeconds()
  $packet = @{
    auth = @{token=$gsi}; provider = @{name='Dota 2'; appid=570; version=48; timestamp=$sec}
    map = @{matchid='742000001'; game_state='DOTA_GAMERULES_STATE_GAME_IN_PROGRESS'; clock_time=($sec-$fakeStart); paused=$false}
    player = @{team_name='radiant'; activity='playing'; last_hits=80}
    hero = @{name='npc_dota_hero_luna'; xpos=1100; ypos=-5400; level=9; alive=$true
      stunned=$false; disarmed=$false; hexed=$false; silenced=$false
      health=(900-($i%15)); max_health=1100; mana=400; max_mana=520}
    items = @{slot0=@{name='item_power_treads'; cooldown=0}}
    abilities = @{ability0=@{name='luna_lucent_beam'; level=2; cooldown=4}}
  }
  $json = $packet | ConvertTo-Json -Depth 6 -Compress
  Invoke-RestMethod -Method Post -Uri "$base/gsi" -ContentType 'application/json' -Body $json | Out-Null
  Start-Sleep -Milliseconds 500
}
```

1. 页面应显示 `742000001`、天辉露娜、从 11:00 开始的时钟，以及随包变化的 HP。没有本人帧时，两个输入表单不可用。
2. **仅在这个虚构演练中**填 `6986` 和终端 B 打印的 `matchStartUnix`，核对视角，点「确认本局」。故意留空也可以：此时只看输入，状态解释未知的是版本还是开局时间。
3. 模拟目击 `lane:bot`：选「此刻看见兵或野怪」、「此刻亲见为己方活动段」、「此刻未看到危险」，核对时钟后提交。应显示本次目击；是否有历史重合取决于输入和匹配到的独立样本。**未看到危险不是安全证明。**
4. 把同一目标改成「现场确实为空」或区域「未核对」再提交：旧候选不能保留。点「看到危险，立即撤回」立即取消。网页每次提交替换整个目击集；API 支持一次最多 32 个不同目标。
5. 停终端 B：无人操作也会撤回。页面「断开」仅忘记本页 token；「终止桥接」清空并锁住本进程，之后 GSI 返回 `410`，不能通过 API 再开启。终端 A 按 Ctrl+C 释放监听。

### 用 curl 检查相同接口

在 **PowerShell** 中，可把上面循环里的 `Invoke-RestMethod ...` 那一行替换成（本例 JSON 只含 ASCII，`@-` 避开 Windows 引号问题）：

```powershell
$json | curl.exe --noproxy '*' --fail --silent --show-error -H 'Content-Type: application/json' --data-binary '@-' "$base/gsi"
```

在另一个 PowerShell 中读取/写入操作员 API；终端 B 要继续发送。下面两条 POST 给出实际 schema；把 `curl.exe` 行换成 `Invoke-RestMethod -Method Post -Uri "$base/api/$name" -Headers $headers -ContentType 'application/json' -Body $json` 即为全 PowerShell 版本：

```powershell
$portNo = [int](Read-Host '本次端口'); $base = "http://127.0.0.1:$portNo"
$op = Read-Host '本次 Operator token'; $headers = @{Authorization="Bearer $op"}
$demoStart = [long](Read-Host '仅合成演练：终端 B 打印的 matchStartUnix')
function Get-Anchor {
  $s = Invoke-RestMethod -Uri "$base/api/state" -Headers $headers
  if (-not $s.binding) { throw '没有新鲜玩家帧' }
  @{contextId=$s.binding.contextId; matchId=$s.binding.matchId; team=$s.binding.team; hero=$s.binding.hero
    clock=$s.own.clock; capturedAt=[DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds()}
}
function Send-Bridge($name, $body) {
  $json = $body | ConvertTo-Json -Depth 6 -Compress
  $json | curl.exe --noproxy '*' --fail --silent --show-error -H "Authorization: Bearer $op" -H 'Content-Type: application/json' --data-binary '@-' "$base/api/$name"
  if ($LASTEXITCODE -ne 0) { throw "写入 $name 失败；重新检查本局和时效。" }
}
Send-Bridge 'confirm' ((Get-Anchor) + @{exercise='rehearsal'; ownPlayerView=$true; build=6986; matchStartUnix=$demoStart})
Send-Bridge 'visible' ((Get-Anchor) + @{visibleNow=$true; danger='not-seen'; targets=@(
  @{targetId='lane:bot'; state='present'; area='own'}
  @{targetId='good_1'; state='empty'; area='own'}
)})
curl.exe --noproxy '*' --fail --silent --show-error -H "Authorization: Bearer $op" "$base/api/state"
# 测完可锁住本次桥：Send-Bridge 'stop' @{stop=$true}
```

## 后续有已获授权的本人自定义局输入时

单独以 `--mode custom-lobby` 启动。给**专用于这场练习**的官方玩家 GSI 发送端的接收合同是：URI `http://127.0.0.1:<新端口>/gsi`，根级 `auth.token` 填新 **GSI token**，只需要 `provider/map/player/hero/items/abilities`；要保持新鲜，期望在时钟前进时至少每秒收到一帧。这里只提供接收端和协议，**不运行安装器、不生成/写入 cfg、不复用房间、双机或沙盒凭据，不要求改变正在运行的游戏**。没有这样的输入就停留在上面的合成演练。

请只从玩家本人画面报告目标；复盘也不是任意观战槽。新对局需操作员按 `matchId + hero + team + contextId` 单独确认。若 GSI 显式给出非练习 `map.lobby_type`（例如 `7`），自定义模式拒绝；字段缺失时无法从包独立证明房间类型，token 也不是 Valve 身份证明。真实匿局号/试玩 `matchid=0`、没有来源时戳等不满足条件的包**只给原因**，不给它们补编号或把到达时间冒充捕获时间。

本场 build / 实际开局 Unix 秒必须由操作员从**同局可靠记录**独立核对，不能从时钟倒推；不掌握就留空。复盘演练仅在局号、英雄、阵营与已发布归档的同一条目一致时可以取该局固定元数据，声明冲突即拒绝。版本按已核对 SHA-256 的 `steam.inf` 快照中匹配的 Client/Server build，且必须确实出现在该补丁的归档样本中；不把 `provider.version`、最新日期或一段连续 build 号当证明。人工核对不是对游戏进程的密码学认证。

## 接口与撤回合同

所有响应 `no-store`。除静态网页/JS/CSS 外只有下列接口，没有配置、历史全图、执行动作或加载路线的接口。仅绑定字面地址 `127.0.0.1`；校验实际对端、Host 和浏览器 Origin，无跨域许可。网页也核对独立接收器写入的本机地址；如果被别的静态站点提供，则禁止输入 token、不发 API 请求。拒绝 URL token；所有 POST 为未压缩 UTF-8 `application/json`。原始数据不落盘。

| 接口 | 授权及字段 | 作用 |
|---|---|---|
| `POST /gsi` | 根级 `auth.token`；≤64 KiB；上方是完整可发送例包 | 接收扁平当帧，返回 ACK，不向 GSI token 开放状态。`map.matchid` 为非零正十进制字符串或安全整数；`provider.timestamp` 为 Unix **秒**。 |
| `GET /api/state` | `Authorization: Bearer <Operator token>` | 取 `status`、当前 `binding`、`own`、`reported`、`timing`、`confirmation`、`visible`，及只读 `observation` / `shadow`。未确认不向研究模块供帧。 |
| `POST /api/confirm` | Operator；≤8 KiB；`contextId,matchId,team,hero,clock,capturedAt,exercise,ownPlayerView,build,matchStartUnix` | 锚点由本局 GSI 给出；`capturedAt` 为核对时的 Unix **毫秒**，`clock` 为游戏秒；未知元数据传 `null`。更改确认先撤回旧目击。 |
| `POST /api/visible` | Operator；同一锚点（六个字段）＋`visibleNow:true,danger,targets` | `danger` 为 `seen / not-seen / unknown`。每个目标只有 `{targetId,state,area}`，后两者分别为 `present / empty / unknown`、`own / enemy / unknown`。区域指目标所在**局部**，不是可通行路径。没有目击用 `[]`。来源、局号和观测时钟由桥绑定，客户端不能声明 `screen`、安全证书或全图权限。 |
| `POST /api/stop` | Operator；`{"stop":true}` | 立即清空、锁止；要求人工重启进程。 |

- **两种时间都要新鲜**：GSI 来源时戳、接收时刻、时钟最后前进时刻，任一满 5 秒即撤回。重复包不能延长来源有效期，冻结时钟不能靠心跳续命。手工目击有独立的 5 秒墙钟期限；游戏时钟走过目击时刻 **3 秒以上**也撤回。墙钟最多容忍超前 500 ms，游戏时钟不允许超前。启动后有后台清理，GET/POST 还会在使用前复核；网页约每 450 ms 轮询并设本地失效计时，断网/切页不留旧候选。
- **换局与终止**：换英雄、换阵营、倒带、大幅跳秒、过期后恢复都换 `contextId`；不自动继承确认、手工观测或历史候选。旧局尾包、倒序来源戳不能覆盖新帧。暂停、死亡、受控、缺失关键本人字段、非进行中对局、安全终止都撤回。暂停/死亡等撤回帧之后须有更新的来源秒，不能用同一秒迟到的旧包复活。
- **可见范围**：`minimap` 等未证明迷雾语义的块直接丢弃；`previously/added/removed` 不拼当前帧；多玩家嵌套、观战及回放世界输入拒绝。仅主栏 `slot0..5` 的正面目击进入 `itemsSeen`；未上报不是背包为空，未知主动冷却不当作就绪。没有明确暂停/受控位时仅显示本人状态。Steam ID、昵称、聊天、原始 GSI 和既有凭据均不进入输出。
- **不写策略**：桥只生成 [`Shadow742Observation`](../src/tactics/shadow-742.ts)，调用独立模块的 `observeShadow742`。默认与研究 CLI 一样优先已发布的 `expanded`，不存在才取 `fixed`；可显式 `--corpus fixed|expanded`，不接受用户文件路径。`archive` 显示实际选择和局数。历史独立账号命中只描述过去；本人账号未上送，除同局归档能识别的焦点账号外，不宣称跨账号隔离已经证明。没有可信版本/开局时间/本人阵营/亲见己方局部目标就不出历史候选；没有可通行区域、追击和伤害边界证明，**任何状态都不出路线或操作**。

参考仓库依据：[`tactical-assistant-design.md` 的权限边界](tactical-assistant-design.md)、[玩家与观战模式的实测差异](plus-observer-gsi-forensics.md)、[`provider.version` 不是补丁号的实测](plus-observer-e2e-20261116.md)、[己方编号不能证明路径安全](sandbox-own-area-audit.md)、[7.42 归档证据](farming-742.md)。复用 [`PlayerSnapshot`](../src/tactics/types.ts) 的已知字段约定，不启动旧 7.41f 战斗/规划链路。

## 验证范围（2026-11-22，香港时间）

```powershell
node --test tests/farm-shadow-bridge.test.ts tests/farm-shadow-bridge-http.test.ts
node scripts/farm-shadow-bridge.ts --demo
# 可选：已具备 Playwright / Chromium 时，沿用仓库的 PLAYWRIGHT_MODULE、PLAYWRIGHT_EXECUTABLE_PATH
node tests/farm-shadow-bridge.browser.mjs
```

本桥 **15 项自动化测试通过**，包括真实回环 POST/GET、鉴权/来源隔离、错局/倒序/过期/危险撤回。实际用 PowerShell 和 `curl.exe` 跑过上面的请求；独立无头浏览器覆盖普通和窄屏页面、目击提交、换局、断网、停止。一次固定归档的 40 次热连接样本，**合成 GSI POST 发起 → 鉴权 GET 读到同一帧 HP**，P50 **30.96 ms**、P95 **31.69 ms**（本机记录 `.tmp/farm742-bridge/demo.json`）。不含游戏采样/缓冲、操作员反应、网页轮询等待或屏幕绘制；`provider.timestamp` 只有秒级，不能拿来报告游戏链路的毫秒延迟。没有实战时延、迷雾正确率、路线收益或 GPM 验收结果。
