# Windows native LAN pairing discovery

Independent `net10.0-windows` library using Windows 10+ `dnsapi.dll`. No Host,
Pairing or Contracts dependency; no Bonjour, raw UDP broadcast, public-DNS
fallback, device connection, or listener creation.

```csharp
await using var discovery = new LanDiscovery(selectedInterfaceIndex);
discovery.CandidatesChanged += (_, _) => Render(discovery.Candidates);
await discovery.StartAsync(tcpBootstrapPort, lowercaseSessionUuid, displayName,
    selectedLocalAddress, pairingPageCancellation);
await discovery.StopAsync();
```

`selectedInterfaceIndex` is a nonzero `uint`, belonging to the address family of
`selectedLocalAddress`. The native adapter validates address ownership against
local interface metadata before registering. It passes that explicit index to
Register, Browse and Resolve; zero never means an implicit all-interface mode.
An index of zero in a resolved native instance inherits the explicitly scoped
query; a conflicting nonzero index is rejected.

Advertisements use `<displayName>._fy1switch-pair._tcp.local.` and exactly three TXT
properties: `v=1`, `role=windows`, `sid=<lowercase UUID>`. The port is the short-code
TCP bootstrap listener, **not HTTPS**. The caller supplies no code, PIN or token.
Display names must be nonempty single labels, at most 63 UTF-8 bytes, with no
control characters, dots or backslashes. Labels are untrusted, not identities.

Browse PTR notifications trigger asynchronous native `DnsServiceResolve` calls.
Only valid opposite-role Android instances enter `Candidates`. IPv4 sorts first;
IPv6 link-local addresses acquire the selected scope if absent. Candidates are
keyed by session ID, address (including scope) and interface index. PTR TTL expiry,
TTL-zero/delete notifications, per-instance revision and per-session generation
prevent expired/withdrawn/older results from reappearing. A one-second timer
cleans expired entries. At most 256 tracked instances and 256 pending resolutions
are admitted; each resolution requests cancellation after ten seconds.

`CandidatesChanged` is dispatched on the thread pool, not a UI thread, and carries
no historical snapshot: read the latest `Candidates`. Notifications can coalesce
semantically; subscribers must marshal UI work themselves. Subscriber exceptions
are isolated and recorded. `Diagnostics` retains up to 64 operation/status/message
records; native status is the API's returned DWORD, not a guessed last-error code.
`IsRunning` indicates an active session, not proof of LAN reachability.

Start/restart/stop/dispose serialize lifecycle changes. Cancellation ends the
pairing-page session. `StopAsync` and `DisposeAsync` are preferred; synchronous
`Stop`/`Dispose` also exist. Teardown cancels Browse and outstanding Resolve,
waits for completion callbacks, and deregisters the advertisement. It does not
free callback roots, request storage, or cancellation handles while native code
can still use them. Failed teardown is recorded and throws; a later Stop/Dispose
can retry retained leases. A native cancellation failure is reported immediately,
but awaiting the native terminal callback is deliberately not replaced by an
unsafe timeout/free. A malfunctioning OS DNS service can therefore delay shutdown.

## Native ABI references

- [Microsoft SDK declarations](https://github.com/microsoft/win32metadata/blob/main/generation/WinSDK/RecompiledIdlHeaders/um/WinDNS.h)
- [DNS record layouts and charset/delete flags](https://github.com/microsoft/win32metadata/blob/main/generation/WinSDK/RecompiledIdlHeaders/shared/windnsdef.h)
- [BrowseCancel terminal ERROR_CANCELLED callback](https://learn.microsoft.com/en-us/windows/win32/api/windns/nf-windns-dnsservicebrowsecancel)
- [DeRegister requires a null cancellation pointer](https://learn.microsoft.com/en-us/windows/win32/api/windns/nf-windns-dnsservicederegister)
- [Service instance layout](https://learn.microsoft.com/en-us/windows/win32/api/windns/ns-windns-dns_service_instance)

P/Invoke uses Winapi calling convention, sequential pointer-sized structs,
32-bit BOOL for register `unicastEnabled=FALSE`, Unicode service strings, host
order service port and copied address octets. Version 1 request/callback ABI is
used for Windows 10. Successful asynchronous submissions return
`DNS_REQUEST_PENDING=9506`; `ERROR_CANCELLED=1223` is terminal cancellation.
Browse record lists are released with `DnsRecordListFree(..., DnsFreeRecordList)`;
returned service instances transfer to managed operation ownership and are freed
with `DnsServiceFreeInstance` after consumption. Register input and returned
instance ownership are separate.

## Verification boundary

`IDnsSdAdapter` and optional `TimeProvider` are injectable. Tests use only fake
adapters, synthetic unmanaged metadata, deterministic TTL time and read-only DLL
export lookup. They do not invoke native Register/Browse/Resolve, send multicast,
open TCP listeners or connect to devices. Current local tests/build do not certify
Windows 10 device interoperability, DNS service callback/cancellation behavior,
multihomed routing, firewall rules or Android interoperability. Those require the
separately authorized loopback fixture and real-device tests.

Run: `dotnet test tests/Fy1Switch.Discovery.Tests/Fy1Switch.Discovery.Tests.csproj`
