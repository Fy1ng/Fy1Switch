# RemoteDisplaySwitch

基于 Microsoft PowerToys Power Display 源码的独立 Windows 显示器远程控制项目。

## 当前状态：Windows HTTPS、Android / Tile 与自建 Relay 已实现

已实现 WPF 托盘宿主、独立 Worker、本地 CLI、SQLite 绑定与操作日志、自启、HTTPS 直连与一次性配对、Android Compose App / Quick Settings Tile，以及 ASP.NET Core 自建 Relay。**实现与本地自动化验证不等于实机或公网验收**：OriginOS Tile、真实 LAN/Tailscale、实际自建公网 Relay，以及后置系统级恢复仍需验证。详见 docs/Remote-Control.md、docs/Android.md、docs/Relay.md。

官方 Power Display 退出后，已完成真实 DDC 发现及受限对照实验：原始上游按 CCD 顺序绑定时把唯一响应句柄标为 VDD；按本机 GDI 顺序显式重排后标为 G4Q。用户已于 2026-10-05 人工确认复测时实体屏熄灭约 5 秒后恢复，VDD、复制模式正常。随后 30/30 轮软件循环、保持句柄关闭 60 秒，以及释放原句柄后等待 15/60 秒由全新进程恢复均成功；当前发布包也通过 5 秒全新进程恢复复核。**人工确认只覆盖指定短循环，整机重启、睡眠和物理拔插尚未验证，不把工作进程重启当成整机重启。**详见 docs/P0-findings.md。

## 已确认架构

验证安排更新（2026-10-05）：用户先暂缓物理拔插，随后明确同意将剩余整机重启、睡眠恢复和 VDD 重建验收后置到正式发布前，先推进已确认架构中的 Windows 后台与托盘。后置不等于验收通过；身份或拓扑不确定时拒绝控制。

- 新独立项目，不使用 RemoteScreenSwitch 实验代码。
- Windows：.NET 10 用户会话内 Host/Worker，WPF 托盘设置界面；不在 Session 0 控制显示器。
- 控制核心：锁定 PowerToys 源码子集，保留原始发现、配对、句柄及读写行为，独立适配日志、路径与产品状态。
- Android：Kotlin/Compose 与原生 TileService；短时交互刷新，不以永驻进程保证状态。
- 网络：HTTPS LAN/Tailscale 直连及可信自建 ASP.NET Core Relay；两段 TLS 不宣称端到端加密。
- 当前受限环境下的 P0 开关、长关闭与工作进程恢复已验证；Windows P1、Android、HTTPS 与 Relay 实现已落地，系统级及跨设备验收按批准顺序后置。

## Windows 本地版本

~~~powershell
./tools/Test-Windows.ps1
./tools/Publish-Windows.ps1
./tools/Test-Windows-Integration.ps1
~~~

输出 artifacts/windows/win-x64。运行其中 RemoteDisplaySwitch.Host.exe，点击发现、选择、绑定，再刷新/控制；关闭设置窗口后保留托盘。Install.ps1 提供每用户安装，Uninstall.ps1 只删除已标记的程序目录并保留用户数据。自启默认关闭，可在界面选择；没有为开发测试启用用户的真实登录自启。默认集成测试为模拟，不访问硬件；显式 -Hardware 才会触发真实 5 秒开关及宿主重启恢复。

宿主本地控制使用当前用户命名管道；远程 HTTPS 默认关闭，仅在“连接手机”中显式启用后监听。不要直接暴露本地 IPC。P0 诊断工具与新 Worker 互相避让，不能同时操作硬件。

Worker 首次原生发现前, 在取得 `worker.instance.lock` 独占租约并通过进程冲突检查后, 尝试将遗留 `discovery.lock` 移至同目录唯一命名的 `discovery.lock.<GUID>.previous` 保留现场. 留档失败只记录诊断码, 保留原文件并继续发现. 上游仍管理本次发现锁; 冲突拦截, 拓扑确认和同一 Worker 不自动重试发现的约束不变. 临时拓扑读取失败返回 `topology_unavailable`, 不返回可信观察且不使绑定永久失效; 写前失败不写入, 写后失败保留实际接受结果且不重放. 恢复后重新核验完整指纹, 真实变化仍要求重新确认绑定.

## 手机与中继版本

