# Android App 与快捷设置开关

完整更名后的包名为 `com.fy1ng.fy1switch`, 与更名前的旧版独立安装. 旧版数据和 Keystore 不迁移或删除, 新版需要重新配对并添加新的快捷设置开关. 以下稳定组件和协议标识指 Fy1Switch 新版, 不表示可以覆盖安装旧包.

## LAN 六位短码（2026-10-18 更新）

Android“添加电脑”支持原生 `NsdManager` 发现/广播 `_fy1switch-pair._tcp.`，TXT 仅 `v=1`、`role=android`、`sid=<小写 UUID>`。电脑列表自动随发现/丢失更新，只有一台时自动选中；名称不是可信身份。界面统一输入电脑屏幕上的六位短码（Android SRP client），明文保留前导零，输入六位后仅自动提交一次，失败后需要修改或手动重试。底层 Android SRP server 保留协议兼容，手机不再显示反向配对短码。保留离线扫码和高级 JSON 粘贴；未提供手填 IP/session 的假入口。本手机名称默认采用设备型号，可手动修改。

Wire 严格依据 [Pairing-v1.md](Pairing-v1.md)：BC `bcprov-jdk18on:1.83`、RFC5054 3072/SHA256、BC evidence/session key、384 字节 A/B、32 字节 M1/M2/key、transcript HKDF-SHA256、AES256GCM。Client hello 在 salt 到达前发送 A，因此 SRP6Client 子类仅在收到 challenge 后设置 BC calculateX，保留同一随机 a/A；不重新生成 A、不另造 proof。TCP JSON 行最大 16KiB；短码不明文上 wire、不写磁盘、不输出日志。

只有页面处于 RESUMED 且未扫码/进行其他配对时开启临时 `ServerSocket(0)`。离页、pause、后台、SCREEN_OFF、锁屏或完成关闭监听/握手 socket、NSD 和 multicast lock；扫码启动前先清理，返回可重新开始。会话 generation 丢弃过时回调。每码最多 5 分钟/5 次 admission；进程共享 10 次/10 分钟和单握手 gate，刷新/页面重建不能重置进程限流。连接和 PAKE 总计 15 秒、配对总计 45 秒，有独立 socket 截止计时器防慢速分片；成功 proof 消费短码，后续 HTTPS 失败也须重新开始。

无论 SRP 角色，Android **只在验证 PAKE 后**接收 authenticated `windows-bundle`。bundle 中的 Windows deviceId/pin 才进入既有 `DeviceRepository.pair`，使用原严格 pin HTTPS `/v1/pair` 并保存旧格式凭据；不把 mDNS device identity/pin 当 expected identity。已经配对的 deviceId 不自动重复 pair。保存成功后才发送 encrypted `android-result {success:true}`；HTTPS/证书失败发送 false。若 ack 途中断开，电脑不得假定完成，手机可能已经保存凭据。

Android 29 使用串行 legacy resolve，进程共享 resolve 队列避免页面重建时 `FAILURE_ALREADY_ACTIVE`。按能力取得 Wi-Fi multicast lock，API 33+ 使用 NSD network，连接时绑定解析网络（否则使用可用 Wi-Fi network）。不要求公网发现、后台保活或硬件控制。同 SSID 也可能有 AP/访客隔离、组播过滤、防火墙或 OEM NSD 限制；本地 JVM/build 不代表真机发现、锁屏、相机返回已验收。

### JVM/跨语言联调 API

包 `com.fy1ng.fy1switch.pairing`，不依赖 Android UI 的入口：

