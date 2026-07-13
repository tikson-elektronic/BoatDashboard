# Simrad / Navico MFD Integration

How BoatDashboard puts its full HTML5 dashboard **onto the boat's Simrad / B&G /
Lowrance chartplotters (MFDs)** and raises **native alarms** on them.

This is the "way the dashboard communicates with the MFD." It is implemented by
[`NavicoMfdService.cs`](../NavicoMfdService.cs), served by
[`LocalServer.cs`](../LocalServer.cs), and wired up in
[`ShellWindow.xaml.cs`](../ShellWindow.xaml.cs). It is **opt-in** via
`AppSettings.EnableNavicoMfd` (default **on**).

---

## 1. What the user sees on the chartplotter

1. An **app icon** ("Vessel Monitor", teal tile — `WebUI/uploads/mfd-icon.png`)
   appears on the MFD home page.
2. Tapping it opens the **full dashboard** inside the MFD's built-in HTML5 browser
   panel — the exact same UI as the iPad, fed live iTach telemetry.
3. Boat conditions (service-battery fault, low tanks) raise **native MFD alarm
   notifications**, with an **Acknowledge** that actually sticks.

---

## 2. Protocol overview — Navico HTML5 Integration (NOS 20.2 / 20.3)

The dashboard is a **client application** that advertises itself to the MFDs over
**UDP multicast**. There is no pairing handshake — the MFD listens on a well-known
multicast group and renders whatever valid announcements it hears.

| Item | Value |
|---|---|
| Multicast group | **`239.2.1.1`** |
| Client Application Link (CAL) port | **`2053`** |
| Alarms port | **`2054`** |
| Announce cadence | every **5 s** (both messages) |
| Payload | UTF-8 **JSON**, one datagram per message |
| `Source` / `FeatureName` | `VOMS` / `VesselMonitor` |
| Multicast TTL | `2` (link + one hop), loopback off |

> **Addresses, never names.** Every URL in every announcement uses a numeric IP.
> The MFD browser has no name resolution for the PC, so a hostname URL fails to load.

### 2a. Client Application Link (CAL) — port 2053

Advertises the app and the URL of the dashboard's own LAN web server. Example payload:

```json
{
  "Version": "1",
  "Source": "VOMS",
  "FeatureName": "VesselMonitor",
  "IP": "169.254.201.50",
  "Text":  [{ "Language": "en", "Name": "Vessel Monitor" }],
  "Image": "http://169.254.201.50:8080/uploads/mfd-icon.png?v=<ticks>",
  "Icon":  "http://169.254.201.50:8080/uploads/mfd-icon.png?v=<ticks>",
  "URL":   "http://169.254.201.50:8080/?v=<ticks>",
  "BrowserPanel": { "Enable": true, "ProgressBarEnable": false,
                    "MenuText": [{ "Language": "en", "Name": "Home" }] }
}
```

When the user taps the tile the MFD opens `URL` and appends its own query params
(`mfd_name`, `mfd_model`, `lang`, `mode`, `brand`) — the page uses `mode`/touch to
switch into the MFD-friendly layout (see §5).

### 2b. Alarms — port 2054

A rolling summary of active alarms plus a watchdog, so the MFD can show and clear
native notifications:

```json
{
  "Version": "1", "Source": "VOMS", "FeatureName": "VesselMonitor",
  "IP": "169.254.201.50",
  "WatchdogInterval": 20,
  "URL": "http://169.254.201.50:8080/",
  "TickCount": 1234,
  "Alarms": [ { "Type": "Important", "Count": 1, "New": 1, "NewTickCount": 1200 } ]
}
```

Alarm content is built by `ShellWindow.BuildMfdAlarms()` from **live iTach telemetry**:

| Condition | Alarm `Type` |
|---|---|
| Service bank fault — channel `00` f8 reads **≥ 5 V and < 22 V** | `Important` |
| Any monitored tank **< 15 %** (chans `00` f10/f11, `03` f2/f3/f10/f11) | `Warning` |

> **Disconnected ≠ dead.** The service-battery channel reads `0.0 V` with a
> `-3276.8 A` sentinel when the sensor is unwired — that is *no data*, not a flat
> battery, so it is deliberately **not** alarmed (the `≥ 5 V` floor). A real 24 V
> bank in fault still reads several volts.

---

## 3. The two hard-won details

These are the things that made it actually work on real hardware, and any change to
the announcement code must preserve them.

### 3a. Link-local (169.254.x.x) egress by interface **index**

Navico MFDs sit on the **169.254.x.x link-local (Zeroconf)** backbone, not a
`192.168.x` subnet. So:

- The PC is given a link-local IP on the Navico NIC (e.g. `169.254.201.50/16`).
- The service announces **only** the link-local URL(s) when it has one — a
  `192.168.x` URL is on a different L3 subnet the MFD can't route to and yields
  "Failed to load the page". With no link-local address it falls back to announcing
  every interface.
