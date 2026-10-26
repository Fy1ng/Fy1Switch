# 远程控制与配对

## 自动多路径（2026-10-07）

同一设备可配置局域网 HTTPS、Tailscale HTTPS 和可信 Relay，按此顺序尝试；未配置的路径跳过。所有入口使用原设备身份、证书和手机授权，不需要为每条路径重新绑定显示器。旧版单地址配置继续有效。Tailscale 客户端和 Relay 服务仍须单独安装、配置，本项目不会自动登录、开放防火墙或部署中继。

失败路径从整轮请求结束后冷却五秒, 之后由下一次请求触发只读 `status` 恢复探测; 没有永驻后台探测. 后续存在未标记失败的备用路径时, 探测最多一秒并为备用路径保留请求预算. 没有这种备用路径时, 探测可使用当前路径的常规预算, 并为原请求保留至少一秒, 避免恢复较慢的连接持续被短探测拦截. 配置 Tailscale 时两个直接入口各最多六秒, 中继最多十二秒, 始终受同一次请求剩余期限限制. 主界面显示最近成功路径, 不把历史路径当成当前在线保证. 手机清空 Tailscale 地址并保存会停用自动学习, 可通过勾选自动更新重新启用.

切换仅处理连接层失败；证书、身份、认证和协议错误直接提示，不以切换路径掩盖异常。一次开关操作跨路径保留相同请求内容、操作 ID 和截止时间，不并行发送开关命令。超时不能证明没有执行；结果不确定时查询已有操作，不生成新的 Toggle。

Windows 的可选 Tailscale 入口与 LAN 入口共享证书；可选入口不可用不能阻止正常 LAN 入口。配对载荷与认证状态响应通过可选 `tailscaleEndpoint` 字段交付 Tailscale 地址。未经认证的局域网广播不能修改可信地址。手机高级设置可维护该地址，已有手机无需清除凭据重新配对。Tailscale 路径底层可能使用 DERP，与本应用自建 Relay 不是同一层。

跨网络验收：先确认 LAN 路径；手机离开家庭 Wi-Fi 并保持 Tailscale 在线，确认切换至 Tailscale；再在已配置 Relay 的情况下关闭手机 Tailscale，确认使用中继；恢复 LAN 后确认优先路径恢复。检查每次操作仅执行一次、Tile 状态不把离线当作关闭。上述步骤需要目标手机和实际网络，自动化测试不能替代。物理拔插及整机恢复仍按原约定后置。

## 安全模型

LAN、Tailscale 和应用 Relay 都要求每台手机独立的 Windows 设备凭据。Tailscale IP、设备 UUID、Relay 房间都不是密码。Windows 程序启动时自动运行 HTTPS 手机控制服务, 默认选择一个活动的物理局域网地址; 没有合适局域网时仅监听 loopback. 读取或控制仍要求已配对手机的独立凭据, 配对码仅在用户主动打开配对界面时生成. 没有自动修改防火墙、路由器、Tailscale 策略或部署服务器。

直连使用持久设备证书。Windows 将证书私钥经当前用户 DPAPI 保护保存，手机通过配对信息得到 SPKI SHA-256 pin，而不是忽略 TLS 证书校验。该 pin 是校验 Windows 身份的依据，配对信息必须经可信渠道交给自己的手机。证书有效期三年，证书损坏或过期不会静默生成与旧配对不同的身份。

配对信息有效五分钟，只能完成一次；重新生成使旧码失效。成功后手机得到独立随机 token，Windows 仅保存 SHA-256 token 哈希，手机使用 Android Keystore 保护保存。撤销手机只影响该客户端后续请求，不会撤回已经执行的显示命令。

## Windows 设置

先在主界面完成本地显示器绑定, 确认本地开关正常, 然后打开主窗口的"设置".