- `PairWire.read(): JSONObject` / `write(JSONObject)`，TCP 实现 `SocketPairWire(Socket)`。
- `ShortCodeProtocol.client(wire, serverSession, code, clientRole="android", clientSession=<UUID>, onProof={})`。
- `ShortCodeProtocol.server(wire, serverSession, code, serverRole="android", onProof={})`。
- 两者返回 `SecureChannel`：`encrypt(purpose, plaintext)` / `decrypt(purpose, frame)`；`transcriptHash` 可供 fixture 断言。每方向每 purpose 一次。测试可传 `windows` 角色；生产入口固定 Android。调用者负责 socket 关闭、截止时间及 admission，生产由 `LanPairing` 负责。
- `ShortCodePairing.acceptBundle(repository, text, clientName): Boolean` 是 suspend 函数；测试用内存 StateStore，生产仍使用原 repository/store。不返回或回传 bearer。

显式任务 `:app:liveShortCodeTest` 读取 `FY1SWITCH_SHORT_CODE_FIXTURE` 指向的临时 UTF-8 JSON **文件路径**（可带 BOM），不进入常规 unit test。缺 env、失效 fixture、握手/身份/pin/target 断言失败均失败，不 skip、不复用缓存。测试只允许 loopback，不安装 APK、不调用电源 set。

正向 fixture schema（Windows 显示码，Android JVM 连接）：

```json
{"host":"127.0.0.1","port":12345,"sessionId":"<Windows lowercase UUID>","code":"000042","expectedDeviceId":"<fixture Windows UUID>","targetId":"<simulated target UUID>"}
```

反向 fixture schema：`{"expectedDeviceId":"<UUID>","targetId":"<UUID>","reverseControlUrl":"http://127.0.0.1:<port>/<fixture-trigger>"}`。测试先开 loopback `ServerSocket(0)`，生成手机 session/code，然后向该 **测试专用 loopback CLI 协作入口** POST `{"host":"127.0.0.1","port":<actual>,"sessionId":"<Android UUID>","code":"<six digits>"}`。Windows fixture 用 SRP client 接回、发送自己创建的 bundle、验证 encrypted ack；trigger 返回 2xx。这个明文控制入口只属于显式本地 fixture，不是生产配对 wire，不加入 App。生产短码只输入对方 UI。两方向分别导出/调用一次，以免重复消费 code。

`expectedDeviceId` 仅来自测试操作者可信 fixture，在 PAKE 解密 bundle 后、HTTPS pair 前断言；生产不从发现记录读取它。测试还检查 bundle HTTPS loopback 和 repository 状态中的模拟 targetId。该 schema 是此入口的集成契约，Windows CLI fixture 需按此提供；未提供真实 fixture 时不得声称 Java/C# 双向互通通过。

运行：设置 `JAVA_HOME`、`ANDROID_HOME` 和 `FY1SWITCH_SHORT_CODE_FIXTURE` 后执行 Gradle 8.13 `-p src/android --no-daemon --console=plain :app:liveShortCodeTest`。报告位于 `src/android/app/build/reports/tests/liveShortCodeTest/`。常规检查仍为 `testDebugUnitTest lintDebug assembleDebug`，覆盖旧测试及新增双角色、错误输入/proof/public value、transcript、GCM 篡改/重放、framing、限流/到期/消费和 repository 配对测试。

## 构建与产物

- 包名：`com.fy1ng.fy1switch`。
- 稳定 Tile 组件：`com.fy1ng.fy1switch/.DisplayTileService`。升级时不要重命名或替换组件。
- Android 10 / API 29 起；compileSdk / targetSdk 36。
- Kotlin 2.3.10、Compose BOM 2025.08.01、AGP 8.13.2、Gradle 8.13；Java 字节码目标 17。本机使用 JDK 21.0.11，为已有缓存的稳定兼容组合。
- 无常驻前台服务、WebSocket、开机广播接收器或后台轮询。仅需要网络权限，不申请通知权限。

仓库根目录下，用 PowerShell 7 运行：

```powershell
.\tools\Build-Android.ps1 -SdkPath C:\android-sdk -JavaHome 'C:\Program Files\Java\jdk-21.0.11'
```

