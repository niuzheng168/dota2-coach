# Azure VM 部署

## 当前部署（2026-11-14）

| 项目 | 配置 |
| --- | --- |
| 入口 | `https://dota2.largeniu.com` |
| Azure VM | `proxy-southeastasia-20261112121407-61d1`，新加坡 |
| 资源组 | `LARGENIUSS` |
| 静态公网 IP | `20.212.231.41` |
| 阿里云 DNS | `largeniu.com` 下 `dota2` 的 A 记录，TTL 600 秒 |
| 应用源码 | `/opt/dota2-coach` |
| 容器 | `dota2-coach`，Node.js 24，非 root 用户 |
| 内部监听 | `127.0.0.1:3210`，不直接暴露公网 |
| 反向代理 | 现有 Caddy，自动签发/续期证书及 HTTP → HTTPS 跳转 |
| 账号与房间 | `/var/lib/dota2-coach/access.json`，权限 `0600` |
| 主资源配置 | `/etc/dota2-coach/app.env` |
| GPT-Live 独立配置 | `/etc/dota2-coach/foundry-live.env` |
| 视觉识阵模型 | `gpt-6.1-terra`，使用主资源 |
| 当前发布标签 | `dota2-coach:listener-v0.1.2-20261114-r2` |

首次部署复制本地已有的账号密码哈希、权限、房间、邀请和 GSI token，**不迁移本地登录会话**。原账号和密码保持不变，需要在新域名重新登录。生产数据此后独立保存，更新代码时不要再用本地 `.local` 覆盖。

主资源沿用 `va-dev-sea-resource`，GPT-Live 沿用独立的 `avatar-use2` 资源。密钥只保存在 VM 的受限配置文件，通过只读挂载提供给应用，不在镜像、Git 或前端中。没有复制个人 Azure CLI 登录缓存。

### 2026-11-14 字幕位置 v0.1.3（待停止音频后切换）

- 修复字幕位置只到 60%：新版网页与助手协商完整 0–100% 范围，原生窗口按实际面板高度和 DPI 定位；100% 对齐底边。旧助手显示升级提示而不误报支持。
- 本地和候选镜像各 48 项测试通过，三套浏览器回归与 Windows 构建通过；C# 核心共 390 条断言，包含字幕几何/DPI 回归。
- 安装包交付到 `/nas/share/DotaCoach-v0.1.3-win-x64.zip`（同名 `.sha256`），必须与新版网页配套使用。
- 本次候选发布在切换前检测到仍有持续音频输入，**未重启生产容器，已恢复旧源码和镜像标签**。当前线上仍是上表 v0.1.2，不要把新版包已生成误认为网页已上线。
- 等用户停止 App 连麦及外部监听后，重新核对生产版本/漂移再切换。准备脚本 `.tmp/listener-v013/install.sh`，候选清单 `.tmp/listener-v013/release.json`，公网验收脚本 `.tmp/listener-v013/verify-public.mjs`；备份/测试日志在 VM `/var/backups/dota2-coach/listener-v0.1.3-20261114`。这些本地 `.tmp` 文件不提交，缺失时需重新生成。

### 2026-11-14 桌面监听 v0.1.2 配套上线

- 修复桌面助手连接线上后仍提示“未检测到桌面监听支持”：部署此前遗漏的 `desktop-listener.js`、网页面板与 `ProcessListenEngine`，保留既有 App 连麦、房间权限与英雄面板。
- 监听使用独立鉴权 WebSocket；服务端先确认私有/纯文字协议后才允许采音。仅向发起连接返回原文和中文翻译，不发送 TTS、不广播房间。旧 v0.1.1 助手重开页面即可兼容。
- 新增独立 `DEPLOYMENT_LISTENER_TEXT`，默认使用实际可用的 `gpt-6-luna`，不沿用旧 Cascade 不存在的带 SKU 后缀部署名。不修改房间现有引擎设置或两份私有配置文件。
- 首次候选发布在真实文字接口验证中发现 `DeploymentNotFound`，切换生产容器前已自动恢复源码和旧镜像。修复后发布 `listener-v0.1.2-20261114-r2`；最终切换前确认最近 60 秒没有麦克风输入。
- 本地与生产候选镜像各 47 项自动化测试通过；桌面监听、房间设置与英雄面板三套本地浏览器回归通过。公网 HTML/JS/CSS 哈希与发布清单一致，HTTPS 200、HTTP 308、匿名监听 WSS 401。
- 使用生产资源配置的隔离容器真实验证三种 ASR 转录会话、英语/中文文字筛选，并用合成音频验证英文转录与中文译文配对、中文音频被过滤。没有挂载生产账号目录；不等于 Windows/Dota 2/YY 实机采音验收。
- 公网实际页面的桌面消息桥、进程选择、私有聊天与双语字幕事件均通过浏览器验收；账号/API/原生采集由模拟数据提供，不触碰业务房间。127 英雄面板仍可打开。
- 账号、房间及两份私有资源配置已核验保持不变。回退镜像为 `dota2-coach:before-listener-v0.1.2-20261114-r2`；备份、镜像构建、自动化测试和云端验收日志位于 `/var/backups/dota2-coach/listener-v0.1.2-20261114-r2`。
- Windows 包按版本号交付到 `/nas/share/DotaCoach-v0.1.2-win-x64.zip` 并附 `.sha256`，不覆盖旧包。用户关闭并重开助手后进入房间，展开「桌面双语字幕 · 外部程序监听」。

