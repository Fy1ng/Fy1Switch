# LAN short-code pairing v1

Approved 2026-10-06: six decimal digits, original UI/QR direction retained. This protocol is new application integration of Bouncy Castle SRP-6a, not a claim of external cryptographic audit.

## Scope and roles

UI update (2026-10-18): Windows displays the code and Android accepts it in “添加电脑”. The reverse entry has been removed from both interfaces; the wire protocol and existing credentials remain compatible. Android shows input digits and submits one attempt when six digits are entered. Failure requires editing or an explicit retry. The open Windows window renews natural expiry only, preserving the shared process admission limit; it never renews through an active handshake, failed authentication, exhausted attempts, stop, close or lock.

mDNS DNS-SD service type _rds-pair._tcp; TXT v=1, role=windows|android, sid=lowercase UUID. Service display name is untrusted. Advertise only while pairing page/session is active. Discovery never establishes trust. Windows chooses a LAN interface; Android is foreground only, closes on background/lock/page exit. No bearer, code or certificate pin in mDNS. Both directions use TCP, port from DNS-SD, same protocol. Showing-code device is SRP server; input-code device is SRP client. Only opposite platforms accepted. Codes 000000..999999 uniformly random, preserving zeros; valid at most 5 minutes, successful SRP proof consumes the code. Five total admitted handshakes per code, not per IP. In-memory device-wide limit 10 admissions/10 minutes survives code renewal within the process, outbound attempts use same limit. At most one active handshake; connect/handshake 15 seconds, total pairing 45 seconds; frames <=16KiB UTF-8 JSON newline terminated. No network or hardware control commands accepted here.

## Cryptography and exact wire

Bouncy Castle SRP6Client/SRP6Server, RFC5054 3072 group, SHA256 only, no negotiation. New salt 32 bytes and ephemeral exponent for every admitted handshake. UTF8 password = exactly six ASCII digits. SRP identity = UTF8("RDS-Pair-v1/" + serverSession). A and B are unsigned big-endian exactly 384 bytes, Base64 canonical; reject zero or >=N. Salt=32 bytes; evidence M1/M2 are unsigned big-endian exactly 32 bytes, canonical Base64. Session IDs lowercase canonical UUIDs. JSON lower camel names. Unknown message types/role changes/IDs/version errors fail closed. Server session is from advertisement or manual entry; never silently replace the selected session.

1. client -> server: {type:"hello",version:1,clientSession,serverSession,clientRole,A} (wire field is lowercase a).
2. server -> client: {type:"challenge",version:1,clientSession,serverSession,serverRole,salt,b}.
3. client -> server: {type:"proof",version:1,clientSession,serverSession,m1}.
4. server -> client: {type:"verified",version:1,clientSession,serverSession,m2} only after VerifyClientEvidenceMessage success. Client must verify M2 before any payload.

Use BC CalculateClientEvidenceMessage/VerifyClientEvidenceMessage and CalculateServerEvidenceMessage/VerifyServerEvidenceMessage conventions, not a different SRP proof formula. CalculateSessionKey is converted to exactly 32 unsigned big-endian bytes (leading zero padding).

Transcript UTF8 consists of these exact values joined with LF and NO trailing LF:
RDS-Pair-v1, clientSession, serverSession, clientRole, serverRole, salt(base64), a(base64), b(base64), m1(base64), m2(base64).
transcriptHash=SHA256(transcript). Encryption key=HKDF-SHA256(IKM=BC session key 32 bytes, salt=transcriptHash, info=UTF8("RDS-Pair-v1/encryption"), length=32). AES-256-GCM, random 12-byte nonce, 16-byte tag. Encrypted frame {type:"encrypted",version:1,purpose:"windows-bundle"|"android-result",nonce:base64,ciphertext:base64,tag:base64}. AAD=transcriptHash concatenated with UTF8(purpose). Reject unexpected purpose; only one payload of each purpose per session.

5. Windows (regardless of SRP role) -> Android: encrypted purpose windows-bundle, plaintext existing PairBundle JSON {version,deviceId,endpoint,certificatePin,code,expiresAt}. Windows obtains it from its own DeviceSecurity.BeginPairing after SRP authentication, not from advertised identity. The code in this payload remains existing 192-bit HTTPS bootstrap, never the six digits. Android uses original strict pin and /v1/pair logic. Pairing must succeed before ack; no trust-all TLS.
6. Android -> Windows: encrypted purpose android-result, plaintext {success:true|false}. Windows only shows completed upon authenticated success. No bearer token transmitted back. Consume original short-code session on valid SRP proof even if final HTTPS operation fails; new explicit pairing required. TCP disconnect is not proof of success.

Authenticates Windows pin and both role/session transcripts without trusting discovery metadata. A transparent relay/denial-of-service is not prevented. Device names are labels, not proofs. Local network isolation/firewall/multicast limits remain. Production code must not log six digits, proofs/keys, bundle, bearer or raw payloads.

## Backward compatibility

Existing HTTPS QR pairing, per-phone tokens, Windows certificate, Android package/Tile and stored bindings unchanged. QR scanner continues to accept existing PairBundle. LAN short-code session never silently widens HTTPS listener or disables firewall. Bluetooth, internet-wide discovery, Tailscale multicast, background Android listener are out of scope.

## Required verification

Both platform roles cross-language, wrong six digits, leading zero, invalid A/B, wrong M1/M2, encrypted payload/tag/context tampering, expiry/attempt caps/one-shot, disconnection and lifecycle cleanup. Local fixtures use only simulated display/temporary credentials. OEM discovery/camera/network/lock lifecycle still requires actual device testing.

## 2026-10-06 verification

.NET pairing tests 50/50; native discovery metadata/lifecycle tests 30/30; remote compatibility/address-choice tests 103/103; Android unit tests 76/76. Both directions of actual C# to Android JVM TCP SRP and pinned HTTPS bootstrap passed using tools/Test-ShortCode-Live.ps1. Native Windows DnsService register/browse/deregister start-stop succeeded on the selected local LAN interface; this does not prove Android OEM discovery or router multicast behavior. Android build/lint succeeded with retained dependency/deprecation warnings. No APK install, firewall changes, existing credential migration or physical display writes were performed.