脚本从当前用户 Gradle wrapper cache 定位 **8.13**，也可用 `-GradlePath <gradle-8.13\bin\gradle.bat>` 显式指定。不会安装 Gradle、写入 local.properties 或安装 APK；默认模式不接触 Windows 服务，仅显式 `-LiveWindowsTest` 才访问下述 loopback fixture。首次缺少 Maven 依赖时从 Google/Maven Central 下载；依赖完整后可加 `-Offline`。只构建本地 debug 包，不代表可直接商店发布。

执行单元测试、lint 和 debug APK 构建（Gradle 可并行执行互不依赖任务）：

- APK：`src/android/app/build/outputs/apk/debug/app-debug.apk`
- JUnit XML：`src/android/app/build/test-results/testDebugUnitTest/`
- 测试 HTML：`src/android/app/build/reports/tests/testDebugUnitTest/index.html`
- lint：`src/android/app/build/reports/lint-results-debug.html`

## 使用

1. 在可信 Windows 端启用 HTTPS 服务并导出一次性 PairBundle JSON；手机和电脑先有直连网络。
2. 打开 App，粘贴 JSON，填写本手机名称。App 仅接受 version 1、有效期尚未结束且最多剩余 5 分钟的 bundle，严格绑定其中的设备 UUID 和证书 pin。
3. 配对成功后加密保存 token 并立即请求 `status`。若配对成功但连接状态获取失败，配对仍保留，可实时刷新。配对超时不自动重放一次性码；应检查 Windows，必要时重新导出。
4. 在设备列表切换默认设备；此选择同时决定 Tile 控制对象。设备配置可更改显示名称、添加/更新/删除 Relay，或移除本机配对。直连地址或证书变更需重新配对。
5. “连接 / 获取主机已知状态”发送 `status`，不要求硬件读；“实时读取显示器”发送 `refresh`。开启、关闭、切换均为真实协议请求，而非本地 UI 模拟。
6. 在系统快捷设置编辑页添加“远程显示器”；API 33+ 也可使用 App 的添加按钮。长按开关打开配置。显示器状态待确认时，点按快捷开关只刷新，不发送切换操作；尚未配对或操作进行中时不可点击。

一次最多保存 20 台设备。编辑中的配对 JSON / Relay token 不进入 SavedState；提交后清空输入。配对码不会落盘。仅“添加电脑”和扫码页面通过 FLAG_SECURE 保护截图及最近任务预览，主页可截图。App 不主动清理系统剪贴板，用户可自行清除其中的配对 JSON。

## 协议和通道边界

源契约：`src/shared/Fy1Switch.Contracts/RemoteMessages.cs`、`Messages.cs`。JSON 使用 camelCase；intent、观测状态、operation outcome 保持 C# 枚举大小写字符串，包括 `On`、`Off`、`Toggle`、`Unknown`、`Pending`、`Confirmed`、`AcceptedUnconfirmed`、`Failed`。

| 调用 | 地址 | 鉴权 |
| --- | --- | --- |
| 配对 | Windows `POST /v1/pair`，body `{code,clientName}` | 预配置 Windows SPKI pin；不发送现有设备 token |
| 直连控制 | Windows `POST /v1/control` | `Authorization: Bearer <deviceToken>` |
| Relay 控制 | Relay `POST /v1/devices/{selectedDeviceId}/control` | `Authorization: Bearer <relayControllerToken>`；`X-Device-Token: <deviceToken>` |

Relay 在每台设备的配置内后置添加，不支持 Relay-only 首次配对。地址必须为 HTTPS 根地址（允许端口、IPv4、IPv6；禁止 userinfo、路径、query、fragment）。更换 Relay 地址必须重新填写控制端凭据，避免把旧 token 静默发送给新主机；同地址留空保留现有 token。控制端 token 不得等于 deviceToken。Relay 可接触代理调用所需的设备 token，需由用户明确配置可信服务。

每条命令使用新 UUID requestId，expiresAt 为 UTC 当前时间 + 30 秒、version 1；set 必带 targetId、intent 和 operationId。operation 查询携带原 operationId 和原 targetId，并使用**新的、未过期的查询请求**。不把旧 set 请求重新排队。

