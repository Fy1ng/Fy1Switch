# Windows P1 使用与验证

## 当前交付

P1 是独立 Windows 本地控制版本，不是完整远程产品。包含 WPF 托盘宿主、唯一硬件工作进程、本地 CLI、SQLite 绑定/操作日志、每用户自启设置，以及自包含 x64 目录与安装/卸载脚本。

后续实现已加入 Android、Quick Settings Tile、HTTPS/Tailscale 应用入口和 Relay，详见 Remote-Control.md。本页保留 Windows 本地功能说明。远程 HTTPS 默认关闭，只有用户在远程设置启用后才监听；不应把本地命名管道转发为公网接口。

## 启动

运行 artifacts/windows/win-x64/RemoteDisplaySwitch.Host.exe。设置窗口关闭后保留托盘；托盘“退出”结束宿主和其控制进程。再次启动同一用户会话的宿主会请求已有实例显示设置。

主窗口和手机连接窗口使用自绘标题栏, 隐藏 Windows 默认标题栏; 保留拖动、缩放、最大化、最小化及原有关闭行为. 从托盘重新打开时恢复最小化的窗口. 主窗口突出当前状态和开关操作, 状态未知时提供刷新和明确开启/关闭, 绑定入口直接显示在主界面. 手机连接窗口以配对为主, 网络参数集中在连接设置中; 选择网络后可点击使用此地址并连接, 无需先填入地址再单独保存.

首次打开且未绑定时只读发现一次, 不自动绑定或开关屏幕. 明确选择候选并绑定后, 按读回状态显示开启或关闭操作. Toggle 在宿主串行队列中先读当前状态, 未知时拒绝翻转. 绑定是用户确认当前受限配置的记录, 不是新发现的通用物理身份 API.

验证范围仍限定 SAC2563 G4Q + MTT1337 VDD 共享显示源的配置。沿用已实测的 GDI 顺序适配及上游 DdcCiController，要求完整唯一的候选。每次读写检查拓扑指纹；指纹不一致或目标不再唯一时仍需要重新确认。Windows 显示设置变化或睡眠恢复通知本身不再直接使绑定失效：宿主在控制命令的串行队列中结束旧 Worker，通过新 Worker 只读发现，核对完整指纹、唯一目标和配置类型；全部一致则保留原绑定及 targetId，不发送电源写入。连续通知合并为最新一次待处理校验，不采用已被后续通知取代的发现结果。校验后电源状态为 Unknown，必须重新读回，不能沿用变化前的状态。

真正失效或此前已标记需要确认的绑定不会自动恢复。重新扫描会结束旧 Worker，再启动新的实例；用户重新绑定分配新的 targetId，旧 targetId 不能用于控制。该优化不忽略 VDD 重建、目标替换、GDI 顺序或完整拓扑指纹的变化，也不替代睡眠恢复和物理拔插的实机验收。

官方 Power Display、P0 Probe 或另一个 Worker 运行时拒绝硬件访问，不会停止它们。用户此前确认的 5 秒实体开关、30 轮 P0 及长关闭结果不等于系统级全部验收。整机重启、睡眠恢复、VDD 重建和物理拔插按用户确认后置到正式发布前。

## 状态与失败语义

- On/Off 是最近一次读回记录，带来源和时间；启动或重启后不恢复成虚假的当前 On。
- 写入接受与读回确认分开；读回失败显示“已接受、未确认”，客户端超时/断线且可能已写入则为 Unknown。
- OFF 发送 0x04，ON 发送 0x01，不自动回退 Hard Off 0x05。
- 实机首次联调发现写后立即读取可失败；现等待 500ms，读取失败最多再读两次，每次间隔 200ms。只重试读取，不重复开关写入。
- 命令有 operationId，重复的同一操作返回持久化结果，不重复执行；不同目标或命令复用 ID 被拒绝。
- Pending 操作跨宿主重启转为 Unknown，不自动重放。SQLite 中保存当前绑定和操作记录，不存原始物理句柄。
- Worker 原生调用无法被可靠取消；读写等待上限 10 秒、发现 90 秒，超时隔离该工作进程。下一次请求可以显式重新启动，不进行无限自动恢复。
- Worker 首次原生发现前, 在取得 `worker.instance.lock` 独占租约并通过进程冲突检查后, 尝试将遗留 `discovery.lock` 移至同目录唯一命名的 `discovery.lock.<GUID>.previous` 保留现场. 留档失败仅记录 `discovery_archive_failed`, 保留原文件并继续发现. 上游仍管理本次发现锁; 冲突拦截, 拓扑确认和同一 Worker 不自动重试发现的约束不变. 临时拓扑读取失败返回 `topology_unavailable`, 不持久化重新确认要求, 恢复后重新核验完整指纹; 真实变化仍使绑定失效. 不要修改 PowerToys 自己的文件.

