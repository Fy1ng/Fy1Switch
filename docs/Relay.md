# 自建 ASP.NET Core Relay

## 边界与信任

- .NET 10, 单进程, 单实例, 内存设备路由, 无离线命令队列. 可选管理模式使用 Razor Pages/原生 JavaScript, ASP.NET Core Identity 和 SQLite 保存账户, Passkey 与设备注册. 不使用 Redis. 禁止多副本/滚动重叠运行, 不能轮询多个 Relay 实例.
- Windows 主动建立出站 WebSocket，手机使用 HTTPS。Relay 只鉴别中继凭据、限制协议并转发，不能代替 Windows 验证每手机 deviceToken、配对码、显示器状态或业务授权。
- 生产入口必须为 HTTPS/WSS，TLS 在受控反向代理终止。默认进程只监听 `http://127.0.0.1:5080`；`ASPNETCORE_URLS` 可覆盖。不得将带 Bearer/deviceToken 的明文 HTTP/WS 暴露公网或不可信局域网。
- **这是可信应用层中继，不是端到端加密隧道**：Relay 与 TLS 终止代理可见命令、配对码、回复和每手机 token。运行者必须可信。配置里的中继凭据以明文文件保存，仅运行时认证索引使用 SHA-256 哈希及常量时间比较。
- Tailscale DERP 是转发 WireGuard 加密报文的网络中继，不解析本应用协议，也不会自动提供本项目的设备路由、配对或授权接口。两者不是可直接互换的实现。

## 文件与构建隔离

- 服务：`src/relay/Fy1Switch.Relay/`；测试：`tests/Fy1Switch.Relay.Tests/`。
- 引用 `src/shared/Fy1Switch.Contracts` 项目中的共享消息契约, 不复制协议类型, 不构建 Windows/Android 项目. 设备注册与手机授权使用共享 DTO, 保留原有控制/配对消息的可选字段兼容.
- Relay 和测试 csproj 均显式清空父 `Directory.Build.props` 的 `RuntimeIdentifiers`。Docker 只复制所需源码和 Relay csproj，**不复制父 Directory.Build.props、global.json 或本机 bin/obj**；镜像独立使用 .NET 10 SDK 发布 self-contained Linux 程序。

## 配置和凭据

### WebUI 管理模式

推荐新部署使用 `src/relay/relay.management.config.example.json`. `management.publicUrl` 必须是规范的 HTTPS 根地址和固定 DNS 主机名, 不含末尾 `/`, 路径, 查询或片段. 它同时限定 WebUI Host/端口, Passkey RP ID 和精确 Origin. 不根据任意 Host 或 X-Forwarded-* 自动推断, 更换域名后需要重新注册该域名的 Passkey.

`management.dataDirectory` 为绝对私有可写目录, 保存 `identity.db`, `registry.db` 及 `keys/`. 数据库只保存 Windows 凭据哈希; 手机 Relay 凭据同时保存认证哈希和通过 ASP.NET Core Data Protection 加密的可恢复副本, 用于同一手机重复授权. 必须将两个数据库和 `keys/` 作为一套备份, 停止单实例后备份或使用 SQLite 一致性备份方式, 禁止上传 Git 或诊断包.

管理员固定为 `admin`, 不开放公网注册. 第一次启动从 `management.adminPasswordFile` 指定的绝对私有文件读取密码, 至少 12 个字符并包含大小写字母, 数字和符号. 不通过命令行或日志输出密码. 初始化后修改该文件不会重置账户, 可移除该配置项和秘密挂载. 登录后可以修改密码及添加/移除最多 8 个 Passkey. 修改密码需要当前密码; 删除最后一种登录方式被拒绝. 丢失所有登录方式时需通过受控服务器管理流程恢复, 没有公开重置接口.

管理会话为固定 30 分钟, Cookie 为 Secure, HttpOnly, SameSite=Strict. 所有浏览器变更请求需要 CSRF token; Passkey 挑战还校验一次性服务端会话和有效期. 密码错误会触发账户锁定, 登录和配对入口另有限流. WebUI Cookie 不能用于 Windows 或手机设备 API.