- 优先直连，call timeout 最多 5 秒；仅网络 I/O 失败可回退已配置的 Relay，最多 7 秒。
- 两通道使用**同一序列化 body、requestId、operationId、expiresAt**，不因为回退重新生成操作。
- 各次发送前检查剩余有效期，剩余时间更短则缩短 timeout；过期不发送后续通道。
- TLS 身份失败、HTTP 错误/鉴权拒绝、协议解析失败不自动换通道。两边都超时的 set 记为 Unknown，而不是成功或肯定未执行。
- 禁止自动重定向和 OkHttp 自动重试，防止凭据转发与隐藏重发。响应体最多读取 64 KiB。
- 响应必须匹配 requestId、version、当前选定设备 deviceId；operation 必须匹配请求携带的 operationId / targetId / intent。额外 capabilities 不参与授权，不从响应 body 切换当前设备或路由目标。

## 安全存储与 TLS

所有设备 token、Relay token、端点、pin、默认设备和最近状态/操作一起序列化，使用 Android Keystore 非导出 AES-256 key、随机 IV、AES-GCM 和固定版本 AAD 加密，AtomicFile 原子写入 `noBackupFilesDir/devices.aesgcm`。不是新建的第三方加密协议。

`allowBackup=false`、`fullBackupContent=false`，API 31+ extraction rules 排除云备份与设备迁移全部数据域；文件另在 no-backup 目录。密钥失效/读取失败/写入失败一律关闭控制能力，不悄悄改存明文。需先尝试重启；无法恢复时清除应用数据并重新配对。卸载/清数据会丢失本机凭据；本机移除配对不等于撤销 Windows 端授权。

直连为每个请求创建绑定该设备身份的 TLS 配置。X509TrustManager 检查叶证书有效期以及 `SHA-256(SPKI)` 常量时间比较，只有匹配的证书才接受；因此允许持有对应私钥的自签证书，但绝非 trust-all。HostnameVerifier 要求连接 host 与用户配对 endpoint.host 一致（DNS 大小写不敏感），并再次校验相同 pin 与证书有效期；在这些条件满足时允许 IP 或 DNS 名称与证书 SAN 不同。因此 Windows 证书仅含 localhost SAN 时，也可使用用户配对的 Tailscale MagicDNS 主机名。不能借同 pin 访问另一个未选择 host；没有全局 SSL 配置修改。Relay 的系统 TLS / hostname verifier 不变。

Relay 使用独立系统信任链/hostname verifier，不套用 Windows pin。应用无 HTTP 日志拦截器，不输出 token、配对码、原始响应或异常详情；错误展示使用固定中文提示。远端显示名称只作为显示文本使用，不是安全事实。

lint 的 `CustomX509TrustManager` 警告对应上述明确要求的 pin-only 自签信任模型，保留警告便于复核；依赖“有更新可用”警告不意味着组合不兼容。唯一局部 lint 抑制是 API 29–33 的旧版 `startActivityAndCollapse(Intent)`：实现用 SDK 分支隔离，API 34+ 必走 immutable PendingIntent，不能移除版本判断。

## 结果与生命周期

### 可选后台状态同步

在电脑设置页开启"后台同步显示器状态". 这是应用级开关, 始终跟随当前默认电脑, 默认关闭. 开启前检查系统通知开关, Android 13+ 请求通知权限, 并检查后台同步通知类别; 未获得权限或服务启动失败时显示原因, 不把未运行的服务显示为正在同步. 通过用户在前台开启或返回应用恢复已启用选项来启动服务, 不新增开机广播或后台强行自启动.

服务声明为 `connectedDevice`, 使用已配对外部电脑的网络连接, 复用原有 HTTPS/证书验证/设备授权以及 LAN/Tailscale/Relay 路由. 正常情况下每次读取结束后等待约 10 秒, 离线等失败逐步退避至 60 秒; 身份、安全、授权、协议或安全存储错误会停止同步. 常驻通知显示当前电脑和读回状态, 可点击"停止同步"; 关闭选项同时停止服务并取消读请求. 不使用普通 dataSync 服务冒充无限后台同步.