## 本地 CLI 与 IPC

cli/RemoteDisplaySwitch.Cli.exe 与 Host 必须来自同一完整目录。CLI 检查命名管道服务端进程完整路径；Host/Worker 使用随机私有管道、当前用户限制和双方 PID 核验。此边界信任同一登录用户，不是抵御已经控制该用户账户的恶意软件的沙箱。

~~~powershell
./cli/RemoteDisplaySwitch.Cli.exe status
./cli/RemoteDisplaySwitch.Cli.exe discover
./cli/RemoteDisplaySwitch.Cli.exe bind <targetKey> <topologyFingerprint> "桌面显示器"
./cli/RemoteDisplaySwitch.Cli.exe refresh
./cli/RemoteDisplaySwitch.Cli.exe off <targetId> <新operationId>
./cli/RemoteDisplaySwitch.Cli.exe on <targetId> <新operationId>
./cli/RemoteDisplaySwitch.Cli.exe operation <operationId>
./cli/RemoteDisplaySwitch.Cli.exe startup on
./cli/RemoteDisplaySwitch.Cli.exe shutdown
~~~

自启仅写当前用户 Run 项，默认不开启；不会修改系统服务或要求管理员权限。软件实现和模拟开关已验证，实际注销再登录触发尚未验收。

切换便携版或升级目录后，旧启动项可能仍指向旧版。2026-10-19 修复版在开启自启时显示旧、新启动路径并询问是否替换；取消保留原设置，关闭当前版本的开关不会删除另一份安装的启动项。确认期间启动项已变化时停止替换，并提示重新确认。权限或 EXE 启动方式错误会显示具体原因。回归使用唯一隔离注册表测试键及模拟 UI，不修改真实 Run 项；实际登录触发仍需验收。独立修复包位于 `artifacts/windows-startup-fix-20261019/win-x64`。

## 安装与卸载

在完整发布目录执行 Install.ps1，默认复制到当前用户 LocalAppData/Programs/RemoteDisplaySwitch，并建立开始菜单快捷方式。不会自动启动程序或打开登录自启。更新前先从托盘退出；安装器不覆盖无标记目录或经过重解析点的路径。

在安装目录运行 Uninstall.ps1，检查安装标记、完整绝对路径和文件归属后删除程序文件及其自有快捷方式/自启项。保留 LocalAppData/RemoteDisplaySwitch/Host 中的用户数据。当前是可审查的 PowerShell 安装流程，不是签名 MSI/商店包；SmartScreen、代码签名和干净系统首次安装仍未验收。

## 构建及测试

~~~powershell
./tools/Test-Windows.ps1
./tools/Publish-Windows.ps1
./tools/Test-Windows-Integration.ps1
~~~

默认集成测试使用模拟 Worker、隔离数据目录，不访问硬件和真实自启注册表。-Hardware 显式运行本机受限配置，OFF 后在 finally 尝试 ON；测试失败不会自动补发原 OFF。

2026-10-05：本地模拟和真实完整发布包均完成发现、绑定、OFF、重复操作去重、宿主退出/重启、绑定与日志恢复、全新 Worker ON 读回的闭环。真实测试的最后状态为 On。这是软件链路/硬件读回证据，不是本轮肉眼观察确认。

最终无硬件回归 248/248 通过（上游 87、P0 55、Worker 46、Runtime 60）；新增依赖的 NuGet 漏洞审计未报告已知漏洞，不能替代完整安全审计。安装、覆盖更新、安装后模拟运行、卸载与数据保留已在工作区隔离目录验证，没有修改真实自启项或开始菜单快捷方式。真实用户目录安装、快捷方式人工点击和登录触发仍未验收。

UI 使用真实 WPF 窗口内容离屏渲染检查。用户随后反馈本地测试通过；实际登录触发、系统级恢复和跨设备网络效果仍按各自验收范围记录，不能从这页的本地测试推导远程端通过。

## 数据与诊断

默认数据目录 LocalAppData/RemoteDisplaySwitch/Host。state.db 是未加密的本地绑定与操作记录，没有远程凭据；后续网络凭据必须独立用安全存储，不能直接放入此表。控制进程诊断分卷到 worker/worker.log，限制大小并保留一份 previous，不打印原始设备路径或令牌。导出或分享前仍应检查显示型号、时间和本机信息。
