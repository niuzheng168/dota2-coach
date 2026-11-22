# Codey 0.3.43：真实路由、HTTP 模拟上游

**2026-11-22 复验：完整运行退出 0，8/8 个 C# 场景通过。**
[修复后的逐请求证据](verified-0.3.43.json)包含路由及 source map 的 SHA-256、实际编译的 Core DLL 哈希、两侧请求和图像边界。[修复前的红测快照](observed-0.3.43.json)仅作对照，不是当前结果。

```powershell
node C:/g/dota2-coach/scripts/test-codey-gateway.mjs
```

使用本机 Node 24、.NET 10 SDK 和已安装的指定 Windows 发布包。`--routes-only` 不构建 C#；`--evidence <新文件>` 保存不含请求正文和密钥的 JSON，不覆盖已有文件。可用 `--gateway <目录>` 指向同一构建的 `app/gateway`；哈希不匹配即失败，不回退到假网关。

### 哪些是真的

`CloudCoach`（项目引用，不注入 `HttpMessageHandler`）→ 环回 TCP → 安装包的 `srvx`、`createServer()`、鉴权、Hono 路由、模型发现及 Copilot 转发 → 另一环回端口的 **HTTP mock**。目录、模型答案、用量以及 320×180 灰色 JPEG 均为测试数据，不能据此判断真实账号的可用模型或推理质量。

* 安装包 `server-BotZPvTv.js.map` 内的 `src/server.ts:118,120` 挂载两个 `/v1` 路由；`src/lib/model-display-name.ts:24` 加 provider 标签；`src/services/copilot/create-responses.ts:139` 转发到上游 `/responses`。mock 不给模型预加标签。
* 在独立进程中将 `state.copilotApiUrl` 和 Copilot token 指向测试上游和假 token。仅在内存中为 runtime chunk 附加一个导出，用它调用原有 `cacheModels()` / `getModels()`，预热后立即停止刷新。**没有替换路由、重写它发出的 URL、修改安装文件或运行登录/启动 CLI。**
* 网关和 mock 只监听 `127.0.0.1:0`；网关的 TCP 连接只允许本次 mock 端口，fetch 只允许指定方法和路径且不跟随重定向。测试进程使用环境白名单、临时 home/config/key、内存 SQLite。Node 权限限制文件读写并禁止子进程；C# 在单独进程中离线构建。退出后关闭监听并删除临时目录。无需探测正在运行的 Codey 或读取任何真实凭据。

### 两次红测如何转绿

| 场景 | 直接访问真实网关（对照） | 修复后的生产 `CloudCoach` |
|---|---|---|
| 仅宣告 `supported_endpoints: ["/v1/responses"]` | 直接 POST 返回 400，上游无请求 | 不列选项，只有目录 GET，无 POST |
| JPEG 原始 1,585 B，上限 1,585 或 1,603 B | 均返回 200，但 mock 实收 96×32、`low` 的替代 PNG | 两种上限均在 POST 前拒绝 |
| 同一 JPEG，上限恰为 1,604 B | mock 实收原 JPEG、`high` | 图像 POST 200；原图逐字节一致，模型 ID、建议和用量解析成功 |

`src/routes/responses/utils.ts:96` 识别的原生 HTTP **能力值**是 `/responses`，不同于客户端的请求路径。该文件第 190、303、308 行按 `floor(完整 data URL 的长度 × 3 / 4)` 与上限比较；此样本的 data URL 长 2,139 个字符，对应 **1,604 B**。

正常目录下分别用 `gpt-6.2-astra` 和 `gpt-6.2-sol` 运行 `ListCopilotModelsAsync`、文字 `ChooseMinuteFarmWithMetrics`、图像 `Decide`。还覆盖：未带 bearer 的 401；来自另一 provider 的同名裸 ID；无 vision 的 Sol **能答文字但不发图像**；HTTP 200 却回错模型 ID 时文字和图像都拒绝、无重试。报告中的 400 和 `REDACTED_PNG` 在最终运行中均来自直接对照探针，而非修复后的 C# 请求。
