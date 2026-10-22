# PowerDisplay P0 无硬件回归测试

## 项目与来源

- 目标框架：`net10.0-windows`；测试程序集：`Fy1Switch.PowerDisplay.Tests`。
- 仅引用 `../../src/windows/Fy1Switch.PowerDisplay/Fy1Switch.PowerDisplay.csproj`，沿用 `PowerDisplay.UnitTests` 与上游生产类型的命名空间。
- 固定来源：[microsoft/PowerToys，修订 1400fd8e999f](https://github.com/microsoft/PowerToys/tree/1400fd8e999f/src/modules/powerdisplay/PowerDisplay.Lib.UnitTests)。取自已提供的同修订本地只读快照，不跟随最新分支。
- `Upstream/` 的七个测试文件保留 Microsoft 版权头；MIT 许可原文见 `Upstream/LICENSE`。
- MSTest 固定为 `3.8.3`：已核对上游 `Directory.Packages.props` 的 `MSTestVersion` 与 `PackageVersion Include="MSTest"`，以及 `PowerDisplay.Lib.UnitTests.csproj` 对该包的引用。不引入 Moq、Settings.UI 或上游整个测试项目。
- 唯一上游内容调整（不改变断言）：`MonitorBlacklistServiceTests.IsBlocked_EmptyBuiltIn_ReturnsFalse` 改名为 `IsBlocked_UnlistedMonitorModels_ReturnsFalse`，删除“列表为空”的过时注释，原断言不变。固定快照的黑名单实际包含 `LTM2C02` 与 `GSM7714`。

## 覆盖与安全边界

| 测试文件 | 范围 | 展开数据行后的用例数 |
| --- | --- | ---: |
| `Upstream/MonitorIdentityTests.cs` | DevicePath/WMI 字符串归一、不同 UID、EDID 提取、旧标识解析 | 21 |
| `Upstream/MonitorIdComparerTests.cs` | 忽略大小写的标识相等性、空值、集合匹配 | 5 |
| `Upstream/VcpFeatureValueTests.cs` | 原生范围与百分比换算、钳制、无效范围、往返误差 | 11 |
| `Upstream/VcpDiscoveryEvidenceTests.cs` | 人工构造的能力/缓存证据合并、失效句柄错误、缓存与实时值优先级 | 12 |
| `Upstream/BuiltInMonitorBlacklistTests.cs` | 内嵌黑名单加载、规范化与进程内缓存 | 4 |
| `Upstream/MonitorBlacklistServiceTests.cs` | 未列入名单的模型、空或格式错误的标识 | 2 |
| `Upstream/DdcErrorClassifierTests.cs` | Win32 数值常量、暂态错误与句柄失效分类、格式化 | 25 |
| `MonitorBlacklistResourceTests.cs` | 内嵌资源确实装配、固定名单、大小写和不同端口标识匹配 | 7 |

共 87 个预期用例（60 个上游测试方法、2 个新增方法，数据行展开）。实际发现和通过数量以测试运行结果为准。

测试只操作构造的字符串、值对象、字典及程序集内嵌 JSON。`VcpDiscoveryEvidenceTests` 中的“实时探测”是手工构造的 `VcpProbeObservation`，不会执行探测服务。黑名单测试不读取用户磁盘配置或 PowerToys 设置目录。

不实例化硬件控制器，不执行显示器枚举、WMI/DDC/CI 探测、电源设置、Profile 或 Settings.UI 路径；不含 Android/Relay。通过这些测试不代表通过真实硬件门槛。

## 集成要求与运行

核心项目须保留选中生产类型，向 `Fy1Switch.PowerDisplay.Tests` 开放 `InternalsVisibleTo`，并以内嵌逻辑名 `PowerDisplay.Models.BuiltInMonitorBlacklist.json` 装配固定上游资源。新增资源测试可防止资源缺失时回退空列表造成假阳性。

在仓库根目录、Windows 与 .NET 10 SDK 下运行：

```powershell
dotnet test ./tests/Fy1Switch.PowerDisplay.Tests/Fy1Switch.PowerDisplay.Tests.csproj --configuration Release --artifacts-path ./tests/Fy1Switch.PowerDisplay.Tests/.artifacts -p:RestoreLockedMode=true --results-directory ./tests/Fy1Switch.PowerDisplay.Tests/TestResults --logger "trx;LogFileName=powerdisplay-pure.trx"
```

命令将项目构建产物集中于本测试目录，并使用锁定还原，避免更新核心项目已有锁文件中的依赖解析结果；本测试项目自己的 `packages.lock.json` 随项目保留。常规 NuGet/SDK 缓存仍由本机工具链管理。主项目或 vendor 尚未就绪时，应等待集成后运行，不用测试内重复实现生产类型来绕过依赖。

2026-10-05 已使用该命令在 Windows / .NET SDK 10.0.400 下验证：构建成功，87 个测试通过，0 失败，0 跳过。TRX 输出位于本目录 `TestResults/powerdisplay-pure.trx`（不纳入版本控制）。未执行硬件发现或电源调用，未验证真实硬件。

## 原始上游文件 SHA-256

以下校验值针对原始快照字节，不针对复制后可能规范化换行的本地文件。黑名单测试的改名与注释删除已在上文说明。

| 原始文件（均位于上游 PowerDisplay.Lib.UnitTests） | SHA-256 |
| --- | --- |
| `MonitorIdentityTests.cs` | `d6cc65b316272d54b5d01de83f422bc60f582ac29ce733340e98473d3396e78b` |
| `MonitorIdComparerTests.cs` | `2c7b76393526867fa288a8fdbb58266cd22b34ae99ec3fac42232049284fe0cf` |
| `VcpFeatureValueTests.cs` | `48bafd2968a2b0bdca3d5f1cbb915ff38287afa1a1eb53266e0ad0dc2ab0fc58` |
| `VcpDiscoveryEvidenceTests.cs` | `4688f6f4de5e033086058833c542bb6e658f44354c1dd389d884702180efb203` |
| `BuiltInMonitorBlacklistTests.cs` | `d111f5a1d44c6f59ab669731915674f7d2fd7c8d4ec34baa06c69d297c72b470` |
| `MonitorBlacklistServiceTests.cs` | `e75c5b4ea7dee63b68e89604880eb85b31a26d64ec716a7f03a1e35e131f51f1` |
| `DdcErrorClassifierTests.cs` | `a06ac3b08b425e9a89633e3062a0dbfff5458ec49d422487f87b0bbf6c4c890f` |
