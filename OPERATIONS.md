# BoatDashboard — Operations & Runbook

Yacht monitoring/control dashboard for a **Lagoon 630 motor yacht** ("630 MOTOR YACHT · VESSEL MONITOR").
This document is the field runbook: how the system is wired, how to run and deploy it, what has
broken recently, and — critically — **what must never be done again**. Read the "DO NOT DO" section
before touching device control code or probing the boat's hardware.

Last major update: 2026-07-19.

---

## 1. What this is (architecture)

- **App:** `.NET 9 / WPF` Windows desktop app (`BoatDashboard.csproj`, `net9.0-windows`, WinExe).
  No `.sln` — single project. Runs on the boat's onboard PC.
- **UI:** a single-page HTML5 dashboard (`WebUI/app.html`) rendered two ways:
  1. In-app via **WebView2** (the salon touchscreen), loaded through a `vessel.local` virtual host.
  2. Over the LAN / remotely via an **embedded HTTP server** on **port 8080** (iPad, phones).
- **Hardware bridges:**
  - **Global Caché iTach IP2SL** at `192.168.0.100:4999` — serial gateway to the boat's lighting/systems
    controller. The app speaks its native 4-byte little-endian command frames (`Ip2slClient`).
  - **NMEA 2000** via a **PEAK PCAN-USB** adapter (`N2kService.cs`, PCANBasic P/Invoke, 250 kbit) —
    live nav, engines, depth, wind, tanks, batteries.
  - **MQTT** — the app hosts an **embedded broker on port 1883** for Shelly relay devices
    (TV lift, swing, shades).
  - **Samsung TVs** (Tizen WebSocket + SmartThings cloud) and **Sonos amps** (UPnP/SOAP).
  - **Claude SDK** AI assistant (Anthropic 12.9.0) + ONNX runtime (needs VC++ 2015–2022 x64 redist).

Key source files:
| File | Responsibility |
|---|---|
| `ShellWindow.xaml.cs` | Main window, telemetry tick loop, wiring of all services, WebView bridge |
| `LocalServer.cs` | Embedded HTTP server (`:8080`), `/api/*` endpoints, remote-browser bridge |
| `Ip2slClient.cs` | TCP client to the iTach; sends raw command frames, reads state |
| `N2kService.cs` | PCAN NMEA 2000 decode (engines, nav, depth, wind, tanks, batteries) |
| `NmeaService.cs` | Shared `NavState` consumed by both N2K and legacy NMEA |
| `AvService.cs` | Samsung TV (WS + SmartThings) + Sonos (UPnP) control |
| `ShellyMqttService.cs` | Embedded MQTT broker + direct-HTTP RPC for Shelly relays (lift/swing/shades) |
| `WebUI/app.html` | The entire dashboard UI (single file, inline JS) |

---

## 2. Build & deploy

The PowerShell tool spawns a fresh shell each call — **`git`/`dotnet` are NOT on the session PATH.**
Prepend before every build:

```powershell
$env:Path = [Environment]::GetEnvironmentVariable("Path","Machine")+";"+[Environment]::GetEnvironmentVariable("Path","User")
cd C:\Users\User\BoatDashboard
dotnet build -c Release
```

`dotnet` lives at `C:\Program Files\dotnet\dotnet.exe`. Output exe:
`C:\Users\User\BoatDashboard\bin\Release\net9.0-windows\BoatDashboard.exe`.

### ⚠️ HTML-only deploy gotcha (bites every time)
Editing **only** `WebUI/app.html` (no `.cs` change) → `dotnet build` sees the assembly as up-to-date and
**SKIPS the PreserveNewest content copy**, so `bin/.../WebUI/app.html` stays stale and the server serves
the OLD page. After any HTML-only edit, copy it directly:

```powershell
Copy-Item WebUI\app.html bin\Release\net9.0-windows\WebUI\app.html -Force
```

The LAN server reads files from disk per request and sends `no-store`, so **no app restart and no iPad
cache-clear is needed** — just reload the page. Verify:
`Invoke-WebRequest http://192.168.20.8:8080/app.html`.

---

## 3. Autostart on boot

The app is launched at logon by a **Windows Scheduled Task named `BoatDashboard`**:
- Trigger: **AtLogOn** (current user), **30 s delay** (lets the network/iTach settle).
- RunLevel Highest, Interactive, RestartCount 3 @ 1-min interval.
- It is a **logon** task (WPF + WebView2 need an interactive desktop), so **the user must be signed in.**
  It will NOT run at the Windows login screen before sign-in.