后台仅发送 live `refresh`, 不发送开关命令, 不查询或改写历史操作记录. 后台读不设置前台 busy/request/notice, 避免按钮周期性不可用或 Snackbar 闪烁. 用户操作先取消并等待后台读释放共用 Mutex, 再执行原操作; 后台不会另建网络路径或与前台并发访问同一仓库. 后台取消不取消服务的后续同步循环, 退出服务则取消整个循环.

仅成功的实时读回与当前 set 的确认读回更新手机单调时间记录, 旧 operation 查询不延长有效期; 此记录不保存到磁盘, 不比较电脑和手机的墙上时间. Tile 保留 non-active 模式和原组件身份: 后台实际运行且具有 25 秒内可信读回时, 下拉后直接呈现该状态, 不额外等待一次网络刷新. 过期状态在 UI/Tile 本地遮蔽并回退只读恢复, 不把旧缓存用于点击后的延迟切换. 未决操作仍沿用现有恢复流程.

系统回收、强制停止、通知被禁用或网络不可达均可能中断后台同步, 不宣称具有系统内置组件的运行保证. 回到应用后可恢复仍启用的服务; 真机需覆盖通知授权/拒绝、类别禁用、熄屏、省电、系统停止、默认电脑切换、通知停止和耗电. 官方服务类型说明: https://developer.android.com/develop/background-work/services/fgs/service-types#connected-device.

2026-10-22 本地验证: 169 项 JVM 单测通过, lint 0 errors/8 warnings, debug APK 构建成功. 新增用例覆盖后台预占/取消/Mutex 释放、只读与操作历史保留、全局 busy/request/notice 隔离、手机单调时间有效期、重启/切换/失败失效、Tile 过期展示及退避停止策略. 此轮未安装到手机, 未控制实体显示器, 前台服务和 OriginOS 长时后台行为仍待上述实机验收.

App 与 Tile 使用同一进程内单例 DeviceRepository / Mutex，不创建独立网络路径。配对、配置修改、默认设备切换、请求之间串行，防止 UI/Tile 并发重复操作。set 在发包前持久化 Pending；保存失败则不发送。请求中的设备和 target 在操作期间固定，不跟随后收到的 body 改变。

`Confirmed` 才展示操作确认。`AcceptedUnconfirmed`、`Pending`、`Unknown` 分别明确展示“已接受未确认 / 处理中 / 结果未知”。旧 on/off 观测不会在不确定操作之后继续作为成功证据。查询未找到记录也不表示未执行。显式成功的实时 refresh 或对应的已确认观测能够解除“不确定后需刷新”标记; 确定未发出的失败或可信终态 `Failed` 也不保留该标记, 包括恢复旧版持久化记录. 设备在线且绑定有效时, Unknown 可明确 ON/OFF, 仍不能 Toggle. 不会比较手机与 Windows 的时钟来猜测缓存是否足够新。实时刷新恢复当前状态不把此前 Unknown 操作改写为 Confirmed。

首页进入或回到前台, 已完成的开关请求结果不确定, 以及 Tile 开始 listening 时, 自动执行只读恢复. 有未决操作则先查询原 operationId, 当前状态仍未知时继续 live refresh; 临时未知最多读取 2 次, 安全/授权/协议错误立即停止. 恢复与 UI/Tile 操作共用原有 Mutex 串行执行, 等待空闲最多 30 秒, 切换设备或生命周期结束后不继续后续读取. 刷新或查询期间显示进度, 持续失败后才展示真实错误或待确认提示.

Tile 为 non-active 模式, onStartListening 在缺少有效后台读回时发起短时只读恢复. 仅 listening 期间获取临时 qsTile 并 updateTile, 不长期持有 qsTile; 停止 listening/销毁时取消观察订阅与 Tile 自己的自动恢复 Job, 阻止其排队读取和后续重试, 不停止用户已开启的后台同步服务. 手动点击已开始的短请求仍可完成并持久化结果. 进程被系统结束后不重放 set: 重建时 Pending 转 Unknown, 保留操作查询 UUID, 缓存状态先视为未连接.

