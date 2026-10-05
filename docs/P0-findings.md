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

仍保留 identityVerified=false、physicalPowerValidated=false。API 回执不能替代肉眼确认，单轮也不能替代长期与重建句柄测试。默认非实验路径现已拒绝共享源拓扑的电源写入，避免沿用有歧义的标签。

## 必须保留的限制

- 微软只规定 QueryDisplayConfig 返回路径优先级顺序，未保证它、EnumDisplayDevices 与 GetPhysicalMonitorsFromHMONITOR 的数组同序。
- PHYSICAL_MONITOR 只含句柄和描述，没有公开 DevicePath 字段；此实验不是通用的可靠映射 API。
- capabilities 字符串可能由 INF 覆盖，model=RTK 与 D6 支持列表不是唯一物理身份证据。
- 原上游仍可能过滤 NULL 句柄；本实验不修正其全部句柄生命周期问题，不以一次成功证明这些场景安全。
- 未设置 Hard Off 0x05，未改 Windows 显示模式、VDD 配置或官方 PowerToys 配置。
- WMI 发现曾返回 4200；当前两台均继续走 DDC 路径，不把这条 WMI 日志当作实体控制结果。

## 待完成

1. 人工确认本次 G4Q 确实熄灭约 5 秒后恢复，VDD 与复制模式未受影响。
2. 确认后再进行多轮、长时间 OFF、重发现/程序重启和睡眠/拔插恢复验证。
3. 在证据充分后决定正式产品的受限绑定策略；当前实验开关不自动成为产品契约。
4. P0 通过后再进入托盘 Host/Worker、Android 和 Relay 阶段。

原始诊断输出保存在本机 diagnostics 目录并被 Git 忽略，不提交完整设备路径或本机目标键。

## 原始来源

- https://learn.microsoft.com/en-us/windows/win32/api/physicalmonitorenumerationapi/nf-physicalmonitorenumerationapi-getphysicalmonitorsfromhmonitor
- https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-enumdisplaydevicesw
- https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-querydisplayconfig
- https://learn.microsoft.com/en-us/windows/win32/api/physicalmonitorenumerationapi/ns-physicalmonitorenumerationapi-physical_monitor
- https://learn.microsoft.com/en-us/windows/win32/api/lowlevelmonitorconfigurationapi/nf-lowlevelmonitorconfigurationapi-capabilitiesrequestandcapabilitiesreply
