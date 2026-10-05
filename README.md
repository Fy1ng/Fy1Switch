# RemoteDisplaySwitch

基于 Microsoft PowerToys Power Display 源码的独立 Windows 显示器远程控制项目。

## 当前状态：Windows P1 本地控制可用，系统级验收后置

已实现 WPF 托盘宿主、独立常驻 Worker、本地 CLI、SQLite 绑定与操作日志、登录自启设置，以及独立 x64 发布/安装/卸载流程。**不是已完成的远程控制产品**：Android/OriginOS Tile、HTTPS 与 Relay 尚未实现。使用与验证范围见 docs/Windows-P1.md。

官方 Power Display 退出后，已完成真实 DDC 发现及受限对照实验：原始上游按 CCD 顺序绑定时把唯一响应句柄标为 VDD；按本机 GDI 顺序显式重排后标为 G4Q。用户已于 2026-10-05 人工确认复测时实体屏熄灭约 5 秒后恢复，VDD、复制模式正常。随后 30/30 轮软件循环、保持句柄关闭 60 秒，以及释放原句柄后等待 15/60 秒由全新进程恢复均成功；当前发布包也通过 5 秒全新进程恢复复核。**人工确认只覆盖指定短循环，整机重启、睡眠和物理拔插尚未验证，不把工作进程重启当成整机重启。**详见 docs/P0-findings.md。

## 已确认架构

验证安排更新（2026-10-05）：用户先暂缓物理拔插，随后明确同意将剩余整机重启、睡眠恢复和 VDD 重建验收后置到正式发布前，先推进已确认架构中的 Windows 后台与托盘。后置不等于验收通过；身份或拓扑不确定时拒绝控制。

- 新独立项目，不使用 RemoteScreenSwitch 实验代码。
- Windows：.NET 10 用户会话内 Host/Worker，WPF 托盘设置界面；不在 Session 0 控制显示器。
- 控制核心：锁定 PowerToys 源码子集，保留原始发现、配对、句柄及读写行为，独立适配日志、路径与产品状态。
- Android：Kotlin/Compose 与原生 TileService；短时交互刷新，不以永驻进程保证状态。
- 网络：HTTPS LAN/Tailscale 直连及可信自建 ASP.NET Core Relay；两段 TLS 不宣称端到端加密。
- 当前受限环境下的 P0 开关、长关闭与工作进程恢复已验证；按调整后的顺序推进 Windows P1，系统级验收后置，Android 与 Relay 后续实现。

## Windows 本地版本

~~~powershell
./tools/Test-Windows.ps1
./tools/Publish-Windows.ps1
./tools/Test-Windows-Integration.ps1
~~~

输出 artifacts/windows/win-x64。运行其中 RemoteDisplaySwitch.Host.exe，点击发现、选择、绑定，再刷新/控制；关闭设置窗口后保留托盘。Install.ps1 提供每用户安装，Uninstall.ps1 只删除已标记的程序目录并保留用户数据。自启默认关闭，可在界面选择；没有为开发测试启用用户的真实登录自启。默认集成测试为模拟，不访问硬件；显式 -Hardware 才会触发真实 5 秒开关及宿主重启恢复。

宿主使用本机当前用户命名管道，不监听网络端口。不要将此 IPC 当作远程认证接口。P0 诊断工具与新 Worker 互相避让，不能同时操作硬件。

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
- orphan discovery.lock 表示前次发现中断，下一次拒绝自动探测。先排查原因，再由操作者审查并移走本工具的锁文件；不要删除官方 PowerToys 文件。
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