Tile: 已确认 On 映射 STATE_ACTIVE, 已确认 Off 映射 STATE_INACTIVE. 当前设备实时刷新期间使用可点击的 STATE_INACTIVE 并显示刷新提示, 该外观不表示已关闭; 最多排队一次点击, 同次刷新成功且返回可信 On/Off 后才执行一次 Toggle. 刷新失败/取消/超时, 切换设备或目标, 面板关闭/组件销毁均取消排队, 不使用旧缓存执行. 其他未知/离线/远端 busy/本地写操作映射 STATE_UNAVAILABLE. 长按通过 QS_TILE_PREFERENCES 打开 MainActivity. API 34+ 展开 App 使用 PendingIntent; 不设置会绕过 onClick 的固定 activityLaunchForClick, 因此正常单击仍执行切换.

锁屏点击先 unlockAndRun; 解锁回调到达, 仍为同一默认设备/目标/请求且状态允许操作时, 才发送 Toggle. 解锁前不占用 repository busy; 取消解锁没有任何写命令, 也不会卡 busy. 解锁回调一次性使用, 30 秒后丢弃; 非刷新点击允许解锁导致面板收起后再回调, 但组件销毁或新一轮 listening 后不发送. 刷新排队仅在同一 listening 生命周期内有效, 刷新先于解锁完成时等待同次解锁授权, 面板关闭则取消.

## 验证边界及 OriginOS 必测

2026-10-05 已使用实际 Android HttpsTransport/Protocol/Router（JVM）连接 Windows loopback HTTPS + 模拟 Worker，完成一次性配对、刷新、OFF、跨 requestId 的同 operationId 去重、ON 恢复、错误 pin 拒绝。可用 tools/Test-Android-Live.ps1 重现。此项不涉及安装 APK、Android Keystore 实机或系统 Tile 生命周期。

直连总请求预算为 15 秒，Relay 为 12 秒，均受原请求 30 秒截止时间约束；TCP 连接上限仍为 5 秒。原因是已测 Windows 冷启动发现约 5 秒，不能在刚开始能返回结果时主动超时并反复取消 Worker。

Direct/Tailscale 的 pinned TLS 客户端按规范化 HTTPS 地址和 certificatePin 复用, 最多保留 64 个最近使用的客户端. 连续状态请求可复用已认证连接, 避免每次重建 TLS 配置及握手; token 仍从当前配置逐次写入请求头, pin 或地址变化使用独立客户端, Relay 保留系统 TLS. 路径冷却结束后的原始 status 请求直接承担只读恢复探测并返回结果, 避免 probe 成功后再发一次 status; 实时 refresh 和 set 仍保留独立只读探测及原请求预算, 不用缓存状态替代硬件读回.

常规 54 项 JVM 测试采用 fixture JSON、内存 store、fake transport、JDK 公共证书/有效期失败替身，不访问真实 Windows、Relay 或手机。覆盖字段/枚举、响应身份、跨路由同 ID、过期、并发/取消、防误报、配置隔离、恢复、不重放、TLS pin/有效期、IP/DNS SAN 差异约束和通道鉴权分离。默认 `testDebugUnitTest` 排除下面的 live 类，不以 skipped/假通过充数，即使已设置 fixture 环境变量也不会运行 live 测试。

### 显式 JVM / Windows HTTPS 集成测试

独立任务 `:app:liveWindowsTest` 只运行 `com.fy1ng.fy1switch.integration.LiveWindowsIntegrationTest`，复用 debug JVM 测试编译产物及依赖，但不执行常规测试。它调用实际 HttpsTransport、Protocol、Router，不 mock 网络；Windows 端必须使用**独立 loopback HTTPS 服务和模拟显示器**，初始状态 On，不应连接真实硬件。