1. 手机控制服务随程序启动, 无需开关. 首次配置自动识别活动的 Wi-Fi 或以太网, 填写局域网 IP 和 HTTPS 访问地址, 默认端口为 48152. 已有自定义地址和端口保持原值.
2. 默认显示网络和访问地址摘要. "高级设置"内可关闭"自动选择局域网", 手动选择网卡或修改监听 IP、端口和手机访问地址. 自动推荐优先保留仍可用的已选地址, 否则优先物理 Wi-Fi/以太网, 跳过已识别的 VPN/虚拟接口. 多网卡推荐不能保证手机可达; 无合适局域网时提示连接网络或手动配置.
3. 需要备用路径时勾选"启用 Tailscale", 再填写展开的子表单. 自动检测并填写 Tailscale IP 和 HTTPS 地址, 可修改 IP 或重新检测, HTTPS 地址随 IP 和端口生成. 开启"启用中继"后才显示中继 HTTPS 地址和电脑端凭据.
4. 连接参数在停止输入约 650ms 后自动保存, 离开设置页或退出前也会提交有效修改. 输入不完整时提示尚未保存; 新地址或端口启动失败时恢复原有连接. 关闭 Tailscale 或中继自动停用该路径, 保留已填地址和已加密凭据. 中继接入码不作为连接参数保存, 仍需点击注册到中继完成授权. 自动模式启动时重新识别 LAN, 网络变化时可打开设置或刷新网络列表重新检测.
5. 返回主页面点击"连接手机", 在手机扫描二维码或输入电脑显示的六位码. 设置页没有连接手机按钮. 备用配对信息复制入口位于高级设置, 信息包含一次性凭据, 不应公开分享.

若主入口绑定地址不存在、端口占用或私钥不可读, 服务启动失败. 修改配置启动失败时会尝试恢复已有有效连接; 修正输入后重新自动保存. 可选 Tailscale 入口不可用时保留 LAN 主入口. 自动识别不会登录 Tailscale、部署中继或修改防火墙和路由器. 高级设置中的 `0.0.0.0` 会监听全部 IPv4 接口, 请按实际网络选择监听范围.

没有自动添加防火墙规则。如果手机无法直连，先核对 HTTPS 监听、Windows 防火墙、同局域网隔离、Tailscale 设备在线及 ACL。手机只能有一个活动 VPN 的系统约束也可能影响 Tailscale。按 Tailscale 地址连接时，底层可以是 UDP 直连或 DERP，不影响本项目设备认证。

## Android 与快捷开关

六位短码增量（2026-10-06）：电脑远程设置与手机配对页都会显示本机码并自动发现同一局域网对端。可从任一端选择设备并输入对方码，配对页必须保持打开。原生 mDNS 只传临时会话标识/角色，不传短码、pin 或 token；短码走 SRP-6a，成功后加密交付原 HTTPS 高熵引导，不能用搜索到的设备名当作身份验证。规则与固定 wire 见 Pairing-v1.md。

电脑从地址候选明确选择 LAN 地址并保存后即可自动开始配对；保留已有有效设置，不自动绑定全部网卡。Tailscale 继续支持扫码/固定证书 HTTPS，但不承诺局域网 mDNS 跨 Tailscale 传播。手机短码监听仅绑定 Wi-Fi 地址，退出配对页、后台、锁屏或成功后关闭。

在 Android App 粘贴 Windows 配对 JSON 并完成配对，可保存多台 Windows 设备，选择默认目标。手机显示设备在线状态及最近观测的显示器状态；不是不断访问 DDC 的实时仪表盘。

快捷设置 Tile 需要用户加入系统面板。App 不静默修改系统面板。展开时进行短时刷新，点击时发送命令，锁屏默认先解锁。On/Off 必须根据返回状态呈现；超时、未确认、离线不能伪装为关闭。

OriginOS 6 的后台回收、系统面板缓存、解锁取消、强行停止后的行为和覆盖升级保留需要在目标手机实测；APK 编译和 JVM 测试不等于这些行为已通过。

## 自建 Relay

Relay 是可信服务，手机到 Relay、Windows 到 Relay 各自使用 TLS。Relay 可读取转发请求与手机设备 token；这不是 Relay 不可读的端到端加密。不要把这套模型描述为服务器被攻陷也不能控制设备。