- The multicast socket is **left unbound** and the egress interface is chosen with
  `IP_MULTICAST_IF` **by interface index** (`InterfaceIndexForIp`). Binding the
  socket to a manually-added `169.254.x` unicast address fails on Windows with
  *"the requested address is not valid in its context."*

### 3b. Cache-busting `?v=<ticks>` and stable `NewTickCount`

- **The MFD browser caches aggressively** and ignores our `no-store` header, so a
  stale build kept rendering. Every advertised URL carries `?v=<app.html last-write
  ticks>`; changing the file changes `v`, so re-opening fetches fresh HTML.
- **`NewTickCount` must stay stable while an alarm persists.** Bumping it every 5 s
  made the MFD treat the same alarm as brand-new each cycle and re-alert forever, so
  Acknowledge never stuck. The service stamps the tick when an alarm *first* appears
  and reuses it; it drops it when the alarm clears so a genuine re-occurrence is new.
- **Acknowledge:** `NavicoMfdService.Acknowledge()` (triggered from the UI via the
  `alarm_ack` command) records `_ackTick`; alarms first raised at/before it report
  `New = 0`, so the MFD stops re-alerting while still showing them active. A newly
  raised alarm still alerts.

---

## 4. Network / OS prerequisites

| Requirement | How |
|---|---|
| PC on the Navico link-local wire | link-local IP on the Navico NIC, e.g. `netsh interface ipv4 add address "<NIC>" 169.254.201.50 255.255.0.0` (persists across reboots) |
| Inbound to the dashboard web server | Windows Firewall: allow inbound **TCP 8080**, all profiles |
| MFD can reach the icon + page | both are served by `LocalServer` on the same link-local IP:8080 |

> **⚠ Hardware note (2026-07-12):** the PC's primary Ethernet port failed. The boat
> network — the Navico link-local wire **and** the `192.168.0.x` iTach subnet — is
> currently carried on the spare NIC (`Ethernet 2`), with `192.168.0.240/24` added to
> it as a software failover. The MFD announcement selects its egress interface by
> index at runtime, so it follows whichever NIC holds the link-local address; no code
> change is needed, but the failover IP config is an **OS setting, not in this repo.**

---

## 5. Browser-panel quirks (what the page must respect)

The MFD's embedded browser is an **old Chromium**. `app.html` accommodates it:

- **No `color-mix()`** and **no shorthand `inset: 0`** — both are unsupported. A JS
  `mix()` helper computes tank colours; the tank liquid column uses explicit
  top/left/right/bottom. Any new CSS must avoid these two.
- **Touch → click bridge.** MFD browsers don't reliably synthesize `click` from a
  tap, so `app.html` installs a tap→click bridge.
- **MFD menu.** Touch clients (`window.__remote`) get a simple grid menu
  (`buildMfdMenu()`) instead of the desktop radial ring. (An always-visible nav strip
  for these clients is the current in-progress refinement.)
- **Fresh HTML propagation.** After editing `WebUI/app.html`, copy it to the output
  folder (`bin/Release/net9.0-windows/WebUI/app.html`) — the server serves from there
  — and the `?v=` cache-buster handles the MFD's stale cache on reload.

---

## 6. Files & call graph

```
ShellWindow.xaml.cs
  ├─ if (settings.EnableNavicoMfd)
  │     new NavicoMfdService(HttpPort, BuildMfdAlarms).Start()
  ├─ BuildMfdAlarms()  ← reads live iTach fields (Ip2slClient.Field)
  └─ HandleWebCommand("alarm_ack") → NavicoMfdService.Acknowledge()

NavicoMfdService.cs   (UDP multicast announcer, 5 s loop)
  ├─ LoopAsync → for each link-local IP: SendCal() + SendAlarms()
  ├─ AppVersion()  ← app.html mtime → ?v= cache-buster
  ├─ InterfaceIndexForIp()  ← IP_MULTICAST_IF egress selection
  └─ Acknowledge()  ← stable-tick alarm suppression

LocalServer.cs        (serves the page + icon the announcements point at)
  ├─ AllLocalIPv4()  ← interface list the announcer iterates
  └─ GET /  ,  GET /uploads/mfd-icon.png  ,  /api/telemetry  …
```

---

## 7. Verified working

Dashboard loads via the Navico HTML5 panel; tanks render (blue water / amber diesel);
external alarms raise natively; the false SERVICE-BATTERY alarm is suppressed; the
Acknowledge button sticks; the teal app tile shows on the MFD home page; and edits to
`app.html` propagate on reload via cache-busting. See also
[`VESSEL_MONITOR_UI.md`](VESSEL_MONITOR_UI.md) and [`PROTOCOL.md`](PROTOCOL.md).