由 Windows fixture 提供者创建一个临时 UTF-8 JSON 文件（允许 BOM）：`{"bundle":{...完整 PairBundle...},"targetId":"...UUID..."}`。bundle 必须含 version 1、deviceId、endpoint、certificatePin、一次性 code、有效 expiresAt。endpoint 仅接受 HTTPS 的 localhost、127.0.0.1 或 ::1 根地址及端口；不得提供正式设备配置。代码不打印 token、code、响应 body 或底层异常原文。

仓库根目录下显式运行：

    $env:FY1SWITCH_ANDROID_FIXTURE = Join-Path $env:TEMP 'fy1switch-android-fixture.json'
    .\tools\Build-Android.ps1 -LiveWindowsTest -Offline

也可设置好 JAVA_HOME / ANDROID_HOME 后，直接用 Gradle 8.13：

    & '<Gradle 8.13 路径>\bin\gradle.bat' -p src/android --no-daemon --console=plain :app:liveWindowsTest --offline

显式任务缺少 FY1SWITCH_ANDROID_FIXTURE 会失败；文件不存在、bundle 过期、非 loopback、配对失败或任一断言失败同样失败。任务禁止 up-to-date/build-cache 复用；**每次运行需重新导出有效一次性 code**，不能复用已消费的 fixture。

测试顺序：实际 pair → refresh 确认指定 device/target 为 On → set Off 并确认 operation → 保持 operationId、另建 requestId 与有效 expiresAt 再发 Off → 断言 operation.updatedAt 与第一次完全相同 → set On 并 refresh 确认 → 改错 pin 后的请求必须返回 TLS Security 失败。若配对后的流程在确认恢复 On 前失败，finally 用有效 pin 尝试恢复 On 并实时确认；清理失败会明确报错，不能把“已尽力恢复”当成 On 已确认。正常结束保持 On。

独立报告：`src/android/app/build/reports/tests/liveWindowsTest/index.html` 和 `src/android/app/build/test-results/liveWindowsTest/`。真实 fixture 的成功结果应由实际运行者报告；入口编译通过或“缺 env 正确失败”不算端到端通过。本任务不改变 503/504 的路由策略，也不覆盖 Relay 或 Android Keystore/OEM 行为。

### OriginOS 实机

**OriginOS 实机清单（本次不运行 adb、不安装）：**

- 手动安装 debug APK；添加开关、长按入口、API 33 添加请求、API 34+ PendingIntent 启动是否符合系统行为；升级后组件位置保留。
- 锁屏开/关分别验证：成功解锁仅一次 Toggle；取消解锁、连续点按、快速收起面板再展开不发额外命令、不遗留 busy。
- 通知中心/快捷设置的不同展开方式、状态图标/字幕、深色/大字体/窄屏可读性；unknown/unavailable 不可点击时仍可长按或重新展开恢复。
- Windows 自签证书正确 pin 成功，错 pin、过期/尚未生效证书失败；真实 IP / Tailscale MagicDNS 的 SAN 差异仅在同配对 host、同 pin 且有效证书时可用；手机时间偏差、Windows 更换证书后重新配对。
- 局域网直连 → Wi-Fi/移动网络切换 → Relay 回退；检查两个通道同 operationId 仅执行一次；断网/Relay 故障/401/超时保持 unknown 并可查询。
- 默认关闭后台同步时, 验证灭屏、后台限制、省电模式、清理进程、系统重启后重新展开的短任务恢复. 开启后台同步时, 另验证前台服务通知、暂停/停止和有效期回退; 观察系统是否限制或终止服务, 不把构建通过当作后台长期存活验收.
- 操作发送后立即杀进程，再打开仍有原操作查询；结果未确认时不亮成“已开启”；实时 DDC 观测与显示器实际画面/电源状态分别验收。
- 多设备切换时 App 和 Tile 只作用于当前默认设备；重新配对、Relay 凭据变更、卸载重装/迁移、不备份、Keystore 失效路径正确。

实际显示器面板反应、OEM 解锁/后台行为以及 Windows/Relay 联调均未以本地构建成功代替验证。

