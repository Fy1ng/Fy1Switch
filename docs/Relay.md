# 自建 ASP.NET Core Relay

## 边界与信任

- .NET 10，单进程、单实例、纯内存设备路由；不使用 Redis、数据库或离线命令队列。禁止多副本/滚动重叠运行，也不能靠反代轮询多个 Relay 实例。
- Windows 主动建立出站 WebSocket，手机使用 HTTPS。Relay 只鉴别中继凭据、限制协议并转发，不能代替 Windows 验证每手机 deviceToken、配对码、显示器状态或业务授权。
- 生产入口必须为 HTTPS/WSS，TLS 在受控反向代理终止。默认进程只监听 `http://127.0.0.1:5080`；`ASPNETCORE_URLS` 可覆盖。不得将带 Bearer/deviceToken 的明文 HTTP/WS 暴露公网或不可信局域网。
- **这是可信应用层中继，不是端到端加密隧道**：Relay 与 TLS 终止代理可见命令、配对码、回复和每手机 token。运行者必须可信。配置里的中继凭据以明文文件保存，仅运行时认证索引使用 SHA-256 哈希及常量时间比较。
- Tailscale DERP 是转发 WireGuard 加密报文的网络中继，不解析本应用协议，也不会自动提供本项目的设备路由、配对或授权接口。两者不是可直接互换的实现。

## 文件与构建隔离

- 服务：`src/relay/RemoteDisplaySwitch.Relay/`；测试：`tests/RemoteDisplaySwitch.Relay.Tests/`。
- 直接链接编译现有 `src/shared/RemoteDisplaySwitch.Contracts/Messages.cs` 和 `RemoteMessages.cs`，不复制/修改共享协议，不构建 Windows/Android 项目，也不在共享目录生成输出。
- Relay 和测试 csproj 均显式清空父 `Directory.Build.props` 的 `RuntimeIdentifiers`。Docker 只复制所需源码和 Relay csproj，**不复制父 Directory.Build.props、global.json 或本机 bin/obj**；镜像独立使用 .NET 10 SDK 发布 self-contained Linux 程序。

## 配置和凭据

`RDS_RELAY_CONFIG` 必须是可读取 JSON 文件的**绝对路径**，缺失、相对路径、错误 JSON、空/重复设备、重复凭据或弱长度均拒绝启动。启动加载一次，更新/轮换需协调停止旧实例后重启；不会热重载。

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
$env:RDS_RELAY_CONFIG = (Resolve-Path './src/relay/relay.config.local.json').Path
dotnet run --project ./src/relay/RemoteDisplaySwitch.Relay/RemoteDisplaySwitch.Relay.csproj --configuration Release --no-launch-profile
```

生成文件仅为 `src/relay/relay.config.local.json`，已局部 gitignore；生成后仍需保护权限并通过可信渠道分发。仓库示例始终不含真实凭据。

## HTTP / WebSocket 协议

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

## Linux Docker / Compose（手动步骤，未自动部署）

仓库根目录作为构建 context：

```sh
docker build -f src/relay/Dockerfile -t remote-display-switch-relay:local .
export RDS_RELAY_CONFIG=/absolute/private/relay.config.json
docker compose -f src/relay/compose.yaml config --quiet
docker compose -f src/relay/compose.yaml up -d --build
```

- 多阶段 Dockerfile 自包含构建 linux-x64/linux-arm64（TARGETARCH amd64/arm64），runtime-deps 最终镜像以非 root APP_UID 运行。没有依赖宿主 dotnet、Windows runtime 或父构建配置。
- 现代 Docker/BuildKit 实际读取邻接的 **Dockerfile.dockerignore**，与提供的 `src/relay/.dockerignore` 使用相同白名单。仓库根 context 不会自动采用子目录 .dockerignore，所以不能省略前者。仅传入 Relay 源文件与共享契约源文件及其项目文件；排除配置、.git、凭据、bin/obj、Windows/Android 和整个父构建文件。
- Compose 单实例、宿主仅映射 `127.0.0.1:5080`；容器内部监听 8080 不代表可公开此端口。根文件系统只读，/tmp 为有界 tmpfs，配置只读挂载，不自动创建缺失宿主文件。
- 配置宿主路径必须绝对且确实为文件。为镜像非 root 用户授予只读访问；官方镜像 app UID 通常为 1654，正式部署应核实具体镜像 UID/宿主权限，不要为图省事改成全员可读。应按运维策略固定镜像 digest 并定期更新补丁。
- 不执行 scale，不启动两个实例共享同一设备凭据。凭据轮换会断开连接，须协调 Windows/手机配置后重新连接。

### HTTPS 反代示例

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
