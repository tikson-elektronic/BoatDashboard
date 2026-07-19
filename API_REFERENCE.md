# BoatDashboard HTTP API Reference

Everything a client app (e.g. a **Flutter** app) needs to talk to the boat. The BoatDashboard Windows
app hosts a plain **HTTP/1.1** server; there is no separate cloud backend. The existing `WebUI/app.html`
dashboard is just one client of this same API — a Flutter app is a peer, not a replacement.

> Source of truth: `LocalServer.cs` (routing + payloads), `AvService.cs` (AV control vocabulary),
> `ShellyMqttService.cs` (blinds/lift), `ShellWindow.xaml.cs` (`/api/telemetry` shape).
> This file is generated from that code; if an endpoint changes, update here.

---

## 1. Connecting

| | |
|---|---|
| **Base URL (LAN)** | `http://av-os.local:8080` (mDNS name — preferred) or `http://192.168.20.8:8080` |
| **Auto-discovery** | mDNS/Bonjour service `_avos._tcp` (see §9) — recommended so the app never hard-codes an IP |
| **Base URL (remote)** | the current `https://<random>.trycloudflare.com` (see below) |
| **Protocol** | HTTP/1.1, `Connection: close` per request (no keep-alive, no WebSocket for the API) |
| **Content type** | JSON in, JSON out (`application/json`). All response bodies are `no-store`. |
| **CORS** | none needed — same-origin for the web UI; native apps are unaffected by CORS anyway |

### Auth model (important — read this)
There are **two independent gates**, chosen by where the request comes from:

1. **LAN clients (e.g. an iPad/phone on `192.168.20.x`)** are gated by a **device allowlist**, not a
   password. The first time an unknown device connects it gets a "request access" page instead of the
   dashboard; the captain approves it on the Settings screen, and the device's MAC is remembered.
   - A Flutter app on the boat LAN must therefore be **approved once**. Until approved, every normal
     request returns the HTML request-access page. Drive the flow with:
     - `POST /api/request-access` `{"name":"Captain iPhone"}` → `{"ok":true}` (registers the ask)
     - `GET  /api/request-status` → `{"status":"pending|approved|denied|none"}` (poll until `approved`)
2. **Remote clients via the Cloudflare tunnel** arrive as **loopback** and are gated by **HTTP Basic
   Auth** (`voms` / `Lagoon630`). Send `Authorization: Basic base64(voms:Lagoon630)` on every request.
   - Note: because the tunnel lands on loopback, **`127.0.0.1:8080` also requires Basic Auth**. Use the
     LAN IP `192.168.20.8:8080` for un-authenticated local development.

### The remote URL is ephemeral
The Cloudflare quick-tunnel URL **changes on every app launch/reboot**. A Flutter app that wants remote
access must not hard-code it. Either (a) use it only on the LAN, or (b) fetch the current URL out of band
(today it is only written to the app's `debug.log` as `[cloudflare] public URL: …` — there is **no API
endpoint that returns it yet**; if remote auto-discovery is needed, add one). A stable custom domain
(named tunnel) is planned but not set up.

---

## 2. Endpoint summary

| Method | Path | Purpose |
|---|---|---|
| GET  | `/api/telemetry` | **Primary poll.** All live boat data (AC, batteries, tanks, nav, engines, nav-light status). |
| POST | `/api/cmd` | Fire an iTach lighting/system command by code. |
| GET  | `/api/raw` | Debug: every iTach channel/field expanded to bits (read-only). |
| GET  | `/api/rawbytes?clear=1` | Debug: raw serial byte ring buffer (reverse-engineering only). |
| GET  | `/api/cameras` | List cameras (`{i,name}`) — never exposes raw URLs. |
| GET  | `/api/cam?i=N` | One JPEG snapshot from camera N (server-side proxied). |
| GET  | `/api/av/devices` | List AV devices (TVs, Sonos) + power/pairing/online state. |
| POST | `/api/av/control` | Control any AV device (volume, mute, keys, source, grouping…). |
| GET  | `/api/av/tvpower?ip=` | Live Samsung power state (`{"power":"on|off|…"}`). |
| GET  | `/api/sonos/now?ip=` | Live now-playing/source for one amp. |
| GET  | `/api/av/favorites?ip=` | "My Sonos" favorites for the music picker. |
| GET  | `/api/av/discover` | Re-run SSDP discovery, returns the device list. |
| POST | `/api/av/add` | Add a device manually by `{ip,protocol,name}`. |
| GET  | `/api/shelly/status` | Blinds/lift live state (position, moving, online, alarm). |
| POST | `/api/shelly/set` | Drive a blind/lift (open/close/stop/half/goto). |
| POST | `/api/shelly/timer` | Set a slot's travel time (seconds). |
| GET/POST | `/api/shelly/config` | Get/replace the motor config list. |
| POST | `/api/shelly/provision` | Point a Shelly at the boat broker `{ip,topic}`. |
| GET  | `/api/shelly/discover` | Find Shelly devices on the LAN. |
| GET  | `/api/spotify/status` | `{clientId,connected}`. |
| GET  | `/api/spotify/search?q=` | Search tracks/playlists. |
| GET  | `/api/spotify/now` / `/devices` | Now-playing / available Spotify Connect devices. |
| POST | `/api/spotify/play` | `{zone,uri,ctx}` play on an amp. |
| POST | `/api/spotify/control` | `{zone,action}` play/pause/next/prev. |
| GET/POST | `/api/scenes` | Get/save scenes (opaque JSON array). |
| POST | `/api/scene/run` | Run a scene `{id}`. |
| GET/POST | `/api/automations` | Get/save automations (opaque JSON array). |

