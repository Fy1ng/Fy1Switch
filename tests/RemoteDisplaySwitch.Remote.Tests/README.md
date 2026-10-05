# Remote 无硬件测试

运行环境：Windows、.NET 10 SDK、MSTest 3.8.3；项目显式引用 `Microsoft.AspNetCore.App`。

从仓库根目录运行：

```powershell
.\tools\Test-Remote.ps1
```

脚本使用锁定依赖还原，执行 Release 测试，并把 TRX 写入本目录下被 Git 忽略的 `TestResults/remote-tests.trx`。任一断言失败都会使脚本失败，不把已知产品缺陷标记为忽略或预期成功。

## 隔离与验证边界

- 每个 fixture 只使用新建的 `%TEMP%/RemoteDisplaySwitch.Remote.Tests-<随机 GUID>`；删除前核验绝对路径归属与 reparse point。不会读取已有设备配置。
- `FakeDesktopController` 只返回内存记录并记录调用；本地 discover/bind/unbind/startup/topology 方法若被触达，会先记录再抛错。不实例化实际桌面 controller，不启动 worker，不访问显示器、DDC、ADB 或真实 Relay。
- `TestOnlyEncodingProtector` 只绑定当前测试临时目录，使用带明确测试标记的 Base64 编码。**它不是生产加密，也不证明 DPAPI 的安全性**；只用于隔离的测试证书、假 Relay token 和序列化往返。
- 配对和 gateway 时间由可控时钟驱动，不等待五分钟或两分钟。并发测试用 task gate，不靠 sleep 制造竞争。
- HTTPS 使用真实 `HttpsControlServer`、Kestrel 和 `HttpClient`，只监听 IPv4 loopback。证书由当前 fixture 自行生成，不安装受信任证书，不使用用户证书或关闭 TLS 验证。
- 校验器核验请求端点、主机名/有效期和 RSA SPKI SHA-256 pin；只允许自签名链错误。错误 pin 必须真正进入证书回调并被拒绝，不能把 TCP EOF 等其他失败误算为 pin 校验成功。
- 临时端口经 `TcpListener(IPAddress.Loopback, 0)` 获取。释放探测 listener 与 Kestrel 接管之间仍有极小端口抢占窗口；启动冲突应如实失败，不伪装成业务断言通过。
- 不覆盖另一个代理负责的 Relay 路由；不声称通过真实硬件、真实客户端、互联网部署或生产加密验收。

## 覆盖

- `DeviceSecurityTests`：配对一次性及并发竞争、五分钟边界、错误 code、替换/取消、32 客户端上限、撤销与重载、无明文 bearer token、证书 pin 重载稳定。
- `RemoteGatewayTests`：认证优先、跨设备/空 ID、协议版本、截止时间及未来 120 秒边界、非法命令与本地管理隔离、set 同 operation ID 原样转交、operation 查询、八并发上限与槽位释放、取消和错误映射、状态投影的字段白名单。
- `HttpsControlServerTests`：真实 TLS 配对、带 token 的 status/refresh/set、撤销后 401、错误 pin、认证优先于 JSON 解析、无效 body、65536/65537 字节 Content-Length 与 chunked 边界、pair 第 11 次限流、配对重放。body 超限必须返回 400 或 413，且不能执行命令或消耗配对码。
- `RemoteSettingsTests`：端点/监听参数验证、唯一临时目录内设置持久化、禁用状态的 manager 行为；不连接 Relay。
- `TlsIdentityTests`：直接使用产品创建/重载的证书进行真实 loopback `SslStream` 服务端握手，提供 Kestrel 隐藏后的底层 TLS 故障证据。

## 2026-10-05 验证结果

最初的严格测试发现两项产品缺陷：Guid.Empty 的 set 字段未在网关拒绝，以及 Windows Schannel 不支持原证书的 ephemeral 私钥。这些失败已由主代理修复，未删除或放宽断言：私钥经 DPAPI 保存，再以当前用户 keyset 加载供 TLS 使用；target/operation 空 UUID 被拒绝。

随后修复分块 HTTP 传输的 64KiB 边界：按解码后的 body 长度限制，不能把 chunk framing 开销误算成业务体积。增加重复命令字段与 no-store 测试。最终 92/92 通过；错误 pin 实际触发校验拒绝，HTTPS 不是仅编译成功。

以上仍是独立 loopback 与模拟 controller 验证，不替代 OriginOS 真机、公网服务器或真实显示器验收。
