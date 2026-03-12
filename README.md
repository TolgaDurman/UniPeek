# UniPeek — Unity Editor Plugin

Stream the Unity Game View to the **UniPeek Flutter app** on your iOS or Android device in real-time over a local Wi-Fi network.

---

## Features

| Feature | Free | Pro (via app) |
|---|---|---|
| Stream Game View as JPEG over WebSocket | ✅ | ✅ |
| mDNS / DNS-SD auto-discovery | ✅ | ✅ |
| QR code pairing | ✅ | ✅ |
| Touch Input | ✅ | ✅ |
| Multi-Touch / gyro / accelerometer injection | - | ✅ |
| USB (ADB reverse) connection | - | ✅ | (Coming Soon)
| 540p + 720p streaming | ✅ | ✅ |
| 1080p streaming | — | ✅ |
| fps cap | ✅ | - |

> **Note:** The plugin itself never enforces Pro limits — those are controlled by the companion app tier.

---

## Unity Version Requirements

| Unity | Status |
|---|---|
| 2021 LTS (2021.3.x) | ✅ Supported |
| 2022 LTS (2022.3.x) | ✅ Supported |
| Unity 6 (6000.x) | ✅ Supported |
| 2020 and earlier | ⚠️ Not tested |

Requires **.NET Standard 2.1** API Compatibility Level (`Edit → Project Settings → Player → Other Settings → Api Compatibility Level`).

---

## Installation

### 1 — Install the plugin

Install the plugin via Unity Asset Store or git

### 2 — Open the window

```
Unity menu → Window → UniPeek → Open 
```
## Windows

>On first launch on Windows, UniPeek will prompt for a one-time UAC elevation to add a Windows Firewall inbound rule for TCP port 7777.

## MacOS & Linux

>No additional permissions are required.

---

## Quick-start: Pairing via QR Code

1. Open the **UniPeek** window (`Window → UniPeek`).
2. Click **▶ Start Streaming**.
   A QR code appears showing the local IP and port.
3. Open the **UniPeek** companion app on your phone.
4. Tap **Scan QR** and point the camera at the QR code.
5. The connection indicator in the Editor turns green; the phone now shows the live Game View.

---

## Quick-start: Pairing via mDNS (no QR)

The plugin broadcasts `_unipeek._tcp` on the local network using mDNS / DNS-SD (RFC 6762).
The companion app will discover the host automatically — just tap the machine name when it appears.

Both the Unity host and the phone must be on the **same Wi-Fi network** (or the same network segment).

---

## Quick-start: USB / ADB (Android) | (Coming soon)

1. Enable **USB Debugging** on your Android device.
2. Connect via USB.
3. Click **▶ Start Streaming** — UniPeek automatically runs
   `adb reverse tcp:7777 tcp:7777`
   so the app can reach the editor even if they're on different networks.
4. In the companion app, connect to `localhost:7777`.

---

## Settings Reference

| Setting | Options | Description |
|---|---|---|
| **Resolution** | 540p / 720p / 1080p* | Streaming resolution sent to the phone |
| **Quality** | Performance (50) / Balanced (75) / Quality (85) / Ultra (92) | JPEG compression quality |
| **FPS Cap** | 10 / 20 / 30 / 60* | Maximum capture + encode rate |
| **Stop on Play Mode** | On / Off | Auto-stops streaming when you enter Play Mode |
| **Stop on Focus Loss** | On / Off | Auto-stops when the Game View loses focus |

\* Pro tier required.

Settings are persisted in `EditorPrefs` and restored on next launch.

---

## Message Protocol

### Outgoing (Unity → Phone)
Every binary WebSocket message is one complete JPEG frame. No headers or framing.

### Incoming (Phone → Unity)

```json
{ "type": "config", "resolution": "1280x720", "quality": 75, "fps": 30 }
{ "type": "touch",  "phase": "began", "x": 0.47, "y": 0.63, "fingerId": 0 }
{ "type": "gyro",   "x": 0.1, "y": -0.3, "z": 0.05 }
{ "type": "accel",  "x": 0.0, "y": 0.9,  "z": 0.1  }
```

Touch `x`/`y` are normalised [0, 1]; `x=0` is the left edge, `y=0` is the **top** edge of the phone screen.

---

## Input Injection

### New Input System (recommended)

When the **Input System** package is installed, UniPeek creates virtual `Touchscreen` and `Accelerometer` devices and injects events via `InputSystem.QueueStateEvent`.

Ensure you have `com.unity.inputsystem` in your `Packages/manifest.json`.

### Legacy Input Manager

A best-effort reflection-based path is used to call Unity's internal `SimulateTouch`. This may not work across all Unity versions; switch to the new Input System for reliable injection.

---

## Performance Notes

| Resolution | Expected FPS |
|---|---|
| 540p | 60 fps stable |
| 720p | 50 – 60 fps |
| 1080p | 30 – 45 fps |

- **Main-thread budget:** < 2 ms per frame (capture + blit only).
- **JPEG encoding** runs entirely on a background thread via `Task.Run()`.
- The capture loop drops frames automatically when the encoder is still busy (back-pressure).

---

## Troubleshooting

| Problem | Solution |
|---|---|
| QR code shows `127.0.0.1` | Machine has no active Wi-Fi / Ethernet. Connect to the network first. |
| Phone can't find host via mDNS | Make sure both are on the same subnet. Some corporate Wi-Fi isolates clients. |
| Firewall rule prompt never appears | Click **Reset FW** in the UniPeek toolbar, then Start Streaming again. |
| Game View is black / null capture | Open a **Game** tab in the Editor and make sure it's visible (not behind other panels). |
| `websocket-sharp.dll` not found | Place the DLL inside `Assets/` and re-import. Check the Plugin import settings. |
| High encode latency | Lower Quality to Performance (50) or reduce Resolution. |

---

## License

UniPeek plugin source: **MIT**
QRCoder: **MIT** (https://github.com/codebude/QRCoder)
websocket-sharp: **MIT** (https://github.com/sta/websocket-sharp)