| 场景 | 操作 |
| --- | --- |
| Windows 接入 Relay | 浏览器登录 WebUI 后生成六位接入码. Windows 设置中填写同一 Relay 地址和接入码, 注册后自动保存 DPAPI 加密的 Windows 凭据并建立 WSS. |
| Android 首次配对 | Windows 设置中生成独立的手机 Relay 六位码. Android 添加电脑时输入 Relay 地址和该码, 通过 Relay 请求 Windows 签发手机身份, 证书 pin 和控制授权. |
| 已配对手机, 局域网内 | 已固定证书且携带手机授权的 Direct 请求获得 Relay 地址提示, 再通过 Windows 专用 HTTPS 接口自动取得该手机 Relay 凭据并加密保存. |
| 已配对手机, 非局域网 | 在该电脑连接设置中输入 Relay 地址并保存/重新连接, 自动用已有 deviceToken/ClientId 请求 Windows 验证, 无需六位码或 WebUI 登录. |

两类六位码分开保存和验证, 有效期最多 5 分钟, 保留前导零且只能使用一次. 手机码由 Windows 的 CSPRNG 生成, 在 Windows 独立验证后才签发手机授权, 不替换原有 LAN SRP 或 192-bit HTTPS 配对码. 离开显示配对码的界面, 锁屏, 关闭或切换配置会取消 Windows 手机 Relay 码. 配对请求超时或回复丢失时不自动重试, 应检查是否已保存设备或显式生成新码.

Relay 最多保存 256 个设备身份(包括撤销记录), 每设备最多 32 个手机凭据. Windows 在生成手机码及局域网授权前同步当前有效手机列表, 清理已经撤销的 Relay 凭据. 服务端同时最多 8 个手机短码会话, 六位码兑换入口全实例每 5 分钟最多 10 次尝试, 每来源另限制每分钟 10 次 bootstrap 请求; 刷新码不重置预算. 反代后的用户共享来源额度. 已有手机自动授权不消耗短码预算, 但 Windows 必须在线且仍保留该手机授权.

管理页可查看设备连接情况和撤销注册. 撤销立即禁用该设备的全部 Relay 凭据并断开 WSS. Windows 本地撤销手机仍立即阻止所有路径的控制, 不依赖服务端同步成功. 自动配置失败不阻断正常 LAN 状态读取; Android 对同一地址失败退避 60 秒. 用户清空 Relay 时关闭自动配置, 可通过设置开关重新启用.

没有 `management` 时保留旧静态配置模式, 不开放 WebUI/动态注册. 管理模式可以暂时保留 `devices` 数组, 旧凭据继续工作; 只有管理员点击管理页的 "导入旧配置" 或显式执行 `POST /api/admin/import-legacy` 才将其哈希导入数据库. 导入不覆盖已有或已撤销记录. 导入后同一设备由数据库鉴权, 撤销不会被重启或旧配置重新启用. 旧 Windows-Android 绑定保持有效, 也可以先在管理页撤销旧注册, 再用新接入码注册同一设备.

### 旧静态配置模式

`FY1SWITCH_RELAY_CONFIG` 必须是可读取 JSON 文件的**绝对路径**，缺失、相对路径、错误 JSON、空/重复设备、重复凭据或弱长度均拒绝启动。启动加载一次，更新/轮换需协调停止旧实例后重启；不会热重载。

示例位于 `src/relay/relay.config.example.json`，只含占位符，故意不能直接启动：

```json
{
  "devices": [{
    "deviceId": "11111111-1111-4111-8111-111111111111",
    "windowsToken": "REPLACE_WITH_UNIQUE_BASE64_OF_32_RANDOM_BYTES",
    "controllerToken": "REPLACE_WITH_DIFFERENT_BASE64_OF_32_RANDOM_BYTES"
  }],
  "requestTimeoutSeconds": 30,
  "requestsPerMinute": 60
}
```