### 2026-11-14 英雄头像面板更新

- 上线按力量、敏捷、智力、全才分组的 127 英雄头像面板，支持点击添加/取消、外号搜索、最多 10 人及已选阵容显示。房主操作与 GSI/视觉结果使用原有房间同步通道；模型和语音引擎配置未改。
- 127 张头像和 4 个属性图标随镜像本地提供。部署前逐文件核对哈希；公网 131 张图片及 HTML、JS、CSS 均与发布清单一致，图片 MIME 和缓存头正常。
- 本地类型检查、37 项自动化测试、英雄面板与房间设置两套浏览器回归通过；生产镜像内 37 项测试通过，容器健康。公网 HTTPS 200、HTTP 308，匿名视觉 API/WSS 401。
- 使用公网实际 HTML/JS/CSS/图片验证桌面和手机布局、点选取消、外号搜索。该浏览器验收模拟账号、API 和语音连接，不触碰业务账号或房间；真实房间/GSI/模拟视觉同步由本地隔离回归验证。
- 线上账号、房间及两份私有资源配置已核验未变。旧镜像为 `dota2-coach:before-hero-picker-20261114-112616`，备份目录为 `/var/backups/dota2-coach/hero-picker-20261114-112616`；当前文件清单在 `/opt/dota2-coach/DEPLOYED_RELEASE.json`。
- 已打开页面需刷新，点击「选择英雄」使用新面板；语音连接断开后按需重新开麦。

### 2026-11-14 视觉识阵更新

- 修复不存在的默认视觉部署名，改用 `gpt-6.1-terra`；同时修复失败帧不重试、请求超时和空识别结果处理。语音引擎配置未变。
- 本地类型检查、35 项自动化测试和浏览器回归通过；新生产镜像内 35 项测试通过。公网 HTTPS 返回 200，前端脚本哈希与本地一致，HTTP 返回 308，匿名视觉 API 和 WSS 均返回 401。
- 生产容器内通过隔离的临时账号/房间调用 `/api/vision-draft`，真实主资源识别 JPEG 合成顶栏图为 **10/10，2661ms**。未操作业务房间；单张合成图不代表所有真实游戏画面的识别准确率。
- 账号、房间与主资源/GPT-Live 凭据已核验保持不变。旧镜像标签为 `dota2-coach:before-vision-terra-20261114-075541`，源码、数据及受限配置备份位于 `/var/backups/dota2-coach/vision-terra-20261114-075541`。
- 本次发布包含工作区修复；精确文件哈希及基础提交记录在 `/opt/dota2-coach/DEPLOYED_RELEASE.json`，不要仅凭旧的 `DEPLOYED_COMMIT` 判断当前源码。
- 已打开的页面需刷新，再开启窗口自动识阵；语音重连后按需重新开麦。

## 部署结构与安全边界

- `Dockerfile` 固定 Node 镜像摘要，使用锁文件安装生产依赖。
- `.dockerignore` 采用白名单，不发送 `.env`、`.local`、`.tmp`、Git 历史等到构建上下文。
- `deploy/compose.yaml` 设置只读根文件系统、非 root 用户、移除 capabilities、内存/PID 限制、日志轮换和健康检查。
- VM 原本关闭 IP forwarding，其他容器也使用 host 网络。因此本应用保持该网络策略，通过 `HOST=127.0.0.1` 仅监听回环地址，不更改 VM 防火墙或其他服务。
- `COOKIE_SECURE=1`、`PUBLIC_ORIGIN=https://dota2.largeniu.com` 已设置，GSI 下载配置使用 HTTPS 地址。
- `deploy/Caddyfile` **仅追加**到 `/etc/caddy/Caddyfile`，不要覆盖其他网站。原配置备份位于 `/etc/caddy/Caddyfile.before-dota2-20261113`。
- Docker 随系统启动，应用使用 `restart: unless-stopped`。健康检查本身不会重启一个仍在运行但不健康的进程，需要结合日志排查。

