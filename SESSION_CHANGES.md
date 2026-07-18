# BoatDashboard — Change Log & Honest State (session 2026-07-15)

This documents **everything changed this session**, the **real working state** of each feature
(verified, not assumed), the **known problems**, and how to build/run/undo. Written to be honest,
including the things that don't work and the things I broke.

---

## 0. TL;DR — what actually works vs. what doesn't

| Subsystem | State | Notes |
|---|---|---|
| Dashboard web server (port 8080) | ⚠️ **Works but periodically HANGS** | Root cause not fully proven; load reduction helped (10 min stable in last test). This is the #1 unsolved problem. |
| Lighting (iTach zones) | ✅ Control works / ⚠️ status is a guess | Buttons fire real iTach codes. On/off shown is optimistic (iTach gives no feedback) → **won't reliably match reality**. |
| AC Power / Batteries / Tanks | ✅ Real (when iTach connected) | From iTach telemetry poll. |
| Sonos amplifiers | ✅ **Works** (verified by readback) | Volume/mute/zone-select/link/source. Set 15 → speaker read 15. |
| NMEA 2000 engines + GPS + depth | ✅ **Live real data** | Real position (Ft Lauderdale), depth 2.6 m, port-engine data. Stbd engine silent when its ignition is off. |
| Samsung TV control | ❌ **DISABLED** | Local WebSocket control removed — it repeatedly HUNG the whole app. Wake-on-LAN still fires. See §5. |
| Navigation lights | ❌ **NOT wired** | UI toggle only. Sends nothing to hardware. Never integrated. |
| Electronics master switch | ❌ **NOT wired** | UI toggle only. |
| Cameras | ❌ No feeds | No cameras configured; showing demo tiles. |
| New UI redesign (`preview.html`) | 🟡 Renders, not wired | Display-only prototype served at `/preview.html`. No device control. |

**Honest summary:** genuinely functional = lights (control), Sonos amps, engine/nav data, and
iTach telemetry (AC/batteries/tanks). Not functional / not built = nav lights, electronics, cameras,
reliable TV control. The app also has an unresolved hang-under-load problem.

---

## 1. Environment & topology (as discovered this session)

- **Repo:** `C:\Users\User\BoatDashboard` — .NET 9 (`net9.0-windows`), WPF + WebView2. No .sln.
- **Build:** dotnet not on the tool PATH; use `"C:\Program Files\dotnet\dotnet.exe" build -c Release`.
- **Output exe:** `bin\Release\net9.0-windows\BoatDashboard.exe`
- **Dashboard URL:** `http://192.168.20.8:8080` (wired) or `http://192.168.20.106:8080` (Wi-Fi); `http://localhost:8080` on the PC. Served from `WebUI\app.html`.
- **Wi-Fi SSID:** `TENDERLAND AV`, gateway `192.168.20.1`.
- **PC interfaces:** Ethernet `192.168.20.8` (1 Gb), Ethernet 2 `192.168.0.104` (100 Mb, reaches iTach), Wi-Fi `192.168.20.106`. **The PC IP moves** between wired/Wi-Fi → the URL changes. Consider a DHCP reservation / static IP so the URL is stable.
- **iTach IP2SL (lights):** `192.168.0.100:4999`. Lives on the `192.168.0.x` wired subnet.
- **Sonos amps:** `192.168.20.119` SALON (RINCON_74CA604E602E01400), `192.168.20.19` MAINDECK (RINCON_74CA604E5FAA01400), `192.168.20.78` FLYBRIDGE (RINCON_74CA60E7CFFB01400).
- **Samsung TV:** model QN43Q60AAFXZA. Moved `192.168.20.107` (Wi-Fi) → `192.168.20.77` (wired, MAC `c0-23-8d-14-57-39`) during the session.
- **NMEA 2000:** PEAK **PCAN-USB** adapter (USB→CAN), VID_0C72&PID_000C.

---

## 2. Files changed