Every `POST` action endpoint returns `{"ok":true}` immediately (fire-and-forget — see §7).

---

## 3. Telemetry — `GET /api/telemetry`

Poll this every ~1.5 s. Fields are always present; `nav`/`engines` may be `{}` until the first NMEA 2000
frame arrives (merge them as no-ops). Voltages are volts, currents amps, `hz` hertz, tanks/percent 0–100.

```jsonc
{
  "ac":       { "v": 230, "a": 12, "hz": 50.0 },          // active shore/inverter summary
  "acDetail": {
    "shore1":    { "v": 230, "a": 12, "hz": 50.0 },
    "shore2":    { "v": 0,   "a": 0,  "hz": 0.0 },
    "generator": { "v": 0,   "a": 0,  "hz": 0.0 },
    "inverter":  { "v": 230, "a": 3,  "hz": 50.0 }
  },
  "batteries": {
    "genset":     { "v": 12.7, "a": 0 },
    "portEngine": { "v": 13.1, "a": 0 },
    "stbdEngine": { "v": 12.6, "a": 0 },
    "service":    { "v": 25.9, "a": 0 }                    // 24V bank
  },
  "tanks": {
    "waterPort": 80, "waterStbd": 75,
    "fuelAftPort": 60, "fuelFwdPort": 55, "fuelAftStbd": 62, "fuelFwdStbd": 58
  },
  "fuelTransfer": { "toTransfer": 0, "toGo": 0 },
  "waterAvg": 77, "fuelAvg": 58,
  "service": { "v": "25.9", "low": false, "noData": false }, // low=fault (<22V), noData=sensor absent
  "alarm": false,                                             // service bank low
  "nav":     { /* NMEA 2000: lat, lon, heading, depth, sog, cog, wind… (empty {} before first frame) */ },
  "engines": { /* NMEA 2000: port/stbd rpm, hours, oilPressure, temps, volts… (empty {} before first frame) */ },
  "navStatus": { "anchor": false, "running": false, "electronics": true } // real iTach light status (field 12)
}
```

`navStatus` is the **only reliable light feedback** (anchor/running/electronics/deck circuits).
**Interior per-fixture light status is NOT readable** — do not build UI that claims to show it live.

---

## 4. Lighting & systems — `POST /api/cmd`

Body: `{ "cmd": "<name>", "code": <uint32> }`. Returns `{"ok":true}`. This sends a raw 4-byte frame to
the iTach. **Codes are physical and some are dangerous** — only send known-good ones:

| Function | code (decimal) | code (hex) |
|---|---|---|
| Anchor light | `3` | `0x03` |
| Running / nav lights | `1` | `0x01` |
| All lights ON | `1536` | `0x0600` |
| All lights OFF | `1792` | `0x0700` |
| Inverter | `1572864` | `0x180000` |
| Generator START | `3840` | `0x0F00` |
| Generator STOP | `4096` | `0x1000` |

> ⚠️ **Never send arbitrary/scanning codes.** A blind sweep of this code space once **started the
> generator.** Genset/inverter/lighting share the space. Read status back from `/api/telemetry`
> `navStatus` after a light command; there is no ACK beyond `{"ok":true}` (which only means "sent").

---

## 5. AV control — TVs & Sonos

