# ⚓ Boat System Dashboard

A modern Windows (WPF / .NET 9) dashboard that **monitors and controls a yacht's
systems** — lighting, tank levels, batteries, and AC/shore power — by speaking
directly to the boat's **Global Caché iTach IP2SL** serial-to-IP gateway over TCP.

It also includes:

- 🤖 a built-in **Claude AI assistant** with full tool access to read every sensor and operate every light,
- 📡 **MQTT publishing** of all telemetry (for Home Assistant, Node-RED, Grafana, etc.),
- ⚙ a **settings page** for the Claude API key and MQTT broker.

> **Status:** working. All eight cabin/area lights, scene commands, tanks,
> batteries, and AC monitor are live. See [Light commands](#light-commands).

---

## 🛥 New UI — "Lagoon 630 MY" Vessel Monitor

The dashboard's interface has been reskinned to the **Lagoon 630 MY — Vessel Monitor**
design. It is hosted inside the WPF app in a **WebView2** control (`ShellWindow`) and fed
**live iTach telemetry**, so it matches the design exactly while showing real vessel data.

- **13 screens** in a left nav rail: Home, **Engines**, AC Power, Batteries, Tanks,
  Nav Lights, Lighting, Shades, **AV / Media**, Electronics, Automation, System, Settings.
- **Live data**: AC volts/amps/Hz, tank levels, battery/service bank and alarm state are
  read from the iTach and pushed into the page every second (demo values shown when no
  hardware is connected).
- **Engines** page: twin-diesel RPM, coolant, oil pressure, hours, fuel rate.
- **AV / Media** page: Salon TV lift (up/stop/down), amplifier (power/volume/mute/source),
  and a full TV remote.
- **Kiosk mode** (Settings → *Kiosk / Display*): full-screen over the taskbar, hidden
  cursor, always-on-top, optional launch-at-boot, and **locked** — the only way to exit is
  *Shut Down* with passcode **5577**.

`App.xaml` starts `ShellWindow`; the classic native dashboard (`MainWindow`, documented
below) remains in the repo but is no longer the startup window.

📖 **Full details:** [`docs/VESSEL_MONITOR_UI.md`](docs/VESSEL_MONITOR_UI.md)

---

## 🛰 On the boat's chartplotters (Simrad / B&G / Lowrance MFDs)

The dashboard advertises itself to the vessel's **MFDs** using the **Navico HTML5
Integration Protocol** (UDP multicast on `239.2.1.1`, ports 2053/2054). An app tile
appears on the MFD home page; tapping it opens the **full dashboard** inside the MFD's
built-in browser, and boat conditions (service-battery fault, low tanks) raise
**native MFD alarms** with a working Acknowledge.

The MFDs live on the `169.254.x.x` link-local backbone; the announcer selects that
interface by index and cache-busts every URL so the old MFD browser reloads fresh HTML.

📖 **Full write-up:** [`docs/MFD_INTEGRATION.md`](docs/MFD_INTEGRATION.md) — the complete
"how it talks to the MFD" reference.

---

## ✨ What else it does now

Beyond the core iTach dashboard, the app has grown these subsystems (all documented in
[`docs/ARCHITECTURE.md`](docs/ARCHITECTURE.md) §2):

| Area | Capability |
|---|---|
| **LAN web server** | `LocalServer` serves the UI + `/api/*` on **:8080** to any device on the boat (iPad, phone, MFD) — no app install. |
| **MFD integration** | `NavicoMfdService` — app tile + native alarms on Simrad/B&G/Lowrance. |
| **NMEA 2000** | `NmeaService` — real nav + engine data via a CAN gateway. |
| **AV control** | `AvService` — Samsung TV, amplifier, salon TV lift, ESPHome, Wake-on-LAN. |
| **Automation** | `AutomationService` — trigger→action rules that run without calling Claude. |
| **Voice** | `VoiceService` — fully offline speech-in / TTS-out. |
| **Vision** | `VisionService` — YOLOv8 camera object-detection "visual sensors". |
| **Cloud** | `MqttAgent` + `PairingService` publish the full sensor set to the VOMS cloud broker; `CloudflareTunnelService` exposes the dashboard over HTTPS. |
| **AI memory** | `MemoryStore` — the assistant remembers prefs/facts across restarts. |
| **PC health** | `PcStats` — onboard-PC CPU load + temperature on the Settings page. |

---

## 📚 Documentation index

| Doc | Contents |
|---|---|
| [`docs/ARCHITECTURE.md`](docs/ARCHITECTURE.md) | Every source file, threading model, end-to-end data flow, build/release. |
| [`docs/PROTOCOL.md`](docs/PROTOCOL.md) | iTach wire protocol — frames, command codes, capture method. |
| [`docs/MFD_INTEGRATION.md`](docs/MFD_INTEGRATION.md) | How the dashboard talks to the Simrad/Navico MFDs. |
| [`docs/VESSEL_MONITOR_UI.md`](docs/VESSEL_MONITOR_UI.md) | The Lagoon 630 Vessel Monitor HTML5 UI. |

---

## Screenshot / layout

```
┌──────────────────────────────────────────── ⚓ BOAT SYSTEM ───────────────────────────┐
│  ● Connected   │ ● MQTT live                                                      ⚙   │
│                                                                                        │
│  AC MONITOR                                   │  BATTERIES                             │
│  ┌────────┐ ┌────────┐ ┌──────────┐           │  Genset        13.0 V                  │
│  │Shore 1 │ │Shore 2 │ │Generator │           │  Port engine   13.2 V                  │
│  │ 223 V  │ │ 232 V  │ │  0 V OFF │           │  Stbd engine   13.1 V                  │
│  └────────┘ └────────┘ └──────────┘           │  Service        0.0 V                  │
│                                                │                                        │
│  TANKS                                         │  LIGHTING                              │
│  ┌──────────────┐ ┌──────────────┐            │  [ ALL ON ]  [ ALL OFF ]               │
│  │Fresh wtr port│ │Fresh wtr stbd│            │  ● Interior Courtesy        ON         │
│  │ 100% ███████ │ │  38% ███      │            │  ● Salon                    OFF        │
│  └──────────────┘ └──────────────┘            │  …                                     │
│  …fuel ×4…                                     │                                        │
│                                                │  AI ASSISTANT  (Claude)                │
│                                                │  ┌──────────────────────────────────┐ │
│                                                │  │ you: turn off all lights         │ │
│                                                │  │ claude: Done — all lights off.   │ │
│                                                │  └──────────────────────────────────┘ │
└────────────────────────────────────────────────────────────────────────────────────────┘
```

---

## Hardware

| Component | Detail |
|---|---|
| **Gateway** | Global Caché **iTach IP2SL** (IP ⇄ RS-232), firmware `710-1009-05` |
| Gateway IP | `192.168.0.100` |
| Web config | port `80` |
| iTach control API | port `4998` |
| **Serial passthrough** | port **`4999`** ← the dashboard uses this |
| Serial line | **57600 baud, no flow control, odd parity** (8-O-1) |
| Controller | Multi-channel digital-switching/monitoring unit on the RS-232 bus |
| iPad app | reference controller at `192.168.0.102` |

The IP2SL transparently bridges TCP `4999` ⇄ the RS-232 line. Anything written to
the socket is sent to the controller; the controller continuously streams telemetry
back to **any** connected client.

> ⚠️ The iTach command API on port `4998` answered **unauthenticated** and reported
> `NET … UNLOCKED`. Anyone on the LAN can reconfigure it — consider locking it and/or
> isolating it on its own VLAN.

---

## Protocol

See [`docs/PROTOCOL.md`](docs/PROTOCOL.md) for the full reverse-engineering write-up.
Summary below.

### Light commands

Commands are **4-byte little-endian** values written to `192.168.0.100:4999`
(sent 3× ~250 ms apart, like the reference app). Individual lights are **toggles**;
the scene commands are absolute.

| Control | Value | Bytes on wire |
|---|---|---|
| **All On** | `0x0600` | `00 06 00 00` |
| **All Off** | `0x0700` | `00 07 00 00` |
| Interior Courtesy | `0x0009` | `09 00 00 00` |
| Port Fwd Cabin | `0x0100` | `00 01 00 00` |
| Port Fwd Gangway | `0x0200` | `00 02 00 00` |
| Port Mid Cabin | `0x0300` | `00 03 00 00` |
| Galley | `0x0500` | `00 05 00 00` |
| Salon | `0x0800` | `00 08 00 00` |
| Stbd Fwd Cabin | `0x0900` | `00 09 00 00` |
| Stbd Aft Cabin | `0x0D00` | `00 0D 00 00` |
| Heartbeat (keepalive) | `0x00FF` | `FF 00 00 00` (every ~6 s) |

> The byte order matters: for every light except Interior Courtesy the significant
> byte is the **second** byte. Encoding the value as a naive `uint16` in the first
> byte sends the wrong command and the light silently ignores it.

### Telemetry

The controller streams ASCII frames on the same port:

```
<00:0000,0000,0082,0201,0084,0200,0083,0200,0000,0000,0064,0026,0400,FFDF,F000,77E5>
 └ch  └────────────────── 16 comma-separated hex fields ──────────────────┘
```

Channels `00`–`03` carry real data; `FE`/`FF` are framing artifacts. Trailing
fields 12–15 are status/framing constants.

| Reading | Channel · field | Conversion |
|---|---|---|
| Fresh water — port | `00` f10 | value = % |
| Fresh water — stbd | `00` f11 | value = % |
| Fuel — fwd port | `03` f2 | value = % |
| Fuel — fwd stbd | `03` f3 | value = % |
| Fuel — aft port | `03` f10 | value = % |
| Fuel — aft stbd | `03` f11 | value = % |
| Battery — Genset | `00` f2 | value ÷ 10 = V |
| Battery — Port engine | `00` f4 | value ÷ 10 = V |
| Battery — Stbd engine | `00` f6 | value ÷ 10 = V |
| Battery — Service | `00` f8 | value ÷ 10 = V |
| Shore 1 — V / A / Hz | `02` f0 / f1 / f2 | f2 ÷ 10 = Hz |
| Shore 2 — V / A / Hz | `02` f3 / f4 / f5 | f5 ÷ 10 = Hz |
| Generator — V / A / Hz | `02` f6 / f7 / f8 | f8 ÷ 10 = Hz |

---

## Build & run

Requires the **.NET 9 SDK** on Windows.

```bash
cd BoatDashboard
dotnet run
```

Or build a release executable:

```bash
dotnet publish -c Release -r win-x64 --self-contained false
```

On launch the app connects to `192.168.0.100:4999` and auto-reconnects if the link
drops. The connection/MQTT status pills are in the top-right; click **⚙** for settings.

---

## Settings

Open with the **⚙** button. Stored at `%AppData%\BoatDashboard\settings.json`
(this file is **not** committed and may contain secrets).

- **Claude API key** — enables the AI assistant.
- **MQTT** — enable + broker host/port/username/password/base topic.

---

## AI assistant

Powered by the official **Anthropic C# SDK** on `claude-opus-4-8`, using a manual
tool-use loop. Two tools give it full access to the boat:

- `get_status` → returns all tanks, batteries, and AC readings as JSON.
- `control_lights` → operates any light (all on/off + the eight individual lights).

Ask things like *"turn off all lights"*, *"what's my fuel?"*, *"any low batteries?"*,
*"switch on the galley light"*.

---

## MQTT

When enabled, all telemetry is published every 5 s as **retained** messages under
the configured base topic (default `boat`):

```
boat/tanks/fresh_water_port           100
boat/tanks/fuel_fwd_stbd              25
boat/batteries/genset                 13.0
boat/ac/shore_1/volts                 223
boat/ac/shore_1/amps                  10
boat/ac/shore_1/hz                    50.0
boat/status                           {"tanks":[…],"batteries":[…],"ac":[…]}
```

---

## Project structure

| File | Role |
|---|---|
| `Ip2slClient.cs` | TCP client: telemetry read loop, frame parser, command send (serialized writes), heartbeat, auto-reconnect |
| `ViewModels.cs` | MVVM view models (tanks, batteries, AC, lights, chat) + status/MQTT projections |
| `MainWindow.xaml(.cs)` | Dashboard UI + wiring (timer, clicks, settings, chat) |
| `Theme.xaml` | Dark theme: palette, card/button styles |
| `SettingsWindow.xaml(.cs)` | Claude key + MQTT configuration |
| `ClaudeAssistant.cs` | Anthropic SDK tool-use loop (`get_status`, `control_lights`) |
| `MqttPublisher.cs` | MQTTnet publisher |
| `Settings.cs` | Settings model + JSON persistence |

---

## Disclaimer

This software talks directly to a vessel's electrical/lighting control bus. It was
built by reverse-engineering one specific installation. **Use at your own risk.**
Verify behavior before relying on it, and never use it for anything safety-critical
(navigation lights, bilge, etc.).
