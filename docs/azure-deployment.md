# Azure VM 部署

## 当前部署（2026-11-13）

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

首次部署复制本地已有的账号密码哈希、权限、房间、邀请和 GSI token，**不迁移本地登录会话**。原账号和密码保持不变，需要在新域名重新登录。生产数据此后独立保存，更新代码时不要再用本地 `.local` 覆盖。

主资源沿用 `va-dev-sea-resource`，GPT-Live 沿用独立的 `avatar-use2` 资源。密钥只保存在 VM 的受限配置文件，通过只读挂载提供给应用，不在镜像、Git 或前端中。没有复制个人 Azure CLI 登录缓存。

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
