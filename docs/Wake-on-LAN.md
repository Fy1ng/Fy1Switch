# Wake on LAN 远程开机

WOL 是可选的独立通道. 手机连接公网 HTTPS Gateway, Gateway 经已经建立的 WireGuard 隧道连接 OpenWrt, 由 OpenWrt 发送 Ethernet WOL 魔术包. 手机和 Windows 都不需要为了 WOL 安装 WireGuard. Gateway 不依赖 Windows Host、Windows HTTPS、Relay WSS 或用户登录会话.

```text
Android App / 远程开机磁贴
  -> HTTPS + 每手机独立授权
VPS 反向代理 -> loopback WOL Gateway
  -> 已有 WireGuard + 固定主机密钥的 SSH
OpenWrt forced-command -> 白名单 etherwake -> Windows 网卡

Windows 用户登录 + Fy1Switch Host 启动
  -> 原 LAN / Tailscale / Relay 显示器控制通道恢复
```

`packet_sent` 只表示受限脚本报告已执行发送. 它不证明电脑已启动、Windows 已完成登录、Host 已上线或显示器已经开启. 已认证的原控制通道返回匹配电脑 UUID 的状态后, App 才确认 Host 可达. WOL Gateway 不提供 Windows 状态探测或显示器命令转发.

## 使用条件

- Ubuntu VPS, ASP.NET Core Runtime 10.0 和 OpenSSH client. Gateway 是独立 .NET 10 服务, 不要求安装完整 Relay 或数据库服务.
- VPS 到 OpenWrt 的既有 WireGuard 隧道, 以及仅经该隧道可达的 OpenWrt SSH.
- OpenWrt 的 `etherwake`, 目标 LAN 接口和可唤醒的有线网卡.
- Gateway 使用有效 HTTPS 证书. Android 校验系统 CA 和服务器名称, 不接受 HTTP、自签证书绕过或不安全重定向.
- 手机在移动网络下继续控制显示器时, 仍须配置可从该网络访问的原控制通道, 例如 Relay. Gateway 本身不会把家庭 LAN 地址变为公网可达.

电脑断电、拔网线、断开插座电源或关闭待机网卡供电时不能唤醒. 电脑离线也可能是 Host 未启动、尚未登录、网络故障或授权失效, 不等于关机.

## 1. Windows 网卡与 BIOS

1. 在主板 BIOS/UEFI 中按厂商说明启用 Wake on LAN / Wake by PCI-E / PME 等功能. ErP、深度节能可能切断关机后的网卡供电. 不同主板名称和支持的电源状态不同.
2. 在 Windows 设备管理器中选择实际连接 OpenWrt LAN 的有线网卡. 对 Realtek PCIe 5GbE Family Controller, 按当前驱动支持情况核对 Wake on Magic Packet、Shutdown Wake-On-Lan 和电源管理选项. 不应仅凭某个选项名称断言 S5 已支持.
3. Windows 快速启动可能改变关机唤醒行为. 先记录当前设置, 按网卡/主板文档测试普通关机、关闭快速启动后的关机和睡眠. Fy1Switch 不自动修改这些设置.
4. 在 Fy1Switch Windows 设置中的 WOL 辅助区域刷新网卡列表, 明确选择正确的有线网卡并保存. 列表是候选清单, 不代表程序验证了 BIOS 或 S5 能力. 网卡更换、移除或 MAC 变化后重新确认.
5. 复制电脑 UUID、名称和 MAC 配置片段. UUID 必须是电脑身份, 不能填写显示器绑定 TargetId. 不把电脑现有配对 token 或 Relay 凭据复制到 Gateway.

Host 仍是当前用户的托盘应用, 自启动发生在用户登录后. WOL 不开启自动登录, 不创建 Windows 系统服务. 睡眠恢复沿用已有身份和显示器拓扑验证, 不补发之前结果不明的显示器操作.

## 2. OpenWrt 本地验证

由操作者在可信管理会话中完成, 不通过公网开放 SSH 或 WOL UDP. 安装命令随 OpenWrt 包管理器而异; 使用 `opkg` 的版本可执行:

```sh
opkg update
opkg install etherwake
command -v etherwake
ip link show
```

选择真实 LAN bridge/interface 后, 在电脑关机或睡眠时手动运行一次 `etherwake -i <LAN_INTERFACE> <PC_MAC>`. 这是实际唤醒操作, 应由操作者在准备好的硬件测试中执行. 先确认局域网直接唤醒有效, 再排查远程通道.

`etherwake` 使用原始 Ethernet socket, 通常需要 root 或明确授予的 `CAP_NET_RAW`. 默认说明使用 OpenWrt 专用受限 root SSH 密钥. VPS 服务账户仍是非 root. 不给普通账号无限制 sudo, 不为方便而为任意程序设置 setuid.

## 3. 复用 WireGuard

示例网络可为 VPS `10.88.0.1/24`、OpenWrt `10.88.0.2/24`, VPS 接口 `wg0`, 路由器逻辑接口 `wg_vps`. 这些只是部署示例, 业务代码不依赖它们.

通过已有运维方式检查接口、到 OpenWrt 的路由、peer handshake 和传输计数. 不运行重新生成配置、重建隧道或覆盖现有密钥的安装脚本. Gateway 配置引用实际 WG 接口、源地址和路由器地址, 不会管理 WireGuard.

SSH 连接失败不能单独证明隧道断开. WG 接口缺失、目标不可达、SSH 服务未监听和凭据被拒绝是不同故障. 不将 SSH 失败回退到 OpenWrt 公网地址、LuCI 或其他管理接口.

## 4. SSH 密钥和 Dropbear 限制

部署前记录实际 OpenWrt/Dropbear 版本和包信息:

```sh
dropbear -V
ubus call system board
opkg status dropbear
```

使用其他包管理器时替换包查询命令. 检查当前 Dropbear 启动配置及该用户实际使用的 authorized_keys 路径. 不假定 root 的 `/etc/dropbear/authorized_keys` 与所有普通用户的路径相同.

