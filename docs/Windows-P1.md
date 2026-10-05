# Windows P1 使用与验证

## 当前交付

P1 是独立 Windows 本地控制版本，不是完整远程产品。包含 WPF 托盘宿主、唯一硬件工作进程、本地 CLI、SQLite 绑定/操作日志、每用户自启设置，以及自包含 x64 目录与安装/卸载脚本。

Android、Quick Settings Tile、HTTPS/Tailscale 应用入口和 Relay 尚未实现。没有监听网络端口，不应把本地命名管道转发为公网接口。

## 启动

运行 artifacts/windows/win-x64/RemoteDisplaySwitch.Host.exe。设置窗口关闭后保留托盘；托盘“退出”结束宿主和其控制进程。再次启动同一用户会话的宿主会请求已有实例显示设置。

程序不会在启动时自动扫描或开关屏幕。点击“发现显示器”，明确选择候选并绑定，再点击“刷新”。支持开启、关闭与切换；Toggle 在宿主串行队列中先读当前状态，未知时拒绝翻转。绑定是用户确认当前受限配置的记录，不是新发现的通用物理身份 API。

验证范围仍限定 SAC2563 G4Q + MTT1337 VDD 共享显示源的配置。沿用已实测的 GDI 顺序适配及上游 DdcCiController，要求完整唯一的候选。每次读写检查拓扑指纹；Windows 显示变化、恢复通知或指纹不一致使绑定需要重新确认。重新扫描会结束旧 Worker，再启动新的实例；重新绑定分配新的 targetId，旧 targetId 不能用于控制。

官方 Power Display、P0 Probe 或另一个 Worker 运行时拒绝硬件访问，不会停止它们。用户此前确认的 5 秒实体开关、30 轮 P0 及长关闭结果不等于系统级全部验收。整机重启、睡眠恢复、VDD 重建和物理拔插按用户确认后置到正式发布前。

## 状态与失败语义

- On/Off 是最近一次读回记录，带来源和时间；启动或重启后不恢复成虚假的当前 On。
- 写入接受与读回确认分开；读回失败显示“已接受、未确认”，客户端超时/断线且可能已写入则为 Unknown。
- OFF 发送 0x04，ON 发送 0x01，不自动回退 Hard Off 0x05。
- 实机首次联调发现写后立即读取可失败；现等待 500ms，读取失败最多再读两次，每次间隔 200ms。只重试读取，不重复开关写入。
- 命令有 operationId，重复的同一操作返回持久化结果，不重复执行；不同目标或命令复用 ID 被拒绝。
- Pending 操作跨宿主重启转为 Unknown，不自动重放。SQLite 中保存当前绑定和操作记录，不存原始物理句柄。
- Worker 原生调用无法被可靠取消；读写等待上限 10 秒、发现 90 秒，超时隔离该工作进程。下一次请求可以显式重新启动，不进行无限自动恢复。
- discovery.lock 遗留时停止发现；重启并不会自动解除该隔离，应先检查本程序诊断目录。不要删除 PowerToys 自己的文件。

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

UI 使用真实 WPF 窗口内容离屏渲染检查。实际托盘点击、高 DPI、窄窗口人工交互、登录自启和系统级恢复仍需要用户验收。不会据此声称 Android 或远程网络已完成。

## 数据与诊断

默认数据目录 LocalAppData/RemoteDisplaySwitch/Host。state.db 是未加密的本地绑定与操作记录，没有远程凭据；后续网络凭据必须独立用安全存储，不能直接放入此表。控制进程诊断分卷到 worker/worker.log，限制大小并保留一份 previous，不打印原始设备路径或令牌。导出或分享前仍应检查显示型号、时间和本机信息。