### `Ip2slClient.cs` (iTach / lights)
- **Light command timing:** was 3× with 250 ms gaps (~4 s/command). Now **2× with an 80 ms gap** (`SendCommandAsync`). Much faster; small reliability trade-off.
- **Reconnect hardening:** added a **4 s connect timeout** (was the OS ~21 s SYN timeout) so a dead/unplugged iTach fails fast; changed the catch to `catch (OperationCanceledException) when (_cts.IsCancellationRequested)` so a connect-timeout retries instead of killing the loop.

### `LocalServer.cs` (LAN HTTP server + remote-browser bridge)
- **`/api/av/control` is now fire-and-forget** (`Task.Run(ControlAsync)` + instant `{"ok":true}`) so a slow/off device can't stall the request. **(Note: this is a suspect in the hang — unbounded background tasks.)**
- **Remote telemetry poll interval** tuned (ended at **1500 ms**; had briefly been 500 ms which added load).
- A `/api/av/devices` poll for TV power sync was **added then removed** (TV disabled).

### `AvService.cs` (AV discovery + control) — most-changed file
- **Sonos SetVolume/SetMute 500 fix:** discovery matched service types by loose substring, so Sonos's `GroupRenderingControl` clobbered the real `RenderingControl` URL. Now matches `:RenderingControl:` / `:AVTransport:` exactly. **This is what made the amps controllable.**
- **`FromId()` self-heal:** builds a device straight from its `IP:protocol` id, so control works without a prior discovery scan.
- **Sonos grouping + source:** new actions `join` / `unjoin` (x-rincon grouping via `GetSonosUidAsync`) and `source` (`tv` = HDMI/optical `x-sonos-htastream`, `line` = `x-rincon-stream`).
- **`TvReachableAsync`** fast TCP probe.
- **Samsung persistent connection** (`_tvLinks`, `TrySendLiveAsync`, `KeepTvLinkAlive`) + a pile of token/name/boot-window logic — **all now bypassed because TV control is disabled** (see §5).
- **`PowerPollLoopAsync`** (TV power → UI) added, then **disabled** (was adding load).
- **⚠️ TV local control DISABLED:** `SamsungAsync` returns early after Wake-on-LAN for all key/app commands. The WebSocket code below that return is dead. Reason: it hung the whole app.

### `ShellWindow.xaml.cs` (WPF host)
- Wired the new **`N2kService`** (`_n2k`), sharing `NmeaService.NavState`.
- **Merged NMEA 2000 nav + engine data into the LAN telemetry** (`_lastNav`/`_lastEngines`) so the iPad's `/api/telemetry` includes it (previously it only reached the PC's WebView).
- Engine "running" now means `rpm > 50` (not "any data present").
- Added `OnPowerChanged` push for TV power sync (now moot).

### `WebUI/app.html` (the dashboard UI)
- **Owner Cabin TV** given a full remote (now inert — TV disabled).
- **Sonos amps:** zone selector (Salon/Maindeck/Flybridge), 🔗 LINK ALL / UNLINK, source buttons relabeled **TV / LINE**, amp "power-gate" removed so volume/mute work without turning on first.
- **Top nav:** removed the persistent pill strip; added a single **☰ MENU** button opening the existing full-screen section grid.
- TV power-state sync hook (moot).

### `N2kService.cs` — **NEW FILE** (NMEA 2000 decoder)
- P/Invokes **PCANBasic.dll**, opens PCAN-USB @ 250 kbit/s, reads raw CAN.
- Decodes PGNs: 127488/127489 (engine rapid/dynamic → RPM, coolant, oil, hours, alternator, fuel rate), 127250 heading, 128267 depth, 129025/129026 position + COG/SOG, 130306 wind, 127505/127508 tanks/battery (decoded to `N2kAux`, not yet shown on tiles).
- Fast-packet reassembly keyed by (source, PGN, sequence). "Not available" sentinels handled.
- **Graceful:** if PCANBasic.dll / adapter absent, logs once and idles.

---

## 3. System / environment changes made (outside the repo)