- deviceId 必须与 Windows 的实际设备 UUID 相同，不由 Relay 新建。
- 每个角色/设备使用独立 CSPRNG 随机凭据，最少 **32 随机字节（256 bit）**，编码成规范标准 Base64；支持 32–128 字节。加载器校验编码与长度，但无法从字符串证明随机来源，禁止用口令、重复字节或示例占位冒充随机数。
- Windows 使用 windowsToken；手机使用 controllerToken。`X-Device-Token` 是 Windows 配对时生成的每手机 deviceToken，与这两个中继凭据不同；Relay 只原样转发它。
- 超时默认 30 秒，可配 0.05–100 秒；requestsPerMinute 默认 60，范围 1–120。两项均可省略。
- 以文件 ACL/权限限制配置，只让管理员及服务账户读取；禁入 Git、备份日志、请求日志、命令行参数与镜像。不要启用 HTTP body/header 调试日志或把 token 放 URL。

可选的本地生成方式（不会打印凭据、不覆盖现有文件）：

```powershell
./tools/Test-Relay.ps1 -CreateLocalConfig -DeviceId '<Windows实际设备UUID>'
$env:FY1SWITCH_RELAY_CONFIG = (Resolve-Path './src/relay/relay.config.local.json').Path
dotnet run --project ./src/relay/Fy1Switch.Relay/Fy1Switch.Relay.csproj --configuration Release --no-launch-profile
```

生成文件仅为 `src/relay/relay.config.local.json`，已局部 gitignore；生成后仍需保护权限并通过可信渠道分发。仓库示例始终不含真实凭据。

## HTTP / WebSocket 协议

### 管理模式注册与授权接口

| 接口 | 认证 | 结果 |
| --- | --- | --- |
| `POST /api/admin/registration-code` | 管理 Cookie + CSRF | `{code,expiresAt}`, 新码取消旧接入码 |
| `GET /api/admin/devices` | 管理 Cookie | 设备列表与连接状态 |
| `DELETE /api/admin/devices/{deviceId}` | 管理 Cookie + CSRF | 撤销注册并断开 WSS |
| `POST /api/admin/import-legacy` | 管理 Cookie + CSRF | 显式导入旧配置凭据哈希, `{imported}` |
| `POST /v1/relay/register` | WebUI 六位码, body `{deviceId,code,deviceName}` | `{deviceId,windowsToken}` |
| `POST /v1/devices/{deviceId}/pairing-code` | Windows Bearer | 登记 Windows 生成的 `{code,expiresAt}` |
| `DELETE /v1/devices/{deviceId}/pairing-code` | Windows Bearer | 取消手机短码 |
| `POST /v1/devices/{deviceId}/controllers/synchronize` | Windows Bearer | 用 `{clientIds}` 清理已撤销手机的 Relay 凭据 |
| `POST /v1/devices/{deviceId}/controllers` | Windows Bearer | 用 `{clientId}` 获取该手机专属 Relay 凭据 |
| `POST /v1/relay/pair` | Windows 手机六位码, body `{code,clientName}` | Windows 签发的身份和手机 token, 加上专属 controllerToken |
| `POST /v1/devices/{deviceId}/authorize` | `X-Device-Token` + body `{clientId}` | Windows 验证后返回专属 controllerToken, 不签发新手机身份 |

最后两个接口通过在线 WSS 转发 `relay-pair` 和 `authorize` 信封, Windows 用 `RelayResponse.bootstrap` 回复. Relay 严格检查设备, ClientId 和回复种类, 不在 Windows 验证前发放手机凭据. `/authorize` 成功回复中的 `deviceToken`, `endpoint`, `certificatePin`, `tailscaleEndpoint` 均为 null, Android 保留已有身份. 首次配对才包含完整身份. Windows 局域网 `POST /v1/relay` 使用原手机 Bearer 和 `{clientId}`, 返回 `{deviceId,clientId,endpoint,controllerToken}`.

所有 JSON 属性为 camelCase，枚举按共享跨端契约使用 PascalCase 字符串（例如 `"Off"`、`"On"`、`"Confirmed"`），不接受整数枚举。直接使用共享 RemoteMessages 类型，未知属性和缺少必需构造参数拒绝解析；RemoteCommand/RemoteReply 的 Version 省略时沿用契约默认 1。

### Windows 连接

