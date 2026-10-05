# P0 实机证据：2026-10-05

## 边界

锁定 PowerToys 1400fd8e999f381329e16e9df4084f7dc588c8a7。65 个原始 vendor 文件未修改；不使用旧 RemoteScreenSwitch 代码。本次是在用户退出官方 Power Display 后进行的独立程序测试。此记录不是整体验收通过证明。

## 原生只读发现

| 观察 | G4Q / SAC2563 | VDD by MTT / MTT1337 |
| --- | --- | --- |
| CCD 路径索引 | 0 | 1 |
| EnumDisplayDevices 的 GDI 索引 | 1 | 0 |
| GDI 源 | 同为 DISPLAY1 | 同为 DISPLAY1 |
| OutputTechnology | 10，DisplayPort external | 5，HDMI |

本机 VDD 报告 HDMI，说明不能简单用 OutputTechnology 的 indirect 值过滤虚拟显示器。两个 GDI 设备描述均为 Generic PnP Monitor，描述也不能区分实体身份。

按上游原始 CCD 列表传入时，G4Q 名称对应的能力读取失败，唯一响应的句柄被标记为 VDD，能力 model=RTK，D6 值为 1，支持列表为 [1,4,5]。发现的首次调用出现一个 NULL 句柄；上游重试后继续处理两个候选。

这些结果说明上游按下标绑定的标签存在歧义，不能证明 VDD 真有物理电源控制能力，也不能把能力读失败当作虚拟身份。

## 显式 GDI 顺序对照实验

实验只改变传给上游 DdcCiController 的目标顺序，不更换 DDC 传输。仅接受 SAC2563 + MTT1337 这两个已知目标、同一 GDI 源，且 GDI/CCD 集合完整唯一。其他拓扑拒绝，不自动推广。

按 GDI 顺序传入后，唯一响应句柄被标为 G4Q，实时 D6=1。随后运行一次 5 秒 DPM→ON 测试：

| 步骤 | 软件层实测结果 |
| --- | --- |
| 写前 D6 读取 | 成功，0x01 |
| 写入 DPM 0x04 | 成功，无 Win32 错误 |
| 等待 | 约 5 秒 |
| 写入 ON 0x01 | 成功，无 Win32 错误 |
| 再次读取 D6 | 成功，0x01 |
| 进程退出码 | 0 |

首次测试后用户说明当时未在实体屏旁，因此再次执行短时循环。2026-10-05 用户明确确认复测时实体 G4Q 熄灭约 5 秒后恢复，VDD 与复制模式正常。这是该轮独立控制的人工证据，不是第一轮无人观察测试的补签，也不自动验证以后每次发现的映射。

机器输出继续保留 identityVerified=false、physicalPowerValidated=false，因为程序无法自行观测实体画面，且尚未建立正式绑定协议。默认非实验路径拒绝共享源拓扑的电源写入，避免沿用有歧义的标签。单轮人工确认不能替代长期与重建句柄测试。

## 更长关闭与工作进程重启

在人工短循环确认后，继续进行了以下软件层实机验证：

| 场景 | 结果与证据边界 |
| --- | --- |
| 保持原句柄，DPM 60 秒 | OFF/ON 均成功，最终实时读回 0x01 |
| OFF 后结束原工作进程，等待 15 秒，由全新进程恢复 | 原进程确已结束，新进程重新发现成功；ON 前实时读回 0x02，ON 写入成功后读回 0x01 |
| OFF 后结束原工作进程，等待 60 秒，由全新进程恢复 | 原句柄已随进程释放；新进程重新发现并读到 0x02，写 ON 后读回 0x01，退出码 0 |
| 30 轮间歇循环，每轮新工作进程重新发现 | 30/30 软件循环通过，0 个非零退出码；每轮 DPM 5 秒，轮间至少开启 3 秒，最终 ON 读回成功 |
| 当前自包含发布包，5 秒工作进程重启恢复复核 | OFF 后原工作进程被结束，全新进程 ON 成功，最终读回 0x01 |

这证明本次重启恢复不依赖原 DDC 句柄存活。写入 0x04 后实际报告 0x02（Standby），因此不能要求关闭读回必须字面等于 0x04。以上是具体运行的 API 证据，不冒充每一轮的人工观察，也不涵盖整机重启或睡眠。

测试结束后再次读取拓扑：仍是 G4Q 与 MTT VDD 共享 DISPLAY1，CCD/GDI 顺序与测试前一致；没有残留 discovery.lock。无硬件自动化测试 142/142 通过（87 个上游回归 + 55 个本项目测试）。所有诊断进程均已结束，最后一次电源读回为 ON。

## 必须保留的限制

- 微软只规定 QueryDisplayConfig 返回路径优先级顺序，未保证它、EnumDisplayDevices 与 GetPhysicalMonitorsFromHMONITOR 的数组同序。
- PHYSICAL_MONITOR 只含句柄和描述，没有公开 DevicePath 字段；此实验不是通用的可靠映射 API。
- capabilities 字符串可能由 INF 覆盖，model=RTK 与 D6 支持列表不是唯一物理身份证据。
- 原上游仍可能过滤 NULL 句柄；本实验不修正其全部句柄生命周期问题，不以一次成功证明这些场景安全。
- 未设置 Hard Off 0x05，未改 Windows 显示模式、VDD 配置或官方 PowerToys 配置。
- WMI 发现曾返回 4200；当前两台均继续走 DDC 路径，不把这条 WMI 日志当作实体控制结果。

## 待完成

2026-10-05 用户先明确暂不验证物理拔插，随后同意把整机重启、睡眠恢复和 VDD 重建等剩余系统级验收后置到正式发布前，先继续 Windows 后台与托盘。这些项目仍未验证，不能用已通过的短时循环和工作进程恢复替代。

1. 已完成人工短循环确认，继续保留其限定范围。
2. 工作进程重启、多轮与最长 60 秒 OFF 的软件验证已完成；整机重启、睡眠/恢复、VDD 重建仍待验证，物理拔插暂缓。
3. 在证据充分后决定正式产品的受限绑定策略；当前实验开关不自动成为产品契约。
4. 按后续批准的顺序，Windows P1 已开始并完成本地闭环，详见 Windows-P1.md；Android 与 Relay 尚未实现。

原始诊断输出保存在本机 diagnostics 目录并被 Git 忽略，不提交完整设备路径或本机目标键。

## 原始来源

- https://learn.microsoft.com/en-us/windows/win32/api/physicalmonitorenumerationapi/nf-physicalmonitorenumerationapi-getphysicalmonitorsfromhmonitor
- https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-enumdisplaydevicesw
- https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-querydisplayconfig
- https://learn.microsoft.com/en-us/windows/win32/api/physicalmonitorenumerationapi/ns-physicalmonitorenumerationapi-physical_monitor
- https://learn.microsoft.com/en-us/windows/win32/api/lowlevelmonitorconfigurationapi/nf-lowlevelmonitorconfigurationapi-capabilitiesrequestandcapabilitiesreply