上游支持 `command="..."`, `no-port-forwarding`, `no-agent-forwarding`, `no-X11-forwarding`, `no-pty`, 但支持取决于版本和 `DROPBEAR_SVR_PUBKEY_OPTIONS` 等编译选项. 必须在安装的二进制上验证, 不能仅根据在线最新版文档推断. 参考 [OpenWrt 配置选项](https://github.com/openwrt/openwrt/blob/main/package/network/services/dropbear/Config.in) 和 [Dropbear 手册](https://github.com/mkj/dropbear/blob/main/dropbear.8).

在 VPS 为 Gateway 单独生成密钥, 私钥只允许服务账户读取, 不复用管理员登录密钥. OpenWrt 公钥必须携带 forced-command 和全部限制, 包含的脚本及白名单必须由 root 持有, Gateway 的 SSH 身份不能修改它们. 部署脚本位于 `src/wol/openwrt/`, 白名单由本地 CLI 从 Gateway 设备配置导出, 由操作者审查后安装.

forced-command 只把 `SSH_ORIGINAL_COMMAND` 当作待校验数据, 仅允许固定 `check <device-uuid> <mapping-digest>` 和 `wake <device-uuid> <mapping-digest>`. 摘要由服务端 UUID、规范化 MAC 和接口生成, 路由器重新核对, 防止两端白名单配置漂移. 手机不提供摘要、MAC、接口、脚本路径、重定向或额外 Shell 参数. 不能改为 `sh -c "$SSH_ORIGINAL_COMMAND"`、`eval` 或直接执行用户输入.

首次安装和每次 Dropbear 升级后验证:

1. 正常 `check` 返回预期协议结果, 不发送 WOL.
2. 未知 UUID、额外参数、Shell 分隔符、普通 `id` 命令及不带命令的 shell 请求都被拒绝.
3. PTY、端口转发、agent 转发和 X11 转发不能使用. 转发验证需实际尝试打开通道, 不能仅凭 `ssh -L` 进程成功启动判断失败或成功.
4. SCP/SFTP 请求不能绕过 forced-command.
5. 检查脚本、白名单、公钥和目录的所有者与权限. 若限制未生效, 不启用 Gateway 的真实唤醒权限.

通过可信本地控制台或已经验证的管理链路取得 OpenWrt SSH 主机公钥/指纹, 核对后写入 Gateway 专用 `known_hosts`. `ssh-keyscan` 只能收集候选公钥, 不能替代身份验证. 更换主机密钥时先查明原因, 不关闭 `StrictHostKeyChecking`, 不盲目删除旧记录.

## 5. 构建和安装 Gateway

在仓库中运行:

```powershell
./tools/Test-Wol.ps1
./tools/Publish-Wol.ps1
```

发布目录默认 `artifacts/wol/linux-x64/`. 这是 Linux x64 framework-dependent 包, VPS 安装最新维护版 ASP.NET Core Runtime 10.0, 以及 OpenSSH client. 不需要在 VPS 安装 SDK, 也不需要 Docker 守护进程. ARM64 主机需要对应 RID 重新发布, 不能直接使用 x64 包.

由操作者复制产物到独立版本目录, 创建专用系统账户, 使用包内配置示例、systemd unit 和 HTTPS 反代示例. 配置中的实际地址、MAC、设备名、私钥路径和数据目录均放在仓库外. 私钥/配置/数据库不进入镜像、Git、应用日志或诊断包.

以下是首次安装示例, 在 VPS 上由操作者执行. `/path/to/package` 替换为已上传的发布目录, 不在含旧版本的目录直接覆盖升级:

```sh
dotnet --list-runtimes
sudo apt-get install openssh-client
sudo useradd --system --home-dir /var/lib/fy1switch-wol --shell /usr/sbin/nologin fy1switch-wol
sudo install -d -o root -g root -m 0755 /opt/fy1switch-wol
sudo cp -a /path/to/package/. /opt/fy1switch-wol/
sudo chown -R root:root /opt/fy1switch-wol
sudo install -d -o root -g fy1switch-wol -m 0750 /etc/fy1switch-wol /etc/fy1switch-wol/ssh
sudo install -d -o fy1switch-wol -g fy1switch-wol -m 0700 /var/lib/fy1switch-wol
sudo install -o root -g fy1switch-wol -m 0640 /opt/fy1switch-wol/wol.config.example.json /etc/fy1switch-wol/gateway.json
sudoedit /etc/fy1switch-wol/gateway.json
sudo ssh-keygen -t ed25519 -N '' -f /etc/fy1switch-wol/ssh/id_ed25519
sudo chown fy1switch-wol:fy1switch-wol /etc/fy1switch-wol/ssh/id_ed25519
sudo chmod 0600 /etc/fy1switch-wol/ssh/id_ed25519
```

已有同名账户时先核对用途和权限, 不重复创建或擅自修改其身份. 从可信渠道准备主机公钥文件后, 使用 `sudo install -o root -g fy1switch-wol -m 0640 <verified-known-hosts> /etc/fy1switch-wol/ssh/known_hosts`. 不将未知主机扫描结果直接视为可信.

配置必须填写实际 `publicUrl`, `dataDirectory`, `ssh.host`, `ssh.user`, `ssh.identityFile`, `ssh.knownHostsFile`, `ssh.bindAddress`, `ssh.wireGuardInterface`, 以及每个设备的 `deviceId/name/macAddress/interface`. 示例中的 MAC 和 UUID 是占位值. 当前一个 Gateway 配置连接一个 OpenWrt, 支持该路由器下多台电脑. 设备配置启动时加载, 修改后需重启服务并重新导出/安装白名单.

完成公钥、主机密钥和白名单安装后再启动:

```sh
sudo install -o root -g root -m 0644 /opt/fy1switch-wol/deploy/fy1switch-wol.service /etc/systemd/system/fy1switch-wol.service
sudo systemctl daemon-reload
sudo systemctl enable --now fy1switch-wol.service
sudo systemctl status fy1switch-wol.service
sudo journalctl -u fy1switch-wol.service -n 50 --no-pager
curl --fail http://127.0.0.1:5090/healthz
```

健康检查确认 Gateway 进程和授权数据库可读, 不检查 Windows 电源状态. 部署包的 `deploy/Caddyfile.example` 将 `wol.example.com` 替换为自己的域名后, 合并到已有 Caddy 配置并先验证配置再重载. 使用其他反代时提供等效 HTTPS、请求大小和响应时间限制. 不覆盖其他服务的反代配置.

推荐权限和边界:

| 项目 | 访问策略 |
| --- | --- |
| 程序及服务定义 | root 管理, 服务用户只读 |
| 私钥 | 服务用户读取, mode 0600 |
| known_hosts / 配置 | 管理员维护, 仅服务账户可读, 不允许公网下载 |
| SQLite 数据目录 | 专用服务用户拥有, mode 0700, 不共享给其他应用 |
| OpenWrt 脚本及白名单 | root 管理, 不允许 Gateway SSH 身份改写 |
| 后端 HTTP | 仅 loopback, 不开放安全组入站 |

systemd 应设置开机启动、故障自动重启和适当文件系统保护. 使用服务提供的配置参数启动, 不把凭据放在命令行或环境日志. 检查 `systemctl status` 和 journal 日志确认启动成功, 日志不应包含注册码、访问凭据、Authorization 或完整配置.

HTTPS 反向代理必须保持 Authorization, 限制请求大小和请求速率, 为 Gateway 的有界执行时间留出响应预算. 不记录请求体、认证头或注册码. 后端不信任任意客户端提供的转发头, 代理来源限流和认证后手机限流分别生效.

华为云安全组仅为公网 HTTPS 放行 TCP 443; 使用 HTTP-01 签发/续期或 HTTP 跳转时按所选反代配置开放 TCP 80. 保留现有 WireGuard UDP 端口规则, 不重新选端口或覆盖规则. VPS 管理 SSH 仅开放给既有可信管理来源. 不开放 Gateway loopback 端口、OpenWrt SSH 或 WOL UDP 到公网.

## 6. 注册、授权与 Android 配置

先在 Gateway 配置中登记电脑 UUID、名称、MAC、接口和 SSH 目标, 并安装一致的 OpenWrt 白名单. 每台电脑有自己的 UUID/MAC, 配置 ID 不能重复.

本地管理命令使用同一个发布程序, 以有权限访问配置和数据目录的服务账户运行:

```text
Fy1Switch.WolGateway admin /absolute/private/config.json code <device-uuid> [more-device-uuids...]
Fy1Switch.WolGateway admin /absolute/private/config.json clients
Fy1Switch.WolGateway admin /absolute/private/config.json revoke <client-uuid>
Fy1Switch.WolGateway admin /absolute/private/config.json export-openwrt <output-path>
```

framework-dependent 安装也可使用以下完整命令. `PC_UUID` 替换为已登记电脑的 UUID; 导出路径必须尚不存在:

```sh
sudo -u fy1switch-wol /usr/bin/dotnet /opt/fy1switch-wol/Fy1Switch.WolGateway.dll admin /etc/fy1switch-wol/gateway.json export-openwrt /var/lib/fy1switch-wol/devices.export
sudo -u fy1switch-wol /usr/bin/dotnet /opt/fy1switch-wol/Fy1Switch.WolGateway.dll admin /etc/fy1switch-wol/gateway.json code PC_UUID
```

通过既有可信管理链路把 `devices.export`、包内 `openwrt/fy1switch-wol` 和 VPS 专用公钥交给 OpenWrt. 由 OpenWrt 管理员安装 `/usr/libexec/fy1switch-wol` 为 root:root 0755, `/etc/fy1switch-wol` 为 root:root 0700, `/etc/fy1switch-wol/devices` 为 root:root 0600. 参照 `openwrt/authorized_keys.example` 将完整受限公钥行追加至该用户实际 authorized_keys, 不覆盖已有管理员密钥. 执行前述限制验证后才能启用真实 wake.

默认 Gateway 注册码有效 5 分钟, 注册尝试同时受次数和速率限制. 默认每设备发送冷却 30 秒, 全局和每手机分别受配置额度限制. 成功/未知操作保留 24 小时供查询, 请求有效期最多 120 秒. 这些窗口限制不是永久去重承诺.

公开 API 均位于 `/v1/wol`, 除注册兑换和只读健康检查外需要 WOL Bearer 凭据:

| 接口 | 用途 |
| --- | --- |
| `POST /v1/wol/register` | 用 `version/registrationCode/clientName` 兑换独立手机凭据和授权设备列表 |
| `GET /v1/wol/devices` | 查询该手机仍获授权的设备 |
| `POST /v1/wol/devices/{id}/check` | 带请求 ID 和有效期验证受限脚本/映射, 不发送 WOL |
| `POST /v1/wol/devices/{id}/wake` | 带设备、请求、操作 ID 和有效期提交唤醒 |
| `GET /v1/wol/devices/{id}/operations/{operationId}` | 查询原操作, 不重新执行 SSH |
| `DELETE /v1/wol/clients/me` | 撤销当前手机在此 Gateway 的授权 |

请求/响应版本为 1. wake body 包含 `version/deviceId/requestId/operationId/issuedAt/expiresAt`, check 不带 operationId. 过期、超出时限、路由与 body UUID 不符或未知字段会拒绝. 操作响应包含 `version/deviceId/operationId/status/errorCode/updatedAt`;错误响应包含固定 `errorCode`, 不返回原始 SSH stderr、密钥路径或凭据.

注册码通过可信渠道交给对应手机, 短时有效、一次性使用并受尝试次数限制. 不放 URL、聊天公开频道、日志或 Git. 注册成功后每手机取得独立高熵访问凭据, 服务端仅保存哈希, 无法从管理界面取回原凭据. 注册响应丢失时不要无限重试, 列出授权记录并撤销不再使用的记录后生成新码.

Android 已保存电脑的设置中配置 WOL Gateway HTTPS 根地址, 输入注册码完成授权, 选择已授权目标并启用 WOL. 绑定目标应对应当前 Windows 电脑的 UUID. 同一 Gateway 的手机凭据由加密存储统一保存, 不复制 Windows 设备 token.

使用连接测试确认 Gateway 认证及 OpenWrt 受限脚本可达. 连接测试不唤醒电脑, 也不证明 BIOS/WOL 硬件设置有效. 更换 Gateway 地址需要新的授权, 不将原凭据发往新地址.

关闭 WOL 只停用该功能. 本地清除只移除手机记录, 不等于服务端撤销. 撤销本手机授权会使其在该 Gateway 的所有关联目标失去唤醒权限, 但不撤销原 Windows 显示器控制配对. 丢失手机时由 VPS 本地 CLI 按 client UUID 撤销.

## 7. 应用内唤醒和独立磁贴

主页使用独立远程开机按钮. 手机不需要家庭 VPN. 未配置 WOL 时原显示器控制保持原行为. 原 "远程显示器" 磁贴不会因为离线而自动发送 WOL.

用户自行在 Android 快捷设置中添加 "远程开机" 磁贴. 默认目标是 App 当前选择的电脑, 设置中可固定另一台已配置电脑. 固定目标删除后显示未配置, 不改为唤醒其他电脑. 锁屏操作先解锁并重新核对目标/配置, 多次点击由手机及服务器共同限制.

App 在发请求前保存操作 ID. 网络切换、超时或进程回收后查询原操作, 不无限重发魔术包. 发送后只在有限可见生命周期内检查原可信 Host 通道, 有总时限和停止条件; 不要求手机常驻后台. 离开页面后可以稍后回来刷新原控制状态.

| 显示结果 | 实际含义 |
| --- | --- |
| 请求进行中 / pending | 已接纳或等待执行, 尚未确认发送 |
| packet_sent | OpenWrt 受限脚本报告发送成功, 电脑启动仍未确认 |
| failed | 已知失败, 查看固定错误码 |
| unknown | 执行结果不确定, 包可能已发送, 不自动重发 |
| 等待 Host 超时 | 有限观察期内未收到可信 Host 回复, 不证明电脑没启动 |
| Host 已上线 | 原认证通道收到匹配身份的状态, 不代表显示器已开启 |

## 8. 排查

| 故障 | 检查方向 |
| --- | --- |
| Gateway 连接/TLS 失败 | DNS、公网443、证书链、域名、反代和手机网络, 不关闭证书验证 |
| 未授权/已撤销 | 手机独立授权和服务器撤销记录, 不替换为 Windows token |
| 设备未授权 | 注册码允许的设备集合与当前绑定 UUID |
| 限流/冷却 | 等待提示期限, 不连续生成新操作 ID 绕过限制 |
| WG 接口不可用 | VPS 实际接口名、源地址和已有隧道配置 |
| OpenWrt SSH 不可达 | 隧道路由、peer、目标监听及防火墙, 不等同已证明 WG 断开 |
| SSH 认证失败 | 公钥、forced-command 选项、用户、文件权限及 Dropbear 实际能力 |
| 主机密钥不匹配 | 通过可信管理链路核对是否更换设备/密钥, 不接受未知 key |
| 脚本拒绝目标 | Gateway 与路由器白名单、UUID、接口、MAC 是否一致 |
| 已发送但未上线 | 网卡待机供电、BIOS、快速启动、网线、MAC、Windows 登录及 Host 自启动 |
| Host 在线但显示器未知 | 按原显示器拓扑/DDC 诊断, 不把 WOL 重试当成修复 |

SSH 超时、连接中断或进程崩溃可能发生在远端已经发送之后. 这类结果必须保留不确定性. 服务端持久去重覆盖短期重试, 不是跨任意时间、数据库回滚或所有故障的永久 exactly-once 保证. 操作者恢复旧备份后应等待原请求有效期结束再开放写入, 不重放旧请求.

## 9. 升级、备份与卸载

升级前保留当前程序版本、配置、服务定义和 SSH 信任材料. 停止单实例后备份 SQLite 整套数据及配置, 保持目录私有; 不在运行中只复制主数据库而遗漏 WAL. 备份包含敏感设备信息和授权哈希, 应加密保管. 私钥按独立密钥备份策略处理.

把新版本部署到新目录, 停止旧服务后切换程序路径并启动. 不运行多副本、重叠滚动实例或在两台 Gateway 上复用同一数据副本. 验证健康检查、授权设备列表、无副作用连接测试和原操作查询, 再由操作者执行硬件验收. 数据格式升级后回滚需同时考虑备份格式, 不仅替换二进制.

卸载时停止并禁用 Gateway 服务, 移除其独立反代配置, 撤销 OpenWrt 上该 Gateway 专用公钥. 保留或按用户选择销毁私有配置、数据库和私钥. 不删除既有 WireGuard 接口、peer、其他 SSH 公钥或共用反代服务. Android 清除 WOL 配置, 保留原 Windows 配对和显示器控制功能.

## 验证边界

自动化使用模拟 SSH、模拟网卡和临时数据库, 不向真实电脑发送 WOL. 覆盖协议、授权、撤销、限流、重放、幂等、失败及兼容性; Android JVM 测试不等于实际系统磁贴/锁屏/进程回收验收.

Linux 下可运行 `python3 tools/Test-Wol-OpenWrt.py`. 它使用 `unshare -Urn` 和临时 chroot 执行原始受限脚本, 模拟 `etherwake` 和接口, 不需要 root, 不接触真实网络. 需要支持用户命名空间的 Linux、`chroot`、`ldd`、`grep`、`stat` 和 `sha256sum`. `shellcheck -s sh src/wol/openwrt/fy1switch-wol` 检查脚本语法/常见风险.

`tools/Test-Wol-Linux.py <self-contained-gateway-executable>` 可验证非 root Linux 启动、健康检查、注册、认证、操作查询/去重和撤销. 它仅监听临时 loopback 端口, 配置不存在的 SSH 可执行文件与隧道, 不可能发送真实 WOL. 该验证工具接受单独自包含测试构建; 正式部署包默认依赖 VPS 已安装的 ASP.NET Core Runtime 10.0.

真实 VPS/OpenWrt Dropbear 权限、WireGuard 中断恢复、Realtek 网卡关机/睡眠唤醒、移动数据、厂商 Android 磁贴、用户登录后 Host 恢复等需在部署后逐项验收. 构建成功、HTTP 200 和模拟测试通过不能替代这些结果. 本仓库工具不会自动部署生产服务器或改变防火墙.