私有配置目录由 root 持有，权限 `0700`；挂载文件为 `root:1000`、`0640`，容器中的 UID/GID 1000 可读取。数据目录为 `1000:1000`、`0700`。

## 运维

以下命令在 VM 上运行：

```bash
cd /opt/dota2-coach
sudo docker compose -f deploy/compose.yaml ps
sudo docker compose -f deploy/compose.yaml logs --tail=100 app
sudo docker compose -f deploy/compose.yaml restart app
curl -fsS http://127.0.0.1:3210/api/auth/session
```

无需 SSH 时，也可从已登录 Azure CLI 的机器查看状态：

```bash
az vm run-command invoke \
  -g LARGENIUSS -n proxy-southeastasia-20261112121407-61d1 \
  --command-id RunShellScript \
  --scripts 'cd /opt/dota2-coach && docker compose -f deploy/compose.yaml ps'
```

更新前在开发机执行 `npm run typecheck && npm test`，仅同步源码和部署文件到 `/opt/dota2-coach`，**排除所有本地密钥和数据**。然后在 VM：

```bash
cd /opt/dota2-coach
sudo docker tag dota2-coach:local dota2-coach:rollback
sudo docker compose -f deploy/compose.yaml up -d --build --wait --wait-timeout 90
```

需要回退镜像时：

```bash
sudo docker tag dota2-coach:rollback dota2-coach:local
sudo docker compose -f deploy/compose.yaml up -d --no-build --wait
```

代码更新或容器重启会中断正在进行的语音会话，应选择无活动房间时操作。

### 房间设置更新

房主可在房间内修改引擎、音色和 ASR。保存更改会先持久化，再通知成员并以 `4000` 关闭全房间的旧连接；浏览器自动建立新连接，并等待新语音引擎就绪。原邀请链接和允许名单保持不变，麦克风需要重新开启。

升级此功能后，已打开的旧页面需要刷新一次以加载自动重连逻辑。不需要迁移数据，也不要覆盖生产 `access.json`。可用 `tests/room-settings.browser.mjs` 验证双浏览器流程，自动测试使用临时数据和模拟上游。

### 管理账号

存储只支持一个写入进程。先停止应用，再通过受保护的 JSON 文件添加账号：

```bash
cd /opt/dota2-coach
sudo docker compose -f deploy/compose.yaml stop app
sudo docker compose -f deploy/compose.yaml run --rm --no-deps -T \
  app node scripts/create-user.ts < /secure/path/account.json
sudo docker compose -f deploy/compose.yaml up -d --wait
```

JSON 字段见 README。不要把密码放在命令参数、仓库或日志中；完成后删除临时账号文件。

### 数据备份

```bash
sudo install -d -m 700 /var/backups/dota2-coach
sudo cp /var/lib/dota2-coach/access.json \
  "/var/backups/dota2-coach/access-$(date -u +%Y%m%dT%H%M%SZ).json"
```

备份包含密码哈希及邀请等敏感信息，必须限制访问。恢复前停止应用，恢复后将数据文件设为 `1000:1000`、`0600`，再启动。主配置和 GPT-Live 配置也应另行安全备份；同 VM 备份不能防止整机丢失。当前未配置异地自动备份。

## 验证边界

2026-11-13 已通过本地类型检查、本地及生产镜像内各 19 项自动化测试；公网 HTTPS 返回 200、HTTP 返回 308。Cascaded、Realtime 和 GPT-Live 均收到上游就绪确认；WSS 短文本翻译实际返回字幕和 PCM 音频，临时验收会话已注销。初始数据备份为 `/var/backups/dota2-coach/access-initial-20261113.json`，初始镜像标签为 `dota2-coach:20261113`。

上线检查包括 HTTPS 证书、HTTP 跳转、静态文件、鉴权/跨站拦截、WSS 转发、账号与房间持久化，以及三种语音引擎的上游就绪确认。短文本翻译验证可检查字幕和音频字节经过 WSS 返回；这不替代真实浏览器麦克风、扬声器及双人连麦体验测试。