### 2026-10-06 Android 日常主页与扫码入口

- 主页改为顶部当前设备选择/设置、大状态和单主按钮：已知 On/Off 发送明确 Off/On，Unknown 主操作为刷新；安全条件允许时，详情保留明确 ON/OFF。统一为一次实时刷新入口，打开页面/切换设备各刷新一次，不持续轮询；异常操作突出并可手动查询原 operationId，不重发命令。没有真实 lastRoute 来源，故不展示猜测的直连/中继标签。
- 跟随系统深浅色及 Android 12+ dynamic color；设置、历史正常操作详情收起。系统快捷开关添加结果区分已添加、成功、取消及不可用，配对后的非模态引导只主动展示一次，设置中仍可手动添加。
- 使用 JourneyApps ZXing Embedded 4.3.0 离线扫描 PairBundle，仅点扫码时请求相机权限；拒绝/无相机可使用高级备用 JSON 粘贴。只解析既有协议并交给 repository.pair，再验 pin/有效期；不打开二维码网址、不执行电源操作，不保存扫码图片。包名、Tile 组件、encrypted store、配对协议及 Repository Mutex 未改变，不需要旧用户重新配对。未实现短配对码或 LAN 发现。
- 新增纯 UI 状态测试及二维码输入/离线编解码测试。构建入口仍为 tools/Build-Android.ps1（testDebugUnitTest、lintDebug、assembleDebug）；未运行 liveWindowsTest、adb 安装或真实 Windows 硬件控制。lint 通过不代表零警告；相机授权/旋转、OEM Tile 回调、深浅色/大字体/窄屏与真实配对仍需真机验收。

### 2026-10-17 Android 交互精简

- 主页、设备设置、连接电脑分别展示, 顶部电脑名称打开设备列表, 列表末尾添加电脑. 主页保留大状态和一个主操作: 已知状态发送明确 On/Off, 离线重新连接, 未确认操作优先查询原 operationId. 刷新当前状态仍可单独执行, 有效绑定下的 Unknown 保留明确开启/关闭入口, 不生成未知状态下的 Toggle.
- 进入主页或切换电脑时进行一次读取; 若之前存在需确认的操作, 优先查询其结果. 不持续轮询, 不重发不确定的开关命令. 连接路径、时间和操作编号移至详情, 合并重复的状态提示.
- 设置默认只显示电脑名称和快捷开关入口; Tailscale、Relay 地址及凭据收在连接设置中. 离开未保存的编辑时可继续编辑或放弃, 凭据不放入 saveable 状态, 保留原有加密存储和旧配对兼容.
- 连接电脑页以扫码为首选, 二维码读取后仍需明确确认并验证证书. 附近电脑/双向短码和 JSON 粘贴默认折叠; 仅附近入口展开且页面在前台时运行局域网发现, 收起、离页、后台或扫码时停止. 电脑列表默认不显示 IP 和端口.
- 统一绘制操作图标、圆角和间距, 跟随系统深浅色及 Android 12+ 动态配色. 未更改包名、Tile 组件、网络协议、设备身份或服务端配置; 本次不自动安装 APK, 不执行真机或公网开关命令.

### 2026-10-21 Relay 设置保存校验

- 保存连接设置仅校验和持久化本机数据, 不访问电脑或 Relay, 不要求同一局域网, 也不检查配对码有效期. 首次添加或更换 Relay 地址时填写手机使用的控制端凭据; 已有地址留空凭据则保留原值.
- 同一 HTTPS 地址仅大小写、根路径末尾斜线或默认端口 `:443` 的写法变化时, 可以保留已有凭据. 更换主机或实际端口仍必须重新填写凭据, 不放宽 TLS、来源或鉴权边界.
- 保存错误分别提示电脑名称、Relay 地址、缺少或无效的控制端凭据、误用设备配对 token、Tailscale 地址或设备记录已变化, 不再混用配对码过期提示. 已保存数据在校验失败时保持不变.
