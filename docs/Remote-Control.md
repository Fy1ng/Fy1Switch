# 远程控制与配对

## 安全模型

LAN、Tailscale 和应用 Relay 都要求每台手机独立的 Windows 设备凭据。Tailscale IP、设备 UUID、Relay 房间都不是密码。Windows 默认不开放网络；本地设置启用后才启动 HTTPS。没有自动修改防火墙、路由器、Tailscale 策略或部署服务器。

直连使用持久设备证书。Windows 将证书私钥经当前用户 DPAPI 保护保存，手机通过配对信息得到 SPKI SHA-256 pin，而不是忽略 TLS 证书校验。该 pin 是校验 Windows 身份的依据，配对信息必须经可信渠道交给自己的手机。证书有效期三年，证书损坏或过期不会静默生成与旧配对不同的身份。

配对信息有效五分钟，只能完成一次；重新生成使旧码失效。成功后手机得到独立随机 token，Windows 仅保存 SHA-256 token 哈希，手机使用 Android Keystore 保护保存。撤销手机只影响该客户端后续请求，不会撤回已经执行的显示命令。

## Windows 设置

先在主界面完成本地显示器绑定，确认本地开关正常。从“手机配对与远程设置”进入：

1. 勾选启用 HTTPS。
2. 监听 IP 默认 127.0.0.1，只能本机访问。填 Windows 的 LAN/Tailscale IP 可限定接口；0.0.0.0 监听全部 IPv4 接口，不建议无意开放到不可信网络。
3. 端口默认 48152；手机使用地址填实际可访问的 https://地址:端口。
4. 保存并应用。若绑定地址不存在、端口占用或私钥不可读，服务启动失败，不会假装已开启。
5. 生成配对信息，复制到 Android App。临时配对 JSON 含秘密，不要发到公开聊天或保存进代码仓库。

没有自动添加防火墙规则。如果手机无法直连，先核对 HTTPS 监听、Windows 防火墙、同局域网隔离、Tailscale 设备在线及 ACL。手机只能有一个活动 VPN 的系统约束也可能影响 Tailscale。按 Tailscale 地址连接时，底层可以是 UDP 直连或 DERP，不影响本项目设备认证。

## Android 与快捷开关

在 Android App 粘贴 Windows 配对 JSON 并完成配对，可保存多台 Windows 设备，选择默认目标。手机显示设备在线状态及最近观测的显示器状态；不是不断访问 DDC 的实时仪表盘。

快捷设置 Tile 需要用户加入系统面板。App 不静默修改系统面板。展开时进行短时刷新，点击时发送命令，锁屏默认先解锁。On/Off 必须根据返回状态呈现；超时、未确认、离线不能伪装为关闭。

OriginOS 6 的后台回收、系统面板缓存、解锁取消、强行停止后的行为和覆盖升级保留需要在目标手机实测；APK 编译和 JVM 测试不等于这些行为已通过。

## 自建 Relay

Relay 是可信服务，手机到 Relay、Windows 到 Relay 各自使用 TLS。Relay 可读取转发请求与手机设备 token；这不是 Relay 不可读的端到端加密。不要把这套模型描述为服务器被攻陷也不能控制设备。

服务器给每台 Windows 配置两个独立 Relay 凭据：Windows 连接凭据和手机控制通道凭据。Windows 远程设置填写 Relay HTTPS 地址及 Windows 凭据；手机在设备设置中填写同一 Relay 地址和控制通道凭据。Windows 出站 WSS 连接，无需家庭公网入站。两种 Relay 凭据都不同于每台手机在 Windows 配对后获得的 device token。

部署说明在 Relay.md。默认首次配对走已固定证书的直连；完成后可启用 Relay 作为连接途径。Relay 不长期排队离线显示命令，不在设备重连后补发旧 OFF。

## 协议与不确定结果

POST /v1/control 使用 Bearer device token；请求含 version=1、requestId、deviceId、expiresAt，命令只允许 status、refresh、set、operation。set 必须携带绑定 targetId、On/Off/Toggle 和 operationId。远程端没有发现/绑定/自启/退出等本地管理接口。

requestId 用于一次通信关联，operationId 用于跨通信通道的业务去重。网络超时不能证明命令没有执行；换通道重试保留同一 operationId，不新建一次 Toggle。读取既有操作结果应发送新请求，不能重放已过期信封。请求有效期上限 120 秒，过期或不匹配设备拒绝。

返回操作包括 Pending、Confirmed、AcceptedUnconfirmed、Failed、Unknown；只有硬件读回符合请求才是 Confirmed。单次 HTTP 200 并不等于实体显示器已完成开关。显示器拓扑失效后，手机不能自动更换或重新绑定另一个目标，需回到 Windows 确认。

## 本轮验证与交付

2026-10-05：Windows 原回归 248/248、Remote HTTPS/配对 92/92、Relay 53/53、真实 HTTPS/WSS 跨服务 1/1 通过。Android 单元测试 54/54，实际 HttpsTransport→Windows loopback HTTPS→模拟 Worker 联调 1/1 通过，包含错误 pin 拒绝和重复操作去重。Android lint 0 错误、6 警告；debug APK 签名验证通过。

Windows 新版在 artifacts/windows-remote/win-x64，Android 调试 APK 在 artifacts/android，Linux Relay 自包含目录在 artifacts/relay/linux-x64。已有 Windows 旧版运行时改用独立输出目录，不自动终止用户应用。开启新版前需自己退出旧版托盘进程。

没有安装 APK、打开真实远程端口、防火墙放行、登录 Tailscale、部署服务器或运行 Docker。Linux 产物与隔离构建输入检查通过，不等于 Linux 容器实际运行通过。OriginOS 面板/后台/解锁、真实手机网络与公网中继仍未验收；后置的 Windows 系统级恢复亦保持未验证。

## 运行数据

Windows remote.json 保存开关、监听和已加密 Relay 凭据；identity.pfx.protected 是 DPAPI 私钥；clients.json 是客户端名/UUID/token哈希，不含明文 token。此目录仍属于个人运行数据，不提交、不上传诊断包。取消远程控制关闭 HTTPS 与 WSS，并取消尚未使用的配对码。

本地 Worker 硬件权限、Windows 登录会话和此前系统级验收边界仍然适用。整机睡眠或关机不属于显示器 OFF；本项目不默认提供 Wake-on-LAN。