### List devices — `GET /api/av/devices`
```jsonc
[{
  "id": "192.168.20.77:samsung",   // <ip>:<protocol> — use verbatim as the "id" in /api/av/control
  "name": "Owner Cabin TV", "ip": "192.168.20.77", "type": "tv",
  "manufacturer": "Samsung", "model": "QN43Q60AAFXZA", "protocol": "samsung",
  "online": true, "needsPairing": false, "accepted": true,
  "powerOn": true,          // real TV power (polled via :8001 REST)
  "booting": false          // true for ~35s after wake — do not spam control while true
}]
```

### Control — `POST /api/av/control`
Body: `{ "id": "<device id>", "action": "<action>", "value": "<optional>" }` → `{"ok":true}` instantly.

**Sonos (protocol `upnp`) actions:** `play`, `pause`, `stop`, `mute`, `unmute`,
`volume` (value `0`–`100`), `source` (value `tv`\|`line`), `join` (value = coordinator IP — group this
speaker under it), `unjoin`/`unlink` (break out to standalone), `playstream` (value = internet-radio URL),
`playfav` (value = favorite index from `/api/av/favorites`).

**Samsung TV (protocol `samsung`) actions** map to remote keys (send as `action`, no value):
`power` (WoL/toggle), `power_off`, `vol_up`, `vol_down`, `mute`, `home`, `up`, `down`, `left`, `right`,
`select`/`ok`, `back`, `exit`, `menu`, `guide`, `info`, `source`, `hdmi1`–`hdmi4`, `tv`,
`play`, `pause`, `stop`, `rewind`, `ff`, `ch_up`, `ch_down`, digits `0`–`9`, colour keys
`red`/`green`/`yellow`/`blue`. (Denon/Yamaha/Sony drivers also exist for future gear.)

Live readback: `GET /api/av/tvpower?ip=192.168.20.77` → `{"power":"on"}`;
`GET /api/sonos/now?ip=192.168.0.119` → now-playing/source JSON.

### Known devices
- **TVs:** Owner/Salon `192.168.20.77` (id `192.168.20.77:samsung`), Cabin `192.168.20.108`.
- **Sonos amps (protocol `upnp`, control port 1400):** SALON `192.168.0.119`, MAINDECK `192.168.0.19`,
  FLYBRIDGE `192.168.0.78`. **Amps are on the `192.168.0.x` subnet**, TVs on `192.168.20.x`.

---

## 6. Blinds & TV lift — Shelly

### Status — `GET /api/shelly/status`
```jsonc
[{
  "key": "tvlift",      // slot id: tvlift, tvswing, shadePort, shadeStbd, shadeFront, shadeAft, shadeSalon
  "online": true,
  "alarm": false,       // device dropped off the broker past the threshold
  "moving": "up",       // "up" | "down" | "" (idle)
  "pos": 40             // 0 = fully open, 100 = fully closed (time-tracked estimate)
}]
```

### Drive — `POST /api/shelly/set`
Body: `{ "key": "<slot>", "action": "<action>", "hold": <bool>, "target": <0-100> }` → `{"ok":true}`.
- `action`: `open`, `close`, `stop`, `half`, or `goto` (with `target` percent, 0=open 100=closed).
- `hold: true` = **momentary/dead-man** mode: the motor runs only while you keep sending the command;
  a heartbeat must arrive within ~1.5 s or it auto-stops (covers a lost release / dead tablet). Send the
  same command repeatedly while the user holds the button, and an explicit `{"action":"stop"}` on release.
- `hold` omitted/false = run to the limit (auto-stops via the travel timer + firmware `auto_off`).

### Config — `GET/POST /api/shelly/config`
`ShellyMotor` model (POST an array of these to replace the config):
```jsonc
{ "Key": "tvlift", "Name": "TV Lift", "Topic": "shelly2pmg4-abc123",
  "UpOut": 0, "DownOut": 1, "TravelSec": 20, "StartDelaySec": 0.5, "Ip": "192.168.20.x" }
```
`POST /api/shelly/timer` `{ "key":"tvlift", "secs":18 }` sets travel time (also pushes the firmware
auto-off backstop). `POST /api/shelly/provision` `{ "ip":"192.168.20.x", "topic":"…" }` points a fresh
Shelly at the boat's broker.

> All Shelly slots are on `192.168.20.x`. Each command fires an instant direct-HTTP RPC **plus** an MQTT
> publish (see `OPERATIONS.md` §6). The lift/blinds "run only 2 s" bug is fixed (widened watchdog).

---

## 7. Behaviour a client must handle

- **Fire-and-forget writes.** Control endpoints (`/api/av/control`, `/api/shelly/set`, `/api/cmd`,
  `/api/spotify/*`) return `{"ok":true}` **before** the device acts, so the response says nothing about
  success. **Update the UI optimistically and reconcile from the next status/telemetry poll**, exactly
  as the web UI does. This is deliberate: a slow/off device (a Samsung TV WS can hang ~45 s) must never
  stall the caller.
