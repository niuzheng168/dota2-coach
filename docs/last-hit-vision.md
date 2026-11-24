# 补刀视觉模型：从画面认出兵的出手

[补刀实验室](last-hit-lab.md)测出：只看血条，红字要提前约 1 秒判断兵的血量，补到率约 63–66%；如果能从画面上认出正在打这只兵的出手（近战兵抬手到命中 0.47 秒，远程兵抬手 0.5 秒加弹道约 0.3 秒，攻城车约 1.4 秒，塔约 1.2 秒），露娜能到 76%、主宰 84%；认出得晚 100 毫秒只少约 2 个百分点。这里是训练这个模型的数据和脚本。

## 数据：实验室录画面

```powershell
dotnet build desktop/DotaCoach.GameClient -c Release
node scripts/lasthit/lab.ts Z:/dota2coach/data/lasthit-vision/rec1.jsonl --frames Z:/dota2coach/data/lasthit-vision/rec1 --hero npc_dota_hero_luna --seed 11 --rounds 40
dotnet run --project desktop/DotaCoach.TwoPc.Tests -c Release -- --vision-label Z:/dota2coach/data/lasthit-vision/rec1.jsonl Z:/dota2coach/data/lasthit-vision/rec1
```

- `--frames DIR`：记录模式，按实时速度跑（动画和画面一致）。每回合随机一天中的时间（白天、夜晚光照不同）。游戏端（实验室参数 `--frames`）把 Dota 窗口的客户区每秒存 30 张 JPEG（质量 92），Dota 在别的窗口后面也照存。只在实验室里用，正式版不会存画面。
- 竞技场额外记录每次攻击的**抬手开始**（`swing`）和**出手**（`launch`：近战这时命中，远程这时放出弹道），来自挂在每只兵、塔和英雄身上的 `modifier_coach_watch`。
- 1080p 下约每小时 10 万张图、57 GB。

每次录制的文件：

| 文件 | 内容 |
|---|---|
| `recN.jsonl` | 引擎真值：每 1/30 秒所有兵的血量、位置、攻击目标（`f`），英雄（`h`），每次伤害（`dmg`，`inflictor` 为 -1 的是普攻），`swing`、`launch`、`kill`、回合（`round`，含 `daytime`） |
| `recN.sync.jsonl` | 每个服务器帧的第一行到达本机的时间（和画面同一个 QPC 时钟） |
| `recN/NNNNNN.jpg` | 画面 |
| `recN/frames.jsonl` | 每张图：文件名、采集时间 `at`（QPC 秒）、游戏端找到的血条 `[x, y, 行数, 填充像素, 友方, 被遮挡]` |
| `recN/labels.jsonl` | `--vision-label` 生成：每张图的服务器时间 `t`、镜头注视点、每根血条属于哪个单位（`bars`，-1 为没配上）、所有单位血条中心在屏幕上的位置（`units`：`[id, x, y]`） |

**对齐**：服务器时间 = 画面采集时间 − 偏移 − 画面滞后。偏移取同步文件里“到达时间 − 服务器时间”的最小值；画面滞后按“配上单位的血条显示的填充像素和引擎血量算出的一致”拟合（试录：约 0，一致率 77–81%，精度约一个服务器帧 33 毫秒）。血条配单位用产品同一套方法（引擎位置代替 GSI），试录配上 93–96%。

试录（smoke4，3 回合）核对过：近战兵抬手到命中 0.467 秒、远程兵 0.5 + 0.3 秒、攻城车 0.7 + 0.73 秒、塔 0.53 + 0.63 秒；每次伤害都找得到之前的出手；目标死了的抬手会取消（约 6%）。

## 训练样本：`scripts/lasthit/vision/prepare.py`

```bash
python scripts/lasthit/vision/prepare.py REC.jsonl REC_DIR OUT_DIR [--near 256] [--far 768] [--size 128] [--steps 0,1,2,4]
```

- 每张图里每只兵裁两块，以兵身（血条中心下方 40 像素）为中心：**近**（256 像素见方，看贴身近战兵的抬手）和**远**（768 像素见方，看远程兵、塔的出手和弹道），都缩到 128×128，JPEG 存进 `crops.bin`。尺寸按 1080p，其他分辨率按高度缩放。
- 样本：游戏端找到并配上单位的每根血条。输入是这只兵在这张图和之前第 1、2、4 张图（0、33、67、133 毫秒前）的两块裁图；目标是接下来 1 秒内每 0.1 秒落到它身上的兵和塔的伤害（不含英雄：自己的出手玩家知道，实验室里没有敌方英雄）。附带它自己正在抬手的进度和目标方向，以及血量、兵种、回合、离死还有多久，供分析和辅助训练。
- 试录 2146 张图生成 12568 个样本、13142 对裁图，159 MB（原图 1.2 GB），8 进程 21 秒。

## 训练：`scripts/lasthit/vision/train.py`

```bash
python scripts/lasthit/vision/train.py OUT DATA_DIR... [--test DATA_DIR...] [--epochs 12] [--batch 512] [--workers 16]
```

- 模型：近、远两块各一个小卷积网络（4 帧叠成 12 个通道，128 → 4×4），拼起来过两层全连接，输出 10 个 0.1 秒格子的伤害（softplus），另有辅助输出：自己是否在抬手、抬手进度、目标方向。约 450 万参数，ONNX 18 MB；CPU 上 16 只兵约 22 毫秒，显卡上会快很多，游戏机上要实测。
- 回合号是 5 的倍数的回合做验证；`--test` 留出整段录制（例如换英雄、换种子的那段）测泛化。每轮输出到 0.3/0.5/0.8/1 秒累计伤害的平均绝对误差，和“总猜平均值”对照。
- 输出 `model.pt`、`model.onnx`（输入 `near`/`far`：uint8 `[n, 12, 128, 128]`，输出 `damage`：`[n, 10]`）、`log.jsonl`、`predictions.npz`。
- 环境：Python 3.12、PyTorch（CUDA）、numpy、pillow、onnx、onnxscript、onnxruntime。`--limit 600` 可在任何机器上几分钟内跑通一遍。

## 还没做

- 用模型预测代替血条节奏，接回 `--last-hit-eval` 算补到率（离线），再接进游戏端（ONNX Runtime + 显卡）实测延迟和补到率。
- 数据只有中路竞技场、默认兵模型、没有敌方英雄和技能特效；真实对局的画面更乱。
