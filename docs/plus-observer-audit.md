# Dota Plus 好友观战：隔离式 GSI E2E 审计

2026-11-16。首轮实际结果见 [真实观战 E2E 报告](plus-observer-e2e-20261116.md)：
传输/部分字段通过，但没有取得所选英雄装备技能/经济状态，迷雾安全性未通过。
本工具不改动游戏客户端、不读游戏内存、不绕过迷雾、不调用云模型，
也不将任何观战数据转发给普通自动教练。

## 启动

在游戏机运行，输出目录必须尚不存在，父目录必须存在：

```powershell
node scripts/client/observer-audit.ts --out "C:\g\dota2-coach\.tmp\plus-observer-audit-session" --minutes 60
```

默认安装目录是用户指定的
`C:\Steam\steamapps\common\dota 2 beta\game\dota\cfg\gamestate_integration`；
可用 `--cfg-dir` 显式改变。采样器绑定一个空闲回环端口并生成带随机凭据、
唯一名字的专用 `.cfg`。不会读取 `steam.env`、Foundry 或普通客户端的配对密钥。

配置写入后，带 `-gamestateintegration` 启动 Dota。保留原有启动参数，
不加 `-insecure`、不使用本地机器人 E2E 的作弊/场景命令。
是否成功加载必须以实际收到的包为准。

随后用户自行选择好友的 Plus 实时观战。**普通延迟观战、录像、机器人局与 Plus
好友实时观战必须分别记录，不能互相替代验收。**记录好友所在阵营与英雄，
不必公开其账号/昵称。

## 证据与限制

- `session.json`：独立监听端口、进程、启动时间、所生成配置的位置及 SHA256，不含 token。
- `packets.sanitized.jsonl`：逐包接收时间/序号及脱敏字段；保存数值、布尔、null、
  游戏内部英雄/物品/技能名称，保留缺失与显式空值的区别。v3 新增 `ingress`：
  脱敏执行前的结构/白名单值/关键节点摘要与落盘结果比对，不含完整 HTTP 原文。
- `summary.json`：每两秒更新；各字段类型、出现/缺失次数、变化次数、数字范围；
  最新英雄/玩家结构；请求但未出现的组件。
- `STOP`：在输出目录创建这个空文件可停止。默认 60 分钟（参数范围 1–120）、
  512 MiB 包数据、14400 包，到限停止；不会删旧证据目录。

未知字符串和身份/凭据字段会隐藏，`matchid` 也隐藏；昵称、Steam ID、auth 和
任意原始 HTTP 内容不落盘。`previously` / `added` 保存为变化片段，但**不合并为当前状态**，
也不用于当下字段统计。没有能力从单包判断它是自身视角或观战视角。
图片如另行截取，仍可能含昵称和聊天，不能宣称匿名。

请求组件包括 provider/map/player/hero/abilities/items/buildings/league/draft，以及
wearables/events/minimap/roshan/couriers/neutralitems 等探针。
v3 在 `data` 中另请求 auth（记录仍移除 auth），与上游示例的 16 项组件对齐；
根级 `auth.token` 鉴权始终保留。
**配置中写了组件不代表 Dota 支持或会发送它**，以观察到的包为证。

已知事件类型与 JSON 字符串中的白名单游戏数值不再一概隐藏；转换过的数据带
`__auditSourceType` 标记。完整归因/版本差异见 [开源实现复核](plus-observer-gsi-forensics.md)。

退出时只删除本次创建、路径/类型与 SHA256 均未变化的配置；用户修改过的保留。
强杀进程无法保证清理，可根据 `session.json` 定位后人工检查。回环端口关闭后，
留存的配置也不会把数据发送给远程服务。

## 实测步骤

1. 主菜单基线：确认真实 GSI 包到达、客户端 provider/version、菜单结构。
2. 进入 Plus：截屏确认模式；记录接收时间及游戏时间，不将接收时间当作服务端实时性证明。
3. 分开天辉/夜魇与各 player 槽：数量、选中英雄、等级、位置、血蓝、状态、
   装备/冷却、技能/冷却、金币/净资产/GPM/补刀字段逐项检查。
4. 对照同一游戏时刻画面：队友是否持续更新，敌人进入/离开视野后字段是否消失、
   null、停留旧值、或继续变化。至少观察一次可确认的显隐转换；
   若没有场景证据则标“迷雾过滤未验证”，不通过猜测补齐。
5. 逐项检查野怪/堆数/刷新、兵线、塔、信使、Roshan 等是否真的有结构化数据。
   英雄坐标/补刀存在不能证明营地状态可得。
6. 结束并停止采样，保留结论与样本。结果需区分“取得字段”“新鲜度验证”
   “迷雾过滤验证”“能安全用于玩家教练”，四者不是一回事。

普通客户端 v0.2.1 的 Debug 只存 GSI 结构摘要，不能代替本次字段级审计。
本工具不是实时观战路线功能，不修改普通教练的输入权限。

## 不重启游戏地接续记录

仅针对仍运行、配置未改动的本工具会话：

```powershell
node scripts/client/observer-audit.ts --out "C:\g\dota2-coach\.tmp\plus-observer-next" --minutes 45 --continue-from "C:\g\dota2-coach\.tmp\plus-observer-audit-session\session.json"
```

会验证旧配置的目录/格式/SHA，正常停止旧记录器后复用游戏已加载的本机端口和凭据。
旧证据不覆盖，新 `session.json` 记录 `continuedFrom` 与脱敏器版本。
接续期间有短暂缺口，必须在报告中披露；若旧会话已到期、配置被改动或端口被占用则拒绝。
此选项只更换记录器，不会让已运行的 Dota 重新加载新增的组件请求。

## 无游戏测试

```powershell
node --test tests/observer-audit.test.ts
npm run typecheck
```

覆盖双阵营/flat 结构、缺失/null、禁止合并历史状态、深层脱敏、
回环 HTTP 鉴权/Origin/大小限制、CLI 落盘及配置清理/用户修改保护。
测试包使用独立临时目录，**不会发送给真实游戏采样器**。