`GET /v1/devices/{deviceId:guid}/connect`，生产使用 `wss://relay.example.com/...`，WebSocket 握手发送 `Authorization: Bearer <windowsToken>`。只允许配置内设备；同设备第二连接返回 **409**，不会驱逐第一连接。设备间路由、响应相关性及凭据均隔离。已认证且正在完成握手的连接可接受有界 pending，但发送必须等握手成功；失败立即取消，不跨连接迁移。

### 手机控制

`POST /v1/devices/{deviceId:guid}/control`：

- `Authorization: Bearer <controllerToken>`
- `X-Device-Token: <Windows生成的每手机deviceToken>`
- `Content-Type: application/json`，body 为共享 `RemoteCommand`。

示意 body（UUID 和时间必须由调用方实际生成，不能直接重放示例）：

```json
{
  "requestId": "22222222-2222-4222-8222-222222222222",
  "deviceId": "11111111-1111-4111-8111-111111111111",
  "command": "set",
  "expiresAt": "<未来不超过120秒的ISO8601时间>",
  "targetId": "33333333-3333-4333-8333-333333333333",
  "intent": "Off",
  "operationId": null,
  "version": 1
}
```

仅允许 `status` / `refresh` / `set` / `operation`，大小写严格匹配；不转发 shutdown、绑定、启动项等 host 管理命令。deviceId 必须等于路由，requestId 不得为空，version 必须为 1，expiresAt 必须在未来且最多 120 秒。Relay 不自行发明其余 Windows 业务成功条件。

发给 Windows：`RelayEnvelope { id: request.requestId, kind: "control", deviceToken, request, pair: null }`。**内外 UUID 及 RemoteCommand 所有字段不改写**。

Windows 回 `RelayResponse { id, statusCode, reply: RemoteReply, pair: null, errorCode }`。Relay 将 Windows 的 HTTP status 和 Reply 对象返回手机，包含业务失败；不会把中继收件或发送成功当作业务成功。

### 配对

`POST /v1/devices/{deviceId:guid}/pair` 使用 controllerToken 认证，body 为 `PairRequest { code, clientName }`，不要求 X-Device-Token。code/clientName 不得为空，最多 128/256 字符。

发给 Windows：`RelayEnvelope { id: <临时相关UUID>, kind: "pair", deviceToken: null, request: null, pair }`。Windows 返回匹配 id 的 RelayResponse.Pair，Relay 原样回传 status/PairReply；新客户端 UUID 和 token 只能来自 Windows，不由 Relay 生成。

若 Windows 只返回失败 status 和 errorCode，则回该 status 与 `{ "errorCode": "..." }`。缺少成功 Reply/Pair、回复类型不匹配、控制 UUID/version 不匹配、回复携带错误设备等是 **502 invalid_device_response**，不是成功。协议回复必须可承载 JSON body；1xx、204、205、304 等无有效业务 body 的状态视为无效设备回复。未知/迟到的 envelope id 丢弃。

## 生命周期、安全限制和错误

| 条件 | 结果 |
| --- | --- |
| 无/错误凭据、角色错误、设备未配置 | 401，不泄漏设备是否存在 |
| 路由 UUID 不能解析 | 404 |
| 错误 deviceId/version/requestId/command、过远 deadline、错误 JSON | 400 |
| 缺少 JSON Content-Type | 415 |
| 设备未连接或连接丢失 | 503；不会存为离线命令 |
| 第二连接、已接纳控制 UUID 重复 | 409 |
| 超时或 deadline 已过期 | 504 |
| HTTP body 或封装后的消息超过 64 KiB | 413 |
| 入口限流、每设备 16 个 pending 满额、重放索引满额 | 429，不排队等待额度 |