推荐使用带 WebUI 的 Relay 管理模式. 管理员用密码或 Passkey 登录, 生成一次性六位 Windows 接入码; Windows 设置填写 Relay HTTPS 地址和码, 自动保存加密凭据并建立出站 WSS, 无需家庭公网入站. Android 不登录 WebUI. 已配对手机在局域网自动获取 Relay 地址和专属通道凭据, 异地只需输入 Relay 地址并保存/重新连接, 由 Windows 验证原手机授权后发放通道凭据. 首次手机配对可在 Windows 生成独立六位 Relay 码, Android 输入 Relay 地址和码完成. 每手机 deviceToken 始终由 Windows 签发和撤销, 与 Relay 通道凭据不同. 旧静态配置和高级手填 Windows 凭据保留兼容, 显式导入方式见 Relay.md.

部署说明在 Relay.md. 直连配对仍使用固定证书; Relay 首次配对依赖用户指定的可信 HTTPS 服务, Windows 经已注册的 WSS 签发手机身份和证书 pin. 两类 Relay 六位码最多 5 分钟且单次有效. 已配对自动授权要求 Windows 在线且授权未撤销. Relay 不长期排队离线命令, 不在重连后补发旧 OFF.

## 协议与不确定结果

POST /v1/control 使用 Bearer device token；请求含 version=1、requestId、deviceId、expiresAt，命令只允许 status、refresh、set、operation。set 必须携带绑定 targetId、On/Off/Toggle 和 operationId。远程端没有发现/绑定/自启/退出等本地管理接口。

requestId 用于一次通信关联，operationId 用于跨通信通道的业务去重。网络超时不能证明命令没有执行；换通道重试保留同一 operationId，不新建一次 Toggle。读取既有操作结果应发送新请求，不能重放已过期信封。请求有效期上限 120 秒，过期或不匹配设备拒绝。

返回操作包括 Pending、Confirmed、AcceptedUnconfirmed、Failed、Unknown；只有硬件读回符合请求才是 Confirmed。单次 HTTP 200 并不等于实体显示器已完成开关。显示器拓扑失效后，手机不能自动更换或重新绑定另一个目标，需回到 Windows 确认。

## 本轮验证与交付

2026-10-07 自动多路径：Remote 116、Pairing 50、Relay 53、HTTPS/WSS 端到端 1 项通过；Android 96 项单元测试、lint（零错误、五项警告）和 APK 构建通过，Android HttpsTransport 到本机 Windows HTTPS 模拟 Worker 联调通过。测试覆盖双入口共享身份、备用入口不可用、串行回退、去重标识、冷却恢复只读探测、认证失败拒绝回退、旧配置迁移和手动停用。Windows 自包含产物位于 `artifacts/windows-multipath/win-x64`，APK 位于 `artifacts/android/Fy1Switch-multipath-debug.apk`。未替换运行中的程序、安装手机应用或验证真实跨网络。

2026-10-05：Windows 原回归 248/248、Remote HTTPS/配对 92/92、Relay 53/53、真实 HTTPS/WSS 跨服务 1/1 通过。Android 单元测试 54/54，实际 HttpsTransport→Windows loopback HTTPS→模拟 Worker 联调 1/1 通过，包含错误 pin 拒绝和重复操作去重。Android lint 0 错误、6 警告；debug APK 签名验证通过。

Windows 新版在 artifacts/windows-remote/win-x64，Android 调试 APK 在 artifacts/android，Linux Relay 自包含目录在 artifacts/relay/linux-x64。已有 Windows 旧版运行时改用独立输出目录，不自动终止用户应用。开启新版前需自己退出旧版托盘进程。

没有安装 APK、打开真实远程端口、防火墙放行、登录 Tailscale、部署服务器或运行 Docker。Linux 产物与隔离构建输入检查通过，不等于 Linux 容器实际运行通过。OriginOS 面板/后台/解锁、真实手机网络与公网中继仍未验收；后置的 Windows 系统级恢复亦保持未验证。

## 运行数据

Windows remote.json 保存开关、监听和已加密 Relay 凭据；identity.pfx.protected 是 DPAPI 私钥；clients.json 是客户端名/UUID/token哈希，不含明文 token。此目录仍属于个人运行数据，不提交、不上传诊断包。取消远程控制关闭 HTTPS 与 WSS，并取消尚未使用的配对码。

本地 Worker 硬件权限、Windows 登录会话和此前系统级验收边界仍然适用。整机睡眠或关机不属于显示器 OFF；本项目不默认提供 Wake-on-LAN。