- **Windows Firewall:** removed **two auto-created inbound BLOCK rules** for `boatdashboard.exe`; added an **allow rule** for TCP 8080 scoped to LocalSubnet. (This is what let the iPad reach the dashboard.)
- **Installed PEAK PCAN-USB driver** silently (`PeakOemDrv.exe /exenoui /qn`). It placed `PCANBasic.dll` in `System32`/`SysWOW64`. Device now reads "working properly."
- **`C:\voms\`** holds Samsung pairing state: `av-tokens.json`, `av-samsung-names.json`, `av-macs.json`.
- **On the TV itself:** cleared its Device List (Settings → General → External Device Manager → Device Connection Manager → Device List) to un-jam its pairing service.

---

## 4. The app-hang problem (UNSOLVED — top priority)

**Symptom:** after some minutes under load, the HTTP server accepts connections but stops responding (HTTP timeouts), which kills *everything* routed through it (amps, lights control). Process stays alive; port stays listening.

**What I did (patches, not a proven fix):**
- Made TV control non-blocking, then fully disabled it.
- Removed the TV power-poll loop and device poll; slowed telemetry poll to 1.5 s.
- Result: **10 minutes stable** in the last observed window.

**Most likely real cause (not yet proven):** unbounded fire-and-forget `Task.Run` work (`/api/av/control`) and/or accept-loop tasks piling up → thread-pool starvation. **This needs a proper diagnosis (thread/task dump at hang time), not more load-trimming.**

---

## 5. The Samsung TV saga (why it's disabled)

Long story short: the app controls the TV over Samsung's **unofficial local WebSocket API**. This specific TV/firmware:
- Enforces **one control session** and refuses reconnects with a fixed **30-second timeout**.
- Provides its auth token only inside a `clients[]` list (I initially saved a rotating **session** token instead of a persistent one — a real bug I fixed).
- Poisons a controller **name** once it's been refused (why it used random names).

The reconnect churn against the single-session TV **hung the whole app**. Net result: local TV control is
**not reliably achievable** here and was **disabled** to protect the rest of the dashboard. Wake-on-LAN
still works (harmless UDP).

**The correct fix (not done):** use Samsung's official **SmartThings cloud API** (OAuth with a Samsung
account) — that's what the apps that "pair once and work forever" actually use. It's a separate integration.

---

## 6. Build / deploy / run

```sh
# build
cd C:\Users\User\BoatDashboard
"C:\Program Files\dotnet\dotnet.exe" build -c Release

# run
bin\Release\net9.0-windows\BoatDashboard.exe
```

**Build gotcha:** editing **only** `WebUI/app.html` (no .cs change) makes `dotnet build` skip the content
copy, so the running app serves the **old** page. After an HTML-only edit, copy it manually:
```sh
copy WebUI\app.html bin\Release\net9.0-windows\WebUI\app.html
```
The server serves files from disk per-request with `no-store`, so just reload the iPad — no restart needed.

---

## 7. How to undo / re-enable things

- **Re-enable TV control:** in `AvService.cs`, delete the early `return "Samsung: TV control is disabled…"`
  in `SamsungAsync` and re-enable `PowerPollLoopAsync` in `ScanLoopAsync`. (Not recommended until the hang
  and single-session problems are solved — ideally replace with SmartThings.)
- **Reset TV pairing:** stop app → `echo {} > C:\voms\av-tokens.json` and same for `av-samsung-names.json` → restart.
- **Revert light timing:** `Ip2slClient.SendCommandAsync` loop back to `for i<3 / Task.Delay(250)`.

---

## 8. Honest recommendations (priority order)

1. **Fix the server hang for real** — instrument, catch a hang, get a thread dump, fix the root cause
   (bound the fire-and-forget work). Everything else is unreliable until this is solved.
2. **Stabilize the PC's IP** (DHCP reservation / static) so the dashboard URL stops changing.
3. **Decide TV:** SmartThings integration, or drop TV control.
4. **Be explicit in the UI** about what's real vs. not-yet-wired (nav lights / electronics / cameras) so it
   doesn't look broken — or wire them (Scheiber E-Plex / camera feeds), which are real, un-started projects.
5. **Lighting status:** if the E-Plex bus can report actual light states, read them back so the UI matches
   reality instead of guessing.