- HTTP body 以实际读取字节计数，包括 chunked；WebSocket 以**完整消息所有分片累计 UTF-8 字节**限制 64 KiB，不仅检查单帧。只收文本 JSON；超限关闭码 1009、二进制 1003、错误 JSON 1007，并立即取消 pending。关闭握手最多等待 1 秒。
- 单设备最多一个连接，总连接/握手预留数不超过配置设备数；control 与 pair 合计最多 16 pending。每连接只有一个发送锁，关闭帧也使用该锁；排锁/握手/发送/回复等待共用截止时间。
- 等待上限为 `min(配置超时, expiresAt-now)`，pair 使用配置超时，硬上限 100 秒。断线或进程停止取消 pending；请求取消释放资源，但**不代表 Windows 已取消可能开始的操作**。
- 已接纳的 control UUID 保留 120 秒的进程内去重记录，覆盖原请求全部合法有效期；超时、客户端取消、重连也不清除。每设备最多 4096 条，满额拒绝新控制，不驱逐未过期记录。相同 UUID 可能修改 expiresAt 的重试也在该窗口拒绝。过期原报文始终拒绝。
- 此去重不是永久/持久化 exactly-once 保证：进程重启或窗口结束后不能保证旧 UUID 不再使用。调用方每条新命令须新建 UUID；Windows 仍应保留自己的授权、过期和操作幂等检查。
- Relay 从不重发掉线/超时命令，也不把旧连接 pending 转交新连接。遇到 503/504，操作可能已执行而回复丢失；应以**新 requestId** 查询 status/operation，勿自动重试 toggle、set 或配对来猜测结果。
- 入口在认证前使用真实 TCP peer IP 固定一分钟窗口限流，并有 `min(设备数 × requestsPerMinute, 6000)` 的全局每分钟上限；无 token 作为限流 key，无请求等待队列。代理后的用户共享代理 IP 额度，应在代理另加按客户端来源的限流。
- 本实现不处理/信任任何 Forwarded/X-Forwarded-*，即使环境开启 ASP.NET Core 转发头自动处理，也将其禁用。不要靠客户端声称的 https 判断安全，TLS 与网络访问控制是反代部署责任。若未来需要真实客户端 IP，必须先明确配置可信代理地址/网段，不能直接信任全部来源。

## Linux Docker / Compose 部署

仓库根目录作为构建 context：

```sh
docker build -f src/relay/Dockerfile -t fy1switch-relay:local .
export FY1SWITCH_RELAY_CONFIG=/absolute/private/relay.config.json
docker compose -f src/relay/compose.yaml config --quiet
docker compose -f src/relay/compose.yaml up -d --build
```

- 多阶段 Dockerfile 自包含构建 linux-x64/linux-arm64（TARGETARCH amd64/arm64），runtime-deps 最终镜像以非 root APP_UID 运行。没有依赖宿主 dotnet、Windows runtime 或父构建配置。
- 管理模式的命名卷 `relay-data` 挂载到 `/var/lib/fy1switch-relay`, 镜像为非 root APP_UID 创建可写目录; 其他根文件系统继续只读. 不删除该卷来进行普通升级.
- BuildKit 使用邻接的 `src/relay/Dockerfile.dockerignore`, 旧构建器使用仓库根 `.dockerignore`; 两者及 `src/relay/.dockerignore` 保持相同白名单. 每次重新允许父目录后先排除其全部后代, 再精确开放源码和 WebUI 资源, 避免目录继承意外放行锁文件或私有配置. 仅传入 Relay/共享契约源码和项目文件, 排除配置, Git, 凭据, bin/obj, packages lock, Windows/Android 和父构建文件.
- Compose 单实例、宿主仅映射 `127.0.0.1:5080`；容器内部监听 8080 不代表可公开此端口。根文件系统只读，/tmp 为有界 tmpfs，配置只读挂载，不自动创建缺失宿主文件。
- 配置宿主路径必须绝对且确实为文件。为镜像非 root 用户授予只读访问；官方镜像 app UID 通常为 1654，正式部署应核实具体镜像 UID/宿主权限，不要为图省事改成全员可读。应按运维策略固定镜像 digest 并定期更新补丁。
- 不执行 scale，不启动两个实例共享同一设备凭据。凭据轮换会断开连接，须协调 Windows/手机配置后重新连接。

### HTTPS 反代示例

管理模式使用首次密码文件时, 在上述 Compose 命令中追加覆盖文件:

```sh
export FY1SWITCH_RELAY_ADMIN_PASSWORD_FILE=/absolute/private/relay-admin-password
docker compose -f src/relay/compose.yaml -f src/relay/compose.management.yaml config --quiet
docker compose -f src/relay/compose.yaml -f src/relay/compose.management.yaml up -d --build
```

