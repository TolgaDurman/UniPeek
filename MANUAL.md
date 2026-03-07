# UniPeek — User Manual

Stream your Unity Game View live to your iOS or Android phone over Wi-Fi.

---

## What You Need

- A phone with the **UniPeek** companion app installed (iOS or Android)
- Both your PC/Mac and phone on the **same Wi-Fi network**
- Unity 2021.3 or newer

---

## Opening the UniPeek Window

Go to **Window > UniPeek > Open** in the Unity menu bar.

The UniPeek window can be docked anywhere in your editor layout like any other Unity panel.

---

## Connecting Your Phone

### Option 1 — QR Code (easiest)

1. Click **Start Streaming** in the UniPeek window.
2. A QR code appears in the window.
3. Open the UniPeek app on your phone and tap **Scan QR**.
4. Point the camera at the QR code.
5. The dot in the window turns green — you are connected.

### Option 2 — Auto-Discovery (mDNS)

UniPeek broadcasts its presence on the local network automatically.
In the UniPeek app, tap **Browse** and your machine name will appear in the list. Tap it to connect.

No QR code needed. Both devices must be on the same subnet.

### Option 3 — USB / ADB (Android only)

1. Enable **USB Debugging** on your Android device.
2. Connect it to your PC via USB cable.
3. Click **Start Streaming** — UniPeek will set up port forwarding automatically.
4. In the app, connect to **localhost** (no IP address needed).

This works even without Wi-Fi and gives the lowest possible latency.

### Option 4 — Reverse Connection

Use this when your phone cannot reach your PC (e.g. hotel Wi-Fi, corporate network with client isolation).

1. In the UniPeek app, switch the app to **Listen** mode.
2. In Unity, click **Start Streaming**, then expand the **Reverse Connection** section.
3. Enter your phone's IP address and tap **Connect to Phone**.

---

## The Editor Window at a Glance

| Area | What it does |
|---|---|
| Status dot | Grey = stopped, Amber = waiting for phone, Green = connected |
| **Start / Stop Streaming** button | Starts or stops the stream |
| QR code | Appears while waiting; disappears once connected |
| Stats bar | Shows live FPS and encode time (or WebRTC RTT when using Pro) |
| **Options** section | Editor name, Play Mode lock, capture method |
| **Connected Devices** list | Shows all currently connected phones |
| **Reverse Connection** panel | Manual outbound connection to a phone in Listen mode |
| **Docs** button | Opens this manual online |
| **Reset FW** button | Re-runs Windows Firewall setup if connections are failing |

---

## Options

### Editor Name

Sets the display name shown in the UniPeek app's device list. Defaults to your machine name.
Type a name and click **Set** to save it.

### Only Run in Play Mode

When **on**: streaming only runs while the Editor is in Play Mode. Entering Edit Mode automatically stops streaming.

When **off**: streaming runs all the time, even in Edit Mode. Handy for inspecting the editor camera without entering Play. Note that script recompilation will briefly disconnect the stream.

### Capture Method

| Method | Description |
|---|---|
| **Camera Render** | Renders the main camera directly. Works in Edit and Play Mode. |
| **Async GPU Readback** | Same render path but non-blocking — reduces CPU stall at the cost of ~1 frame extra latency. |

Switch between them at any time; the change takes effect on the next captured frame.

---

## Touch Input

When the phone sends a touch, UniPeek injects it into Unity's Input system so your game can respond to it as if a finger touched the screen.

Works with both the **new Input System** package and the older **Legacy Input Manager**.

You can also subscribe to touch events from your own scripts:

```csharp
using UniPeek;

void OnEnable()
{
    // Simple: fires for every touch with normalized position (x=0 left, y=0 top)
    UniPeekInput.OnTouch += pos => Debug.Log($"Touch at {pos}");

    // Detailed: fingerId, phase string, and normalized position
    UniPeekInput.OnTouchDetailed += (id, phase, pos) =>
        Debug.Log($"Finger {id} {phase} at {pos}");
}

void OnDisable()
{
    UniPeekInput.OnTouch         -= ...;
    UniPeekInput.OnTouchDetailed -= ...;
}
```

Touch overlays (semi-transparent circles) are drawn on the Game View automatically while touches are active.

---

## Gyroscope and Accelerometer

The phone continuously sends gyroscope (rotation rate) and accelerometer (gravity + motion) data to Unity. This is injected as virtual sensor devices — your game code reads `Input.gyro` or the new Input System's `AttitudeSensor` and `Accelerometer` devices as normal.

---

## Windows Firewall

On first launch, UniPeek asks for a one-time UAC (administrator) prompt to add a Windows Firewall rule allowing inbound connections on port **7777**. Without this, the phone cannot reach the editor.

If you declined the prompt or the rule was removed, click **Reset FW** in the toolbar and then **Start Streaming** again to re-run the setup.

If you prefer to add the rule manually, run this in an elevated PowerShell:

```powershell
New-NetFirewallRule -DisplayName "UniPeek" -Direction Inbound -Action Allow -Protocol TCP -LocalPort 7777 -Profile Any
```

---

## Free vs. Pro

The plugin itself has no limits. Streaming quality is controlled by the companion app tier.

| Feature | Free App | Pro App |
|---|---|---|
| 540p + 720p | Yes | Yes |
| 1080p | — | Yes |
| 60 fps cap | — | Yes |
| More than 1 device at once | — | Yes |

---

## Troubleshooting

| Symptom | Fix |
|---|---|
| QR code shows `127.0.0.1` | Your machine has no active Wi-Fi or Ethernet. Connect to the network first. |
| Phone can't find the host via Browse | Both must be on the same subnet. Some guest/corporate Wi-Fi blocks device-to-device traffic — try the QR or USB method instead. |
| Firewall prompt never appeared / connections fail | Click **Reset FW** in the toolbar, then **Start Streaming** again. |
| Stream is black or frozen | Make sure there is at least one camera in your scene. In Edit Mode, Camera.main must exist. |
| Touch events are not registering in UI | Enable the **Input System** package (`com.unity.inputsystem`) for reliable injection. |
| High latency or choppy video | Lower the quality setting in the app, or reduce resolution to 540p. |
| Stream drops on recompile | Enable **Only Run in Play Mode** to avoid interruptions from domain reloads. |

---

## Default Port

UniPeek listens on TCP port **7777**. Reverse connections use port **7778** (phone-side).