2026-10-18 UI 更新：统一状态与配对术语，短码统一为电脑显示、手机明文输入，六位自动提交一次。Windows 增加动态托盘状态、短时成功反馈、系统深浅色及高对比度适配，连接窗口合并状态提示、隐藏备用配对 JSON，并在自然过期时续期。只有一个局域网地址时首次启用可直接连接；多个地址仍需选择。Android 大电源图标可操作、支持下拉刷新与触感反馈，短时提示使用 Snackbar；未知状态的快捷开关点按只刷新。术语与边界见 [docs/UI-Terminology.md](docs/UI-Terminology.md)。以下日期条目保留各次历史产物与验证范围。

本轮独立测试包位于 `artifacts/windows-ui-20261018/win-x64`，Android debug 包位于 `artifacts/android/RemoteDisplaySwitch-ui-20261018-debug.apk`。Windows 266 项自动化测试、Android 126 项单测及 lint/build 通过；WPF 隔离行为检查和自包含包模拟启动通过。未安装手机应用、未覆盖运行中的旧版、未操作实体显示器。Windows 11 原生布局弹层、系统高对比度切换及 Android 真机交互仍需设备验收。

2026-10-09 显示器绑定修复：Windows 显示设置或睡眠恢复通知先通过新 Worker 只读核验；完整拓扑指纹、唯一目标和配置类型不变时保留原绑定及 targetId，避免复制模式下仅因系统通知反复要求重新发现。真实拓扑变化和已失效绑定仍需人工确认，不自动重放开关操作。新版输出为 `artifacts/windows-topology-fix/win-x64`，不覆盖运行中的旧版；已有失效标记需要手动发现并确认一次，后续使用新的通知处理逻辑。

2026-10-07 多路径增量：Android 按局域网 → Tailscale → Relay 自动选择连接，保留既有配对与操作去重；Windows 可同时绑定 LAN 和可选 Tailscale 地址。新版输出为 `artifacts/windows-multipath/win-x64` 与 `artifacts/android/RemoteDisplaySwitch-multipath-debug.apk`，不覆盖正在运行的旧版。配置与跨网络验收见 `docs/Remote-Control.md` 的自动多路径章节。

2026-10-06 UI 增量：两端主页简化为可信状态对应的单一主操作，Windows 首次未绑定时只读发现并要求明确确认，设置/诊断折叠；电脑可显示现有安全配对信息二维码，手机按需申请相机权限扫码，并保留粘贴备用。已有凭据、包名与 Tile 组件保持不变。此增量 Windows 在 artifacts/windows-ui/win-x64，Android 在 artifacts/android/RemoteDisplaySwitch-ui-debug.apk。

2026-10-06 用户确认六位短码方案后，已实现原生局域网双向发现和独立 SRP-6a 配对通道。两端打开配对页，自动搜索附近设备；点击对方并输入对方屏幕六位码，或继续扫码。短码五分钟有效、成功后消费、每码最多五次尝试，且进程级累计限流不因换码清零。现有二维码仍使用原高熵一次性凭据，HTTPS证书及已保存手机授权不迁移。地址候选由本机网卡生成，用户明确选择才保存，不静默覆盖旧配置。详见 docs/Pairing-v1.md。

最新短码版 Windows 在 artifacts/windows-pairing/win-x64，Android 在 artifacts/android/RemoteDisplaySwitch-pairing-debug.apk。Windows 原生发布/浏览/撤销已在本机运行；C# 与 Android JVM 正向、反向 SRP→加密凭据→固定证书 HTTPS 配对都已联调通过。真实 Android NSD、OriginOS 配对页生命周期及不同路由器的组播仍需手机实测；本轮未安装 APK、未改变防火墙、未控制实体显示器。

~~~powershell
./tools/Publish-Windows.ps1 -OutputDirectory ./artifacts/windows-remote/win-x64
./tools/Build-Android.ps1 -Offline
./tools/Test-Network.ps1
./tools/Test-Android-Live.ps1
./tools/Publish-Relay.ps1
~~~

本轮 Windows 新版位于 artifacts/windows-remote/win-x64，不要求关闭正在使用的旧版来构建。使用前请自己从旧版托盘退出，再打开新版 Host。发布脚本现在会检查目标目录是否有程序在运行，避免覆盖已加载的文件。APK 位于 artifacts/android/RemoteDisplaySwitch-debug.apk（debug 签名，尚未安装或上传商店）；Relay Linux x64 自包含产物位于 artifacts/relay/linux-x64。Docker/Compose 文件位于 src/relay，本机没有 Docker，未构建/运行容器或部署公网服务器。

