# Windows P1 Worker

独立的 `net10.0-windows` 控制台进程。只由 Host 启动，不提供独立发现或电源控制 CLI：

```text
RemoteDisplaySwitch.Worker --pipe <name> --parent-pid <pid> --data-dir <absolute-local-directory> [--simulate]
```

连接本机当前用户的命名管道，10 秒内连接失败退出；核对 `GetNamedPipeServerProcessId` 后才创建 provider。协议使用 Contracts 的 UTF-8 NDJSON 和 `WireJson.ReadLineAsync` 的 65,536 字符上限。每条请求完整处理并返回之后才读取下一条；上游发现内部的异步并行不改变命令串行约束。需要非空 `requestId` 和 `version: 1`；支持 `discover/read/set/shutdown`，有效请求 ID 原样回显，EOF 或成功 shutdown 释放句柄并退出。

## 模拟与真实控制

- `--simulate` 只创建内存 provider，不配置 PowerDisplay、不创建数据目录、不实例化 DDC。唯一目标是 64 个 `a`，指纹是 64 个 `b`，名称“模拟显示器”，profile/source 都是 `simulation`。初始 On，必须先 discover；只接受显式 On/Off。
- 真实 provider 独占一个长期存活的 `DdcCiController`，使用 `MemoryVcpStore`，不使用官方 PowerToys 配置、WMI provider 或 MonitorManager。通过 `PathConstants.Configure` 将上游发现锁定向自己的 data-dir。
- 发现仅支持已测试 SAC2563 + MTT1337、唯一共享 GDI 源，并复用 `GdiOrderingExperiment.Reorder` 和完整上游 `DiscoverMonitorsAsync`。要求唯一 DDC 候选匹配 SAC2563 的 targetKey，且 D6 明确支持 01 和 04；不按名称猜测身份。
- 固定 profile 为 `sac2563-mtt1337-gdi-v1`。指纹是 `SHA256(UTF8(profile + "\n" + WireJson.Serialize(TopologyDiagnostics.Capture())))` 的小写十六进制；不裁剪、重排或忽略完整诊断中的字段及非活动 GDI 条目。
- 同一会话重复 discover 只校验拓扑并返回已有快照，不重新分配句柄。发现已经进入 DDC 后若失败，需由 Host 显式决定是否重启；检测到拓扑变化后，该 Worker 永久拒绝当前会话操作，不静默重映射。
- 每次 read/set 写前检查请求指纹、会话指纹和当前拓扑，目标键大小写也必须精确一致。写后及读回后再次核对拓扑；变化返回 `topology_changed`，不附带可误认为确认的观察。写入只有 01/04；观察仅来自 `ReadPowerObservationAsync`，01=On，02..05=Off，失败或未知值=Unknown，source 恒为 `hardware_read`。

## 安全与结果语义

- 检测到 `PowerToys.PowerDisplay` 或 `RemoteDisplaySwitch.Probe` 进程时返回 `display_process_conflict`，不会停止它们。数据目录中的独占 `worker.instance.lock` 防止同目录重复 Worker。
- 已有 `discovery.lock` 返回 `discovery_quarantined`，不读取内容、不自动删除。正常上游发现仍管理它自己本次创建的锁。需要 Host/用户在检查后决定恢复；Worker 不做自动重试。
- `WriteAccepted` 独立表示底层写入返回值：未写为 false，底层写调用抛出且结果未知为 null，写后拓扑变化仍保留实际返回值。`Success=true` 的 set 仅表示写被接受，不表示物理关屏或读回匹配；Host 必须按 `WriteAccepted`、`Observation`、`ErrorCode` 联合判断。读回失败仍可返回 `WriteAccepted=true`、Unknown 和 `power_read_failed`。
- 原始核心日志不直接转发，只将事件级别转换为固定安全代码写入 stderr。异常消息、设备路径、原始能力字符串不进入日志或协议。stdout 不用于协议。
- 父进程超时、挂起终止、Host/CLI 状态机由 Runtime/Host 负责。此组件不宣称消除拓扑采样与驱动调用之间的系统级竞态，也不能取消已经进入驱动的同步调用。进程冲突检测是调用前采样，不是与第三方程序的跨进程互斥协议。

## 无硬件验证

从仓库根目录运行以下 PowerShell 命令，把所有引用项目的构建产物和还原中间文件都隔离到测试目录，且不改其他项目的锁文件：

```powershell
$artifacts = Join-Path $PWD 'tests/RemoteDisplaySwitch.Worker.Tests/obj/isolated-build'
dotnet test tests/RemoteDisplaySwitch.Worker.Tests/RemoteDisplaySwitch.Worker.Tests.csproj --artifacts-path $artifacts -p:RestorePackagesWithLockFile=false "-p:NuGetLockFilePath=$artifacts/unused.packages.lock.json"
```

测试仅使用模拟 provider、假 PowerDisplay 后端及本机测试管道；唯一启动的 Worker 子进程必带 `--simulate`，不会启动真实发现或电源写入。物理显示器效果仍需单独人工验收。
