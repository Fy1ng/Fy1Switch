# Windows short-code pairing engine

Targets `net10.0-windows`, uses stable NuGet `BouncyCastle.Cryptography` **2.7.0**, and references shared Contracts only for `PairBundle`. Wire contract: `docs/Pairing-v1.md` at the repository root. No discovery, Windows configuration, hardware, HTTPS request, or certificate-management access is implemented here.

## Integration API

```csharp
await using var host = await PairingHost.StartAsync(
    selectedInterfaceAddress,
    cancellationToken => GetOwnWindowsPairBundleAsync(cancellationToken),
    cancellationToken: pageLifetimeToken);

// UI/discovery reads host.SessionId, host.Code, host.Port, host.ExpiresAt.
// Subscribe to host.StateChanged (Action<string>); marshal onto your UI thread.
bool completed = await host.Completion;

using var client = new PairingClient();
bool outboundCompleted = await client.ConnectAsync(
    androidAddress, androidPort, selectedServerSession, enteredSixDigits,
    cancellationToken => GetOwnWindowsPairBundleAsync(cancellationToken),
    pageLifetimeToken);
```

Both callbacks also have `Func<Task<PairBundle>>` overloads. Supply the existing Windows bootstrap bundle from the caller's own security service only; do not put the short code in it. Prefer the cancellation-token overload. Callback invocation happens after authenticated SRP, on the thread pool, under the remaining total deadline. A callback that ignores cancellation can continue its own work after pairing ends; the engine stops waiting, closes the socket and never sends that late result. Callbacks must avoid retaining or logging secrets.

`PairingHost.StartAsync` is a static factory, binds the exact supplied IP to TCP port zero, and returns the listening host. Its `localRole` parameter permits only `windows`: the production services always send Windows bundles, independent of their SRP role. `PairingClient` has no role parameter for the same reason. Android simulations use the public protocol APIs below. There is no background Windows service or listener widening.

`Completion`/`ConnectAsync` return true only after decrypting `android-result` with `success:true`. Rejection, missing/false/malformed result, disconnect, cancellation and timeout are never success. Malformed caller arguments throw before admission; network/protocol attempts return false. Host wrong-proof attempts remain retryable until the code/session caps or expiry; a successful proof permanently consumes that session even if bundle provision or Android HTTPS pairing fails. The host then closes the listening socket immediately, clears `Code`, and clears `SessionId` at terminal completion. Capture the advertised session before starting a connection; never replace it from peer messages.

Statuses: host `listening`, `authenticating`, `rejected`, `sending-bundle`, `awaiting-android`, `completed`, `failed`, `exhausted`, `expired`, `stopped`; client additionally `idle`, `connecting`, `cancelled`. Events run on the operation thread, are serialized, and contain no sensitive values. Keep handlers nonblocking and post UI updates asynchronously. Subscriber exceptions are suppressed. `Stop` and `Dispose` are terminal; create a new host/client for a fresh UI lifecycle. Host `DisposeAsync` also waits for admitted operations to finish cleaning up. Dispose after `Completion` to release the external cancellation registration.

## Admission and deadlines

- The default `PairingAttemptLimiter.Shared` is process-wide and shared across inbound/outbound services, including newly created hosts/clients. **Do not create a new limiter for every session in production.** Constructor injection is for isolated tests or an application-owned singleton.
- At most five admissions per server session and ten admissions per monotonic rolling ten minutes. Rejections and disconnects do not refund admission. Consumed session IDs and per-session counts are retained for the limiter lifetime so they cannot be reset/replayed by a new client instance.
- `PairingCodeLease` owns a uniformly generated, leading-zero-preserving six-digit code, maximum five-minute lifetime and one active admitted handshake. The shared limiter also rejects concurrent outbound attempts for the same server session. Busy inbound connections close immediately rather than waiting for the current attempt.
- Connect/handshake defaults to 15 seconds; the entire admitted operation defaults to 45 seconds, including callback and final result. `PairingTimeouts` permits only tighter bounds (used by local tests), never wider. Every read/write uses the remaining operation cancellation token. The independent host code-expiry timer stops an unauthenticated session. A proof already authenticated before expiry can finish within its existing total deadline.