Windows 中先绑定显示器，再启用 HTTPS、填写手机可访问的 LAN/Tailscale 地址并生成一次性配对 JSON；在手机粘贴配对。每台手机独立凭据、可撤销、固定服务器 SPKI pin。Relay 是可信的两段 TLS 中继，不是服务器无法读取内容的端到端加密。手机首次配对建议直连，之后再配置 Relay 地址与控制端凭据。

验证：原 Windows 回归 248 项；远程 HTTPS/配对 92 项；Relay 53 项；HTTPS/WSS 端到端 1 项；Android JVM 单测 54 项，另有实际 Android HttpsTransport 到 Windows HTTPS 模拟 Worker 的 live 集成 1 项。跨通道重复 operationId 不重复写入、撤销、过期与错误 pin 均有测试。Android lint 0 错误、6 警告（依赖更新及自定义 TrustManager 提示）。上述网络测试使用本机回环/模拟显示器，不冒充手机、Tailscale 或真实服务器验收。

## 构建与测试

需要 Windows 和 .NET SDK 10.0.400（同 feature band 的新补丁允许）。

~~~powershell
./tools/Test-P0.ps1
dotnet run --project ./src/windows/RemoteDisplaySwitch.Probe -- inventory
dotnet run --project ./src/windows/RemoteDisplaySwitch.Probe -- topology
dotnet run --project ./src/windows/RemoteDisplaySwitch.Probe -- discover
~~~

inventory 只读取显示拓扑；discover 会通过原始 Power Display 核心打开句柄、读取能力和初始值，不写电源。默认关闭最大兼容模式，不读取官方 PowerToys 配置或已知良好缓存。

topology 对照 CCD 路径顺序与 EnumDisplayDevices 的 GDI 设备顺序，只返回脱敏键，不打开 DDC 句柄。微软没有保证这两个数组与物理句柄数组同序，因此默认模式拒绝复制拓扑中的电源写入（退出码 12）。

测试不会枚举、探测或控制真实显示器。上游测试与新增的命令防误触、DPM→ON 恢复顺序、离散电源读回及日志脱敏测试分开维护。

## P0 实机验证

1. 保存工作，保持实体屏 + VDD + 复制模式；准备在失去 DDC 通道时手动开屏。
2. 用户自行退出或暂时禁用官方 Power Display 模块。无需卸载 PowerToys，也无需结束其他模块。
3. 运行 inventory 和 discover，核对返回的目标键、名称、能力及读回结果。**列表名称不证明物理句柄配对正确；复制模式仍使用上游按序配对。**
4. read 使用 discover 返回的完整 targetKey，读取当前 D6。不要把读失败、默认值或能力缓存当成 OFF。
5. 在实体屏旁进行一次有人工观察的 cycle，检查真正被关闭和唤醒的是实体屏，VDD 与复制模式未改变。
6. 确认单次结果后再做多轮、长时间关闭、重新发现、进程重启及睡眠/拔插恢复测试；本工具单次 cycle 不能替代这些验收。

~~~powershell
./RemoteDisplaySwitch.Probe.exe read <完整targetKey>
./RemoteDisplaySwitch.Probe.exe on <完整targetKey> --allow-power-write
./RemoteDisplaySwitch.Probe.exe cycle <完整targetKey> --allow-power-write
./RemoteDisplaySwitch.Probe.exe cycle <完整targetKey> --allow-power-write --off-seconds 60
./RemoteDisplaySwitch.Probe.exe restart-cycle <完整targetKey> --allow-power-write --off-seconds 60
~~~

当前 G4Q + MTT VDD 复制环境只能使用显式诊断实验，不会自动选择或交换索引：

~~~powershell
./RemoteDisplaySwitch.Probe.exe discover --gdi-order-experiment
./RemoteDisplaySwitch.Probe.exe cycle <本次实验返回的G4Q完整targetKey> --gdi-order-experiment --allow-power-write
~~~

该开关仅接受两个指定 EDID 型号 SAC2563、MTT1337、同一 GDI 源、完整且唯一的活动 GDI/CCD 设备集合。实验直接调用上游 DdcCiController，以 GDI 顺序传入目标；写前重新核对拓扑，并要求只有一个被发现的 DDC 候选。名称和身份仍标记为未验证；不会保存实体绑定，不支持任意 VDD/多实体显示器，也不能证明微软保证此映射。