Manage it:
```powershell
Get-ScheduledTask     -TaskName BoatDashboard
Start-ScheduledTask   -TaskName BoatDashboard
Stop-ScheduledTask    -TaskName BoatDashboard
Unregister-ScheduledTask -TaskName BoatDashboard   # remove
```

Firewall: rule **"BoatDashboard 1883 MQTT"** (inbound TCP 1883, profile Any) lets Shelly devices always
reach the embedded broker regardless of network category. There is also an allow rule for TCP 8080.

**Verified working 2026-07-19:** PC booted 00:16:06, task fired 00:16:30 (~24 s, matches the delay), app
came up and served `:8080`.

---

## 4. Remote access (Cloudflare tunnel)

`cloudflared.exe` is installed at `C:\Program Files (x86)\cloudflared\`. The app auto-starts a
**quick tunnel** on launch. Config in `%AppData%\BoatDashboard\settings.json`:
`EnableCloudflareTunnel:true`, `LanUser:"voms"`, `LanPass:"Lagoon630"`.

- Public URL is **EPHEMERAL** — a **new `*.trycloudflare.com` every launch/reboot.**
  Read the current one from the log:
  ```powershell
  Select-String -Path "C:\Users\User\BoatDashboard\bin\Release\net9.0-windows\debug.log" -Pattern "public URL" | Select-Object -Last 1
  ```
- The public tunnel is gated by **Basic Auth** (`voms` / `Lagoon630`). LAN/iPad access is unaffected
  (WebView loads via `vessel.local`). **Setting `LanPass` means loopback `127.0.0.1:8080` also needs
  auth now — use the LAN IP `192.168.20.8:8080` for un-authed local testing.**
- A **stable** URL requires a *named* tunnel (Cloudflare account + domain) — **not set up yet.** Until
  then the URL changes on every restart, so it must be re-shared after each reboot.

---

## 5. Network & device inventory

**The PC straddles two subnets. Scan the right one before declaring a device "down."**

| Subnet | What lives there |
|---|---|
| `192.168.20.x` | Boat LAN / dashboard / iPad / Shelly relays / Samsung TVs |
| `192.168.0.x`  | iTach IP2SL + Sonos amps |

- **PC NICs:** Ethernet **`192.168.20.8`** (/25, the live boat-LAN NIC, primary) is the address to use for
  LAN testing. "Wi-Fi 2" `.83` is a **stale ghost IP on a disconnected adapter — ignore it.**
  The PC also has `192.168.0.104` on the wired side (iTach/Sonos subnet).
- **iTach IP2SL:** `192.168.0.100:4999`.
- **Samsung TVs (Tizen):**
  - Owner/Salon TV: **`192.168.20.77`** (wired, MAC `c0-23-8d-14-57-39`). Paired, token `61708377`,
    controller name `Lagoon630-6824` (stored in `C:\voms`).
  - Cabin TV: `192.168.20.108` (2nd Samsung).
  - Tokens/names persisted in `C:\voms\av-tokens.json`, `av-samsung-names.json`, `av-macs.json`.
- **Sonos amps (S2, model ZPS16):** `192.168.0.119` SALON, `192.168.0.19` MAINDECK, `192.168.0.78`
  FLYBRIDGE. Zone UIDs: SALON `RINCON_74CA604E602E01400`, MAINDECK `RINCON_74CA604E5FAA01400`,
  FLYBRIDGE `RINCON_74CA60E7CFFB01400`.
- **Shelly relays:** on `192.168.20.x`. TV lift + swing + 5 shades — all commissioned. Commands are
  **1 gentle direct-HTTP RPC (instant) + MQTT publish as backup** (see §8).

---

## 6. How control actually works (per subsystem)

### Lighting / nav (iTach)
- Commands are **4-byte little-endian bursts** sent by `Ip2slClient.SendCommandAsync(uint)`.
- **Captured real codes** (by sniffing what the original iPad app sends — see §7):
  anchor light `0x03`, running/nav lights `0x01`, inverter `0x180000`, gen START `0x0F00`,
  gen STOP `0x1000`, all-on `0x0600`, all-off `0x0700`.
- **Status feedback:** telemetry `field 12` is a discrete bitmask mirrored across ch00–03:
  **anchor = `0x0010`, electronics = `0x1000`, running lights = `0x8000`** (reads `0x840F` underway).
  `ShellWindow.OnTick` decodes this into `live.navStatus`.
- **Feedback state that IS wired & verified:** nav lights, anchor, electronics, deck circuits.
- **Interior-light per-fixture feedback is UNRESOLVED** — toggling e.g. Salon does not move a readable
  bit (ch01 f6/f7 tracks scene/nav/deck circuits, not individual interior lights). **Do not claim
  interior-light status is live.** Mapping per-bit→physical-light needs eyes on the boat + the live
  bit monitor (Settings page, `/api/raw`).

### Samsung TVs (hybrid, control-safe)
- **Cloud (SmartThings REST) = primary, local WebSocket = automatic fallback** (`AvService.cs`,
  circuit breaker + `KeepAliveInterval=20s` on the WS to stop idle socket death).
- SmartThings PAT is saved but the account currently has **zero devices** →
  **TO-DO: user must add the TV to the SmartThings account**, then re-scan. Until then the local
  WS fallback is the active path.
- **Boot-prompt suppression (hard-won, do not regress):** a WS connect in the first ~35 s after TV
  power-on triggers Tizen's "Allow" pairing prompt even with a valid token. Fixes in place:
  a pre-connect gate that refuses to connect during that window; a **deterministic controller name**
  `Lagoon630-<sha1>` (a random name per connect made the TV re-prompt forever); a single-flight
  pairing semaphore; and treating `ms.channel.timeOut` as "waking up," not "re-pair," unless the TV
  has been steadily on > 90 s. WoL works now that the TV is **wired** (Wi-Fi WoL never woke it).
- Control is **fire-and-forget** (`/api/av/control` returns instantly) with a fast TCP reachability
  gate, so an **off/asleep TV can't stall the whole app** (see §7 — that was the "nothing works" bug).

### Sonos amps
- Controlled straight from fixed UPnP URLs (`AvService.FromId()` — no discovery scan needed):
  volume, mute, play/pause, grouping/link, source switch (TV/optical vs analog line-in), play a
  Sonos favorite / internet-radio stream. All verified end-to-end on the real speakers.
- `playfav` (Spotify playlists) is **untested** — it only works once the user links Spotify to Sonos
  in the Sonos app and stars playlists as favorites.

### Shelly relays (TV lift, swing, 5 shades)
- Each command fires **one direct-HTTP `Switch.Set` RPC (~0.1 s, instant)** plus an MQTT publish for
  reliability/position tracking. A **warm loop** keeps each device's HTTP connection alive so the
  first tap isn't slow. See §8 for the recent fixes.

---

## 7. What failed recently (incident log)

### 2026-07-19 — Duplicate app instances → dead tunnels + iPad "connection lost"
- **Symptom:** iPad Safari showed *"can't open the page because the network connection was lost"* on
  `192.168.20.8`, and both Cloudflare tunnel URLs returned **NXDOMAIN** even though `cloudflared` was
  still running.
- **Root cause:** **TWO `BoatDashboard.exe` instances were running** (the scheduled task + a second
  launch). Each spawned its own `cloudflared`, so two quick tunnels came up near-simultaneously and
  neither registered cleanly; the second instance lost the `:8080` bind race and destabilized serving.
- **Fix:** killed all `BoatDashboard` + `cloudflared` processes, relaunched a **single** instance via the
  scheduled task. Result: one app, one cloudflared, one fresh tunnel; LAN → HTTP 200, tunnel → 401
  without auth / 200 with `voms:Lagoon630`.
- **Prevention:** never `Start-ScheduledTask` / launch the exe if one is already running. Check first:
  `Get-Process BoatDashboard`. Exactly one instance should own the `:8080` listener.

### 2026-07-15 — "Nothing works" (amps, lights, TV all dead)
- **Root cause:** commands to an **OFF Samsung TV** ground ~45 s each on WebSocket retries; with the
  power button mashed they piled up and exhausted the shared app, so **all** control stopped. It was
  the off TV choking the app, **not** the network/amps/lights.
- **Fix:** fast TCP reachability gate before TV commands (off TV returns in ~0.5 s), fire-and-forget
  control endpoint, shorter WS connect timeout + fewer retries.

### 2026-07-15 — Endless Samsung "Allow" prompt loop
- **Root cause:** a new random controller name every connect made the TV treat each connect as a new
  device. **Fix:** deterministic per-TV name + single-flight pairing gate + correct token capture.

### Lift "only runs 2 s" bug
- **Root cause:** the hold-mode dead-man watchdog was 700 ms; WiFi/tablet heartbeat jitter tripped it
  mid-hold and cut the motor. **Fix:** widened to 1500 ms (firmware `auto_off` 10 s still caps it);
  normal release still stops instantly via the explicit `stop` the UI sends.

### Shade OPEN sometimes fired the CLOSE relay
- **Root cause:** OPEN/CLOSE used position-relative direction, and a drifted position estimate inverted
  it. **Fix:** OPEN/CLOSE now drive the **absolute** direction and run the **full** travel time to hit
  the limit and re-zero; only HALF uses position-relative logic.

---

## 8. ⚠️ DO NOT DO (hard-learned)

1. **DO NOT blind-scan for iTach command codes.** A blind code scan (firing `0x0A00`–`0x1800`)
   **accidentally STARTED THE GENSET.** Genset/inverter/other systems share the light-code space.
   To find new codes, use the **safe capture method** instead (§ below), never a brute-force sweep.
2. **DO NOT run two app instances.** See the 2026-07-19 incident — duplicates kill the tunnels and
   destabilize serving. Check `Get-Process BoatDashboard` before launching.
3. **DO NOT pipe a SOAP body into `curl` in PowerShell** (`$body | curl --data-binary '@-'`) — it
   **corrupts the body** and Sonos returns UPnP 402 "Invalid Args." Write the XML to a **file**
   (UTF-8, no BOM) and use `curl --data-binary "@file.xml"`. `Invoke-WebRequest` also mangles
   AVTransport writes. (The app's C# `HttpClient` sends clean bodies — only manual probes are affected.)
4. **DO NOT assume an HTML edit deployed** — the build skips the content copy on HTML-only changes.
   Always `Copy-Item` `app.html` into `bin/.../WebUI/` (see §2).
5. **DO NOT test against `127.0.0.1:8080` expecting no auth** — loopback now requires Basic Auth
   because `LanPass` is set. Use `192.168.20.8:8080`.
6. **DO NOT assume a device is down after scanning one subnet** — Shellys/TVs are on `.20.x`, iTach and
   Sonos on `.0.x` (§5).
7. **DO NOT claim interior-light per-fixture status is live** — it is not readable yet (§6).
8. **DO NOT rely on `python` here** — it's a Windows Store **stub**, not a real interpreter. Use
   PowerShell for JSON/scripting.

### Safe code-capture method (replaces blind scanning)
The original iPad app's real command codes were captured by making the **PC impersonate the iTach**:
user physically unplugs the iTach, the PC takes `192.168.0.100` on a spare NIC, a PowerShell listener
on `:4999` streams fake state frames (the app only sends commands once it thinks it's "linked" =
receiving state) and logs the incoming command bytes. Then press buttons in the original app. Scripts
live in the scratchpad (`capture3.ps1` is the good one). Clean up afterward:
`Remove-NetIPAddress -IPAddress 192.168.0.100`, put the PC back on `.104`, restart the app.

---

## 9. Troubleshooting quick reference

| Symptom | First check | Likely fix |
|---|---|---|
| Dashboard unreachable from iPad | `Get-Process BoatDashboard` (expect **one**); `Get-NetTCPConnection -LocalPort 8080 -State Listen` | Kill duplicate instances, relaunch via task |
| Cloudflare URL 404/NXDOMAIN | Is more than one `cloudflared` running? | Collapse to one app instance; read new URL from `debug.log` `public URL` |
| Lights/amps/TV all frozen | Is a Samsung TV off and being hammered? | Already gated in code; if regressed, check `SamsungAsync` reachability gate |
| Lift runs only ~2 s | Hold-mode watchdog / WiFi jitter | `HoldWatchdogMs` (now 1500 ms) in `ShellyMqttService.cs` |
| Shade OPEN goes wrong way | Position drift | Absolute-direction OPEN/CLOSE logic (already fixed) |
| Sonos write returns 402 | Manual `curl`/`Invoke-WebRequest` body corruption | Use a file body; or use the app's `/api/av/control` path |
| HTML change not showing | Stale `bin/.../WebUI/app.html` | `Copy-Item app.html` into bin (§2) |
| iTach dead / lights not responding | `192.168.0.100:4999` reachable? NIC `Ethernet` connected? | Check cable/switch power; client self-heals within ~5 s once it returns |

Logs: `C:\Users\User\BoatDashboard\bin\Release\net9.0-windows\debug.log`.
Live bit monitor: dashboard **Settings** page (polls `/api/raw` — read-only; watch bits flash as
switches are flipped on the boat).

---

## 10. Git / deployment notes

- Repo: `origin https://github.com/tikson-elektronic/BoatDashboard.git`, branch `main`.
- **Push auth caveat:** `credential.helper=manager` (GCM) cannot show its interactive prompt in a
  non-interactive shell, and `gh` CLI is not installed. Commits land locally fine, but **push must be
  run by the signed-in user** — e.g. in-session `! git -C C:\Users\User\BoatDashboard push origin main`
  (surfaces the credential prompt), or install/auth `gh`, or configure a PAT.
- Always prepend the PATH line (§2) before any `git`/`dotnet` command.