- **Poll for truth.** Drive UI state from `/api/telemetry` (~1.5 s), `/api/av/devices` (~3 s),
  `/api/shelly/status` (~5 s). Do not assume a command "stuck" from its own response.
- **`booting` / off devices.** Skip hammering a Samsung TV while `booting:true` (~35 s after wake).
- **Grouping/topology is eventually-consistent.** After a Sonos `join`/`unjoin`, wait a few seconds
  before trusting a re-read — the topology takes time to settle.
- **Cameras:** poll `GET /api/cam?i=N` for a JPEG (RTSP feeds are excluded — they need transcoding).

---

## 8. Suggested Flutter client shape

- A single `BoatApi` service holding the base URL + optional Basic-Auth header; swap the base URL for
  LAN vs tunnel.
- One polling loop per cadence above feeding a state store (Riverpod/Bloc/Provider). Model the telemetry
  JSON with `json_serializable`; treat `nav`/`engines` as nullable maps until populated.
- Command methods are thin `POST` calls that return void-ish (`{"ok":true}`); never block UI on them.
- Handle the **device-approval handshake** on first launch (LAN) and **Basic Auth** (tunnel).
- Do not hard-code the tunnel URL; do not send unknown `/api/cmd` codes.

---

## 9. Service discovery (mDNS/Bonjour) — how the Flutter app finds the boat

The PC advertises the dashboard over **mDNS** (Bonjour) so the app finds it automatically, with no
hard-coded IP. Two ways to use it, easiest first:

**A. Just use the name.** The host **`av-os.local`** always resolves to the dashboard on the boat LAN
(no browsing needed — iOS/macOS/Android/Windows all resolve `.local`). Point the app at
`http://av-os.local:8080`. This alone removes the IP problem.

**B. Browse for the service** (robust — survives an IP change and lets the app auto-configure). The
dashboard publishes:

| | |
|---|---|
| **Service type** | `_avos._tcp` (domain `local.`) |
| **Instance name** | `av-os` |
| **Host / port** | `av-os.local` : **8080** |
| **TXT records** | `path=/app.html`, `app=BoatDashboard`, `ver=1` |

Flow: browse `_avos._tcp` → resolve the instance → you get host `av-os.local`, port `8080`, the TXT
map, and the A-record IP `192.168.20.8`. Build the base URL from host+port and proceed with §1 auth.

### Flutter example (`bonsoir` — cross-platform)
```dart
import 'package:bonsoir/bonsoir.dart';

Future<String> discoverBoat() async {
  final discovery = BonsoirDiscovery(type: '_avos._tcp');
  await discovery.ready;
  final completer = Completer<String>();
  discovery.eventStream!.listen((event) {
    if (event.type == BonsoirDiscoveryEventType.discoveryServiceResolved) {
      final s = event.service!;                 // s.host, s.port, s.attributes (TXT)
      final host = s.host ?? 'av-os.local';
      completer.complete('http://$host:${s.port}');   // e.g. http://av-os.local:8080
    } else if (event.type == BonsoirDiscoveryEventType.discoveryServiceFound) {
      event.service!.resolve(discovery.serviceResolver); // must resolve to get host/port
    }
  });
  await discovery.start();
  return completer.future.timeout(const Duration(seconds: 5),
      onTimeout: () => 'http://av-os.local:8080');       // fallback to the name
}
```
(`multicast_dns` is a lighter pure-Dart alternative if you prefer no plugin.)

### Platform setup (required, or discovery silently returns nothing)
- **iOS 14+** — in `ios/Runner/Info.plist`:
  ```xml
  <key>NSLocalNetworkUsageDescription</key>
  <string>Finds the boat dashboard on the local network.</string>
  <key>NSBonjourServices</key>
  <array><string>_avos._tcp</string></array>
  ```
  Without both keys iOS blocks mDNS and the browse returns empty (this is the #1 gotcha).
- **Android** — no manifest change needed for NSD; if you later restrict, keep `INTERNET` +
  `CHANGE_WIFI_MULTICAST_STATE`. Discovery only works while on the boat's Wi-Fi.
- **Remote (off-boat via the Cloudflare tunnel)** — mDNS is LAN-only, so discovery won't work remotely;
  fall back to the tunnel URL there.

> Server side: advertised by Bonjour via the Windows scheduled task `AvOsMdns`
> (`dns-sd -P av-os _avos._tcp local 8080 av-os.local 192.168.20.8 …`). See `OPERATIONS.md`.