## Protocol / JVM fixture API

- `SrpClientHandshake(serverSession, sixDigitCode, localRole, clientSession?)`: `Hello`, `AcceptChallenge(frame)` returns proof, `AcceptVerified(frame)` returns a channel.
- `SrpServerHandshake(hello, expectedServerSession, sixDigitCode, localRole)`: `Challenge`, `AcceptProof(frame)` returns `(Verified, Channel)`. Low-level handshakes are single-use, not thread-safe, and do not enforce network admission/expiry; production callers use Host/Client.
- `PairingTransport.ReadAsync/WriteAsync(Stream, ..., CancellationToken)` implements bounded newline JSON framing; callers of this low-level helper must supply a deadline. `PairingProtocol.Parse/Serialize`, `Decode`, `Padded` are available for cross-language fixtures.
- `PairingChannel.Transcript` exposes `PairingTranscript.ToUtf8()` and `.Hash()` with the exact LF-separated, no-trailing-LF contract. No negotiated or alternative evidence formula is used. `PairingChannel(sessionKey32, transcript, localRole)` supports controlled KDF/GCM fixture vectors without exposing live session keys from a handshake.
- `channel.Encrypt(bundle)` and `channel.Decrypt<PairBundle>(frame)` cover the Windows payload; `channel.Encrypt(new PairingResult(true))` and `channel.Decrypt<PairingResult>(frame)` cover the Android result. Each direction is one-shot; Android cannot send a result before receiving the bundle. Dispose both handshake and channel objects.

An Android JVM test can connect directly to `PairingHost.Port` using its captured `SessionId`/`Code`, or listen on loopback and accept `PairingClient.ConnectAsync`. `TcpTests.cs` demonstrates both complete roles, without real credentials or non-loopback traffic. Neither this library nor its tests perform Android's real pinned HTTPS bootstrap; an actual Android client must complete that operation before sending success.

## Crypto and memory limitations

SRP uses BC RFC5054_3072 and SHA256 with BC's evidence/session-key conventions. To emit A before receiving salt, the BC client subclass retains **one freshly generated exponent per handshake**: the initial credential call uses an empty salt only to produce A; the challenge recomputes x using the real salt and the same exponent before calculating the secret. No empty-salt secret or evidence is sent. Tests also exercise an unmodified BC server independently.

Evidence is first compared as padded 32-byte arrays using `CryptographicOperations.FixedTimeEquals`, then passed through the required BC `Verify*EvidenceMessage`. BC still internally uses `BigInteger.Equals`, and its big-integer operations are not claimed constant-time. HKDF-SHA256 and AES-256-GCM use .NET cryptographic implementations; no homemade primitives.

Owned password/key/plaintext byte arrays are zeroed, sockets closed, and BC secret references released on failure/disposal. Managed immutable strings and BC `BigInteger` backing storage cannot be reliably wiped through their public APIs; UI callers can also retain previously read codes. This is best-effort managed-memory cleanup, **not guaranteed process-memory erasure or an external cryptographic audit**. Do not log codes, proofs, transcript values, keys, bundles, tokens or raw frames.

## Verification

Run `dotnet test tests/Fy1Switch.Pairing.Tests/Fy1Switch.Pairing.Tests.csproj` from the repository root. Tests include pure role simulations, an independent BC-server handshake, canonical encodings, malformed A/B/evidence, wrong code and leading zeros, session/role/version checks, transcript/GCM tampering and replay, limits/expiry, both TCP roles, authenticated success/failure, disconnect/Stop, bounded reads, and reduced-bound handshake/total timeouts. `FixedTranscriptAndGcmVectorMatchesIndependentNodeOpenSsl` contains a reproducible synthetic transcript/KDF/AES vector generated independently with Node's crypto/OpenSSL (A=1, B=2 are fixture inputs, not a claimed real SRP exchange). Cross-language JVM/device verification and discovery/firewall behavior remain separate integration acceptance work.
