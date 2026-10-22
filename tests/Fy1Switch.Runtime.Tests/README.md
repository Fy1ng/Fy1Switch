# Windows P1 Runtime 无硬件测试

目标框架为 `net10.0-windows`，使用 MSTest 3.8.3，直接引用 Runtime 项目。

## 运行

在仓库根目录执行：

```powershell
dotnet test tests/Fy1Switch.Runtime.Tests/Fy1Switch.Runtime.Tests.csproj --artifacts-path tests/Fy1Switch.Runtime.Tests/obj/artifacts -p:RestoreLockedMode=true --logger "trx;LogFileName=runtime.trx" --results-directory tests/Fy1Switch.Runtime.Tests/TestResults --verbosity minimal
```

锁定还原不更新 Runtime/Contracts 的依赖锁文件；项目及引用项目的编译产物统一放在本测试目录的 `obj/artifacts` 下。测试日志在本目录的 `TestResults` 下。

## 隔离与覆盖

- `TestSupport.cs`：Fake `IWorkerTransport` 和 `FakeStartup`。不实例化 WorkerSupervisor，不创建 Worker 进程，不访问显示器、真实注册表或命名管道。Fake transport 不加串行锁，由测试观察 Runtime 本身是否串行。
- `DesktopControllerTests.cs`：默认 Unknown、发现与绑定指纹、显式 ON/OFF、Toggle 权威读取、Unknown 拒绝、重复 operationId 与冲突，以及 Fake 启动注册。
- `DesktopControllerRecoveryTests.cs`：超时与断连的 Unknown、跨重启去重、绑定失效持久化、重新绑定后的 targetId、Pending 恢复且不重放。
- `DesktopControllerConcurrencyTests.cs`：并发命令及 Toggle 读写对的串行、并发重复请求、取消与拓扑变化竞态，以及请求未完成时快照在一秒内返回 Busy。
- `DisplaySettingsRevalidationTests.cs`：显示设置或恢复通知的只读复核、保留相同拓扑的绑定及 targetId、连续通知合并、拒绝过时发现结果、真实变化仍失效、与控制命令串行，以及不重放未确认写入。
- `StateStoreTests.cs`：真实 SQLite 的设备 ID、绑定保存/删除、操作日志更新、终态不变、所有 Pending 转 Unknown，并验证再次重启不改写恢复结果。
- `WireProtocolTests.cs`：纯内存协议行长度边界、未终止行、EOF、版本与 ID。无效请求使用空 controller 哨兵，确保版本/ID 检查先于派发且无需初始化任何运行时资源。

SQLite 只使用 `Path.GetTempPath()` 下本次显式创建的唯一目录。清理先关闭控制器/数据库，仅清除此数据库的连接池，再验证完整路径与父目录，拒绝根目录重解析点，只递归删除该测试实例创建的目录。

并发测试通过异步门闩控制请求进入和释放，不靠 Sleep 猜测时序；门闩在 finally 中释放并有十秒兜底。程序集不并行运行，但并发用例内部真实重叠调用控制器。

这些测试不替代真实显示器、WorkerSupervisor、注册表、进程/管道或 UI 集成验收；传输超时/断连使用 transport 边界上的可控故障注入，不验证真实硬件超时策略。发现生产实现缺陷时应保留失败断言，由 Runtime 维护方修复。