cycle 仅在能力包含 0x01、0x04，且刚读到 0x01 时执行。它发送 0x04，成功后默认等待 5 秒（可用 --off-seconds 指定 1～300 秒），在 finally 中尝试发送 0x01；即使 OFF 报错也尝试 ON，不回退到 Hard Off 0x05。退出码为 0 只表示相应调用和最终 ON 读回成功，**不表示物理身份及肉眼开关已验证**。

restart-cycle 是明确的故障恢复实验：监督进程在收到成功 OFF 回执后结束自己的原工作进程，确认其退出并释放句柄，等待指定时间，再启动全新工作进程重新发现目标、发送 ON 并读回。不保留救援 DDC 句柄，不借用旧进程的内存状态；即使第一阶段异常也尝试一次全新进程 ON。该模式只能由监督进程运行，不能直接作为 --worker 命令调用。两个阶段之间持有监督锁，其他正常 P0 命令不能插入。

原生驱动卡死、监督进程被强制结束或系统故障时，finally 不保证执行。普通诊断工作进程预算为 90 秒，cycle 另计已指定的关闭等待时间；restart-cycle 的关闭和全新进程恢复阶段各有 90 秒预算，中间等待单独计时。超时会结束自己的工作进程并报告结果未知。Ctrl+C 请求不会立即终止正常恢复流程；除明确的 ON 恢复尝试外，不自动重试硬件任务，也不提供永久 OFF 或 Toggle 的产品接口。

重复测试可运行 tools/Run-P0-Matrix.ps1 -TargetKey <完整targetKey> -Cycles 30 -AllowPowerWrite。默认每轮关闭 5 秒，轮间至少保持开启 3 秒，每轮使用新工作进程重新发现；首个异常立即停止。日志在 diagnostics 下，physicallyObserved 始终为 false，不用自动化回执代替人工验收。

PowerToys 与本工具没有共同的硬件锁。检测到官方进程时拒绝访问，只是防误用措施，不是对所有第三方 DDC 程序的排他保证；测试期间不要重启其他控制者。

## 隔离与诊断

- 状态目录：%LOCALAPPDATA%/RemoteDisplaySwitch/Probe；不使用 Microsoft/PowerToys 的状态、事件或单实例名称。
- 标准输出为逐行 JSON；标准错误为脱敏上游日志。可在本地重定向保存，报告仍可能含显示型号和拓扑信息，不要无审查上传。
- targetKey 是规范化 DevicePath 的 SHA-256，仅供本地诊断选择，不是认证秘密、实体身份证明或跨端口永久 ID。
- 仅 P0 Probe 诊断工具保留旧策略: 遗留 `discovery.lock` 时拒绝自动探测, 应先排查再由操作者移走该工具的锁文件; 当前 Windows Worker 使用前述自动留档流程. 不要修改官方 PowerToys 文件.
- 电源适配直接复用上游 IVcpFeatureReader；离散 D6 成功读回不会因 max=0 被连续数值范围验证误判。默认写入走原始 MonitorManager → DdcCiController → SetVCPFeature；实验直接使用同一个上游 DdcCiController.SetPowerStateAsync，不另写 DDC 传输。

常用退出码：0=调用完成；2=参数无效；3=工作进程超时；4=已有探测运行；5=无目标；6=官方控制者运行；7=发现隔离/黑名单资源异常；8=目标缺失或不唯一；9=读回或写入未确认；10=异常；11=能力/初始状态/拓扑安全条件不满足；12=复制模式绑定未验证或实验条件变化。

## 发布

~~~powershell
./tools/Publish-P0.ps1
~~~

输出 artifacts/p0/win-x64，必须保留整个目录，不能只复制 EXE。使用自包含 .NET 和 WmiLight 原生依赖，不要求安装 PowerToys 或开发 SDK。干净系统运行及完整产品安装/升级尚待后续验证。P0 是便携验证包，不是正式安装器。

## 上游维护与许可

vendor/powertoys/upstream.json 固定 Microsoft PowerToys 提交及每个原始文件的 SHA-256。tools/Verify-Upstream.ps1 检查原始快照未被修改；新增适配放在 src/windows/RemoteDisplaySwitch.PowerDisplay，不把产品行为混入 vendor。

升级时显式更新源码快照/清单，检查依赖与相关上游差异，运行全部测试；发现、配对、句柄或电源路径变化必须重新通过目标实机回归。源码仅从锁定的上游提交导入。

PowerToys 和 WmiLight 的 MIT 许可证及分发要求见 THIRD-PARTY-NOTICES.md。新产品尚未发布或推送远程仓库；本机配置、诊断记录及构建产物不纳入版本控制。