设置 `FY1SWITCH_RELAY_CONFIG` 指向基于管理示例准备的私有配置, `publicUrl` 必须等于最终浏览器 HTTPS Origin. 初始化密码文件需可被容器非 root 用户读取. 管理页只能从该 HTTPS Origin 打开; 直接浏览 loopback HTTP 不能完成 Secure Cookie/Passkey 登录. 服务仍不接受任意转发头, 反代必须保留正确 Host. 本次源码改动不自动部署, 更新已有线上实例需另行授权.

可在同一 Linux 宿主的 Caddy 使用受你控制的域名（DNS 指向宿主，按 Caddy 要求开放证书签发/HTTPS 端口）：

```caddyfile
relay.example.com {
    reverse_proxy 127.0.0.1:5080 {
        transport http {
            response_header_timeout 110s
        }
    }
}
```

Caddy 处理 HTTPS 证书与 WebSocket Upgrade，保持 Authorization 和 X-Device-Token，不记录它们或 body。客户端必须直接连接 HTTPS/WSS，不能先用明文 URL 发送凭据再依赖 301 跳转。防火墙不开放 5080；不同机器的反代与 Relay 之间也须加密/使用受控私网。若代理本身在容器中，127.0.0.1 不是 Relay 宿主，应设计私有容器网络，不要顺手把后端发布为 0.0.0.0。

Nginx 等替代代理需配置 HTTP/1.1 Upgrade/Connection 转发、请求 body 上限至少容纳 64 KiB、响应等待大于 100 秒以及适合长连接的读取超时。代理的来源限流、TLS 证书信任和转发头配置不属于 Relay 本地测试的已验证结论。

## 本地验证

```powershell
./tools/Test-Relay.ps1
./tools/Test-Relay.ps1 -DockerBuild
```

默认入口执行 locked restore、Release build、MSTest；所有服务请求只访问随机 loopback 端口的真实 Kestrel + ClientWebSocket fixture。测试配置/随机 token 写入测试输出下的临时 TestResults，结束删除，不访问真实设备或公网中继。DockerBuild 仅额外构建本地镜像，不启动容器、不部署；必须已有 Docker。

覆盖认证/设备隔离、第二连接 409、离线、deadline/超时、同 UUID/枚举透传、控制/配对回复、重放、断线重连、取消、16 pending、错误响应、HTTP 固定长度/chunked 超限、WebSocket 分片/超限及伪造转发头限流。

验收边界：本地自动化不能替代 Linux 容器实际启动、公网 HTTPS/WSS、反代证书、真实 Windows/Android 互通或硬件业务验收。请以执行者最终报告区分已运行与未运行验证。

## Huawei 实例部署记录与更名边界

2026-10-16 部署的实例使用更名前的旧版本 `d297ddf`. 该记录当时已核对 SSH 目标 `huawei`, 公网入口 `https://screlay.fy1ng.cn`, 公网 TLS, 单实例容器及仅监听回环的后端. Docker 与 Caddy 已设置开机启动, 但未通过重启服务器验证恢复.

本次只更名仓库源码, 没有重命名服务器上的目录或容器, 没有修改线上凭据, 也没有部署或重启服务. 旧部署的真实目录, 容器名和回滚路径仍以部署时的服务器清单及 `d297ddf` 版本文档为准. 不要把当前源码中的 `fy1switch-relay` 路径直接套用到旧部署.

新版 Windows 使用独立设备身份. 使用中继前, 需要为新设备重新配置 Windows 和控制端各自的凭据, 并按当前部署流程单独安排服务切换. 当前配置入口为 `FY1SWITCH_RELAY_CONFIG`; 安全模型, 反向代理 TLS 和仅监听回环的后端要求不变.

当时的核验范围为: 非 root 只读容器运行, 回环后端未认证返回 401, 公网 HTTPS 证书校验及未认证 401, Windows 角色凭据完成空 WSS 握手并正常关闭, 控制端凭据通过认证后因缺少每手机 token 返回 400. 未转发控制命令, 未改客户端配置, 未验证真实 Windows/Android 跨网控制或硬件开关. 更名构建不能替代上述线上验收.
