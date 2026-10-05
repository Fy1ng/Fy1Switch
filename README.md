# RemoteDisplaySwitch

基于 Microsoft PowerToys Power Display 源码的独立 Windows 显示器远程控制项目。

## 当前状态：P0，尚未通过实体硬件门槛

本轮已建立可独立编译的控制核心、进程隔离的本地诊断工具和无硬件回归测试。**不是已完成的远程控制产品**：托盘宿主、自启、Android/OriginOS Tile、HTTPS 与 Relay 尚未实现。

当前 Windows 清单读取已确认 G4Q（SAC2563）与 VDD by MTT（MTT1337）共享同一个 GDI 源，符合复制模式。运行发现时检测到官方 Power Display 仍在运行，工具按设计拒绝了硬件访问，没有自行停止它。尚未执行本项目的 DDC 发现或电源写入，更未确认实体开关和恢复。

## 已确认架构

- 新独立项目，不使用 RemoteScreenSwitch 实验代码。
- Windows：.NET 10 用户会话内 Host/Worker，WPF 托盘设置界面；不在 Session 0 控制显示器。
- 控制核心：锁定 PowerToys 源码子集，保留原始发现、配对、句柄及读写行为，独立适配日志、路径与产品状态。
- Android：Kotlin/Compose 与原生 TileService；短时交互刷新，不以永驻进程保证状态。
- 网络：HTTPS LAN/Tailscale 直连及可信自建 ASP.NET Core Relay；两段 TLS 不宣称端到端加密。
- P0 硬件门槛通过后才推进 Windows 产品宿主、Android 与 Relay。

## 构建与测试

需要 Windows 和 .NET SDK 10.0.400（同 feature band 的新补丁允许）。

~~~powershell
./tools/Test-P0.ps1
dotnet run --project ./src/windows/RemoteDisplaySwitch.Probe -- inventory
dotnet run --project ./src/windows/RemoteDisplaySwitch.Probe -- discover
~~~

inventory 只读取显示拓扑；discover 会通过原始 Power Display 核心打开句柄、读取能力和初始值，不写电源。默认关闭最大兼容模式，不读取官方 PowerToys 配置或已知良好缓存。

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
~~~

cycle 仅在能力包含 0x01、0x04，且刚读到 0x01 时执行。它发送 0x04，成功后等待 5 秒，在 finally 中尝试发送 0x01；即使 OFF 报错也尝试 ON，不回退到 Hard Off 0x05。退出码为 0 只表示相应调用和最终 ON 读回成功，**不表示物理身份及肉眼开关已验证**。

原生驱动卡死、进程被强制结束或系统故障时，finally 不保证执行；监督进程在 90 秒后终止自己的工作进程并报告结果未知。Ctrl+C 请求不会立即终止正常恢复流程。程序不会自动重试硬件任务，也不提供永久 OFF 或 Toggle 的产品接口。

PowerToys 与本工具没有共同的硬件锁。检测到官方进程时拒绝访问，只是防误用措施，不是对所有第三方 DDC 程序的排他保证；测试期间不要重启其他控制者。

## 隔离与诊断

- 状态目录：%LOCALAPPDATA%/RemoteDisplaySwitch/Probe；不使用 Microsoft/PowerToys 的状态、事件或单实例名称。
- 标准输出为逐行 JSON；标准错误为脱敏上游日志。可在本地重定向保存，报告仍可能含显示型号和拓扑信息，不要无审查上传。
- targetKey 是规范化 DevicePath 的 SHA-256，仅供本地诊断选择，不是认证秘密、实体身份证明或跨端口永久 ID。
- orphan discovery.lock 表示前次发现中断，下一次拒绝自动探测。先排查原因，再由操作者审查并移走本工具的锁文件；不要删除官方 PowerToys 文件。
- 电源适配直接复用上游 IVcpFeatureReader；离散 D6 成功读回不会因 max=0 被连续数值范围验证误判。写入仍走原始 MonitorManager → DdcCiController → SetVCPFeature。

常用退出码：0=调用完成；2=参数无效；3=工作进程超时；4=已有探测运行；5=无目标；6=官方控制者运行；7=发现隔离/黑名单资源异常；8=目标缺失或不唯一；9=读回或写入未确认；10=异常；11=能力/初始状态/拓扑安全条件不满足。

## 发布

~~~powershell
./tools/Publish-P0.ps1
~~~

输出 artifacts/p0/win-x64，必须保留整个目录，不能只复制 EXE。使用自包含 .NET 和 WmiLight 原生依赖，不要求安装 PowerToys 或开发 SDK。干净系统运行及完整产品安装/升级尚待后续验证。P0 是便携验证包，不是正式安装器。

## 上游维护与许可

vendor/powertoys/upstream.json 固定 Microsoft PowerToys 提交及每个原始文件的 SHA-256。tools/Verify-Upstream.ps1 检查原始快照未被修改；新增适配放在 src/windows/RemoteDisplaySwitch.PowerDisplay，不把产品行为混入 vendor。

升级时显式更新源码快照/清单，检查依赖与相关上游差异，运行全部测试；发现、配对、句柄或电源路径变化必须重新通过目标实机回归。源码仅从锁定的上游提交导入。

PowerToys 和 WmiLight 的 MIT 许可证及分发要求见 THIRD-PARTY-NOTICES.md。新产品尚未发布或推送远程仓库；本机配置、诊断记录及构建产物不纳入版本控制。
