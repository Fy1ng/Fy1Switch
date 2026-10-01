# RemoteScreenSwitch / FyDisplayRemote

OriginOS/Android Quick Settings remote monitor power controller.

## Components

- `android/`: Kotlin Android app with Quick Settings `TileService`.
- `windows-agent/`: C#/.NET 8 Windows GUI + tray agent with built-in native DDC/CI monitor control.
- `relay-server/`: lightweight relay for use when direct Tailscale/LAN access is unavailable.

## Control flow

Direct/Tailscale:

`Android -> http://<tailscale-host>:27120 -> Windows Agent -> Windows DDC/CI -> monitor`

Relay fallback:

`Android -> HTTPS Relay -> long poll -> Windows Agent -> Windows DDC/CI -> monitor`

The Android `AUTO` mode tries Direct first and falls back to Relay.

## Native monitor control

The Windows agent no longer requires PowerToys. It uses the Windows Monitor Configuration API in `Dxva2.dll` directly:

- enumerate physical monitors with `GetPhysicalMonitorsFromHMONITOR`
- read power state with `GetVCPFeatureAndVCPFeatureReply`
- write power state with `SetVCPFeature`
- MCCS VCP code `0xD6` (Power Mode)
- `0x01` = On
- `0x04` = Off (DPM)

Enable **DDC/CI** in the monitor's OSD. Monitor support varies by model, cable, dock, adapter, and GPU driver, so real hardware validation is required.

The agent keeps the last successfully read/written power state. This allows `Toggle` to continue working when a monitor temporarily stops answering DDC/CI after entering Off/DPM.

## Windows agent

Download the latest `FyDisplayAgent-win-x64` artifact from GitHub Actions and run `FyDisplayAgent.exe`.

On first run it:

1. creates `%LOCALAPPDATA%\FyDisplayRemote\config.json`
2. generates a random Device Token
3. opens the settings window
4. scans for external DDC/CI monitors
5. keeps running in the notification area when the window is closed

Select the intended monitor, press **Save settings**, then test **Refresh status**, **Display OFF (DPM)** and **Display ON** locally before configuring remote control.

## Android

Configure:

- Direct URL: e.g. `http://100.90.80.70:27120`
- Relay URL: e.g. `https://display.example.com`
- Device ID: same as the Windows agent
- Token: same as the Windows agent
- Mode: AUTO / DIRECT / RELAY

Then add the FyDisplayRemote Quick Settings tile.

## Security

Every request uses a per-device bearer token. Direct HTTP is intended to run inside an encrypted Tailscale network. Public relay endpoints should always use HTTPS.

## Build

### Windows

Requires .NET 8 SDK on Windows:

```powershell
cd windows-agent
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true
```

### Android

Requires JDK 17+, Android SDK 35 and Gradle 8.9:

```bash
cd android
gradle :app:assembleDebug
```

GitHub Actions builds both Windows and Android artifacts automatically.
