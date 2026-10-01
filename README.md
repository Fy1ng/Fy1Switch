# FyDisplayRemote

OriginOS/Android Quick Settings remote monitor power controller.

MVP components:

- `android/`: Kotlin Android app with Quick Settings `TileService`.
- `windows-agent/`: Go Windows tray-less agent. Exposes a Tailscale/LAN HTTP API and can poll the relay server.
- `relay-server/`: dependency-free Go HTTP long-poll relay.

## Control flow

Direct/Tailscale:

`Android -> http://<tailscale-host>:27120 -> Windows Agent -> PowerToys.PowerDisplay.Cli.exe`

Relay fallback:

`Android -> HTTPS Relay -> long poll -> Windows Agent -> PowerToys CLI`

The Android `AUTO` mode tries Direct first and falls back to Relay.

## MVP security

Every request uses the same per-device bearer token. Direct HTTP is intended for an encrypted Tailscale network. Expose the relay only behind HTTPS (Caddy/Nginx/Cloudflare tunnel, etc.).

## Windows agent

Copy `windows-agent/config.example.json` to `config.json`, edit it, then run `fy-display-agent.exe -config config.json`.

The agent discovers `PowerToys.PowerDisplay.Cli.exe` from PATH or common PowerToys install locations. You can always set `powertoys_cli` explicitly.

## Relay

Set `FY_RELAY_LISTEN` if needed (default `:27121`), then run `fy-display-relay`.

The MVP relay is stateless except for in-memory command queues. Restarting it only drops in-flight requests.

## Android

Configure:

- Direct URL: e.g. `http://100.90.80.70:27120`
- Relay URL: e.g. `https://display.example.com`
- Device ID: same as Windows config
- Token: same as Windows config
- Mode: AUTO / DIRECT / RELAY

Then press **Add Quick Settings tile** and approve the system prompt.

## Build

### Go

```bash
cd windows-agent && go test ./... && GOOS=windows GOARCH=amd64 go build -o fy-display-agent.exe .
cd relay-server && go test ./... && go build -o fy-display-relay .
```

### Android

Requires JDK 17+, Android SDK 35 and Gradle 8.9:

```bash
cd android
gradle :app:assembleDebug
```

GitHub Actions is included to build all artifacts.

### Relay production token binding

For production, set a JSON map so Device IDs cannot be claimed by the first caller:

```bash
export FY_RELAY_DEVICE_TOKENS='{"fy-pc-main":"your-long-random-token"}'
./fy-display-relay-linux-amd64
```

If the variable is omitted, the MVP relay uses trust-on-first-use token binding for development only.
