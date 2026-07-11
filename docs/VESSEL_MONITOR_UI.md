# Vessel Monitor UI (Lagoon 630 MY)

The dashboard's user interface is the **"Lagoon 630 MY — Vessel Monitor"** design,
hosted inside the WPF app in a **WebView2** control and fed **live iTach telemetry**.
This document describes that UI layer: how it's built, how live data flows into it,
how the control bridge works, and how kiosk mode is enforced.

> The classic native-WPF dashboard (`MainWindow.xaml`) still exists in the repo but is
> **no longer the startup window**. `App.xaml` now starts `ShellWindow.xaml`.

---

## 1. Architecture at a glance

```
┌─────────────────────────── BoatDashboard.exe (WPF / .NET 9) ───────────────────────────┐
│                                                                                          │
│  ShellWindow (WebView2 host)                                                             │
│   ├─ Ip2slClient  ──(TCP 4999)──►  iTach IP2SL @ 192.168.0.100   (telemetry + commands)  │
│   ├─ 1 s timer: read fields ──► window.vomsApply({…})  ─────────┐                         │
│   ├─ WebMessageReceived  ◄── window.chrome.webview.postMessage ─┼─ control bridge         │
│   └─ CoreWebView2 ──► https://vessel.local/app.html             │                         │
│                                    │                            ▼                         │
│                        WebUI/app.html (the design)      { cmd: 'all_on' | 'settings' … }  │
│                          reads readouts from `LIVE`                                        │
└──────────────────────────────────────────────────────────────────────────────────────────┘
```

Key files:

| File | Role |
|---|---|
| `ShellWindow.xaml` / `.cs` | The window. Hosts WebView2, pushes telemetry, handles the control bridge, applies kiosk. |
| `WebUI/app.html` | The design, self-contained HTML/CSS/JS. The single source of the UI. |
| `WebUI/uploads/*.png` | Boat profile, deck plan, Lagoon logo. |
| `App.xaml` | `StartupUri="ShellWindow.xaml"`. |
| `Ip2slClient.cs` | Existing iTach socket client (unchanged) — telemetry + light commands. |
| `SettingsWindow.xaml` / `.cs` | Pairing, Claude key, **kiosk / launch-at-boot**, passcode shutdown. |

The WebUI folder is copied next to the exe on build (`<Content Include="WebUI\**\*">`) and
served over a virtual host, `https://vessel.local/app.html`, so relative asset paths resolve.

---

## 2. The design & how to update it

The design is delivered as a self-contained bundled HTML file from a
`claudeusercontent.com` design link. `WebUI/app.html` is the **inner document** extracted
from that bundle.

**Re-extracting after a design update** (PowerShell):

```powershell
$raw   = [IO.File]::ReadAllText("vessel-monitor-bundled.html", [Text.Encoding]::UTF8)
$start = $raw.IndexOf('<!DOCTYPE html>\n<html><head>')          # the escaped inner string
$end   = $raw.IndexOf('</html>', $start)
$inner = $raw.Substring($start, ($end - $start) + 7)
$dec   = [System.Text.RegularExpressions.Regex]::Unescape($inner) # handles /, \n, \\ etc.
$dec   = $dec.Substring(0, $dec.IndexOf('</body></html>') + '</body></html>'.Length)
[IO.File]::WriteAllText("WebUI\app.html", $dec, (New-Object System.Text.UTF8Encoding($false)))
```

After extracting, re-apply the small set of local edits (the design otherwise stands alone):

1. Point the topbar logo at the local asset:
   `<img src="uploads/lagoon-logo.png" … onerror="this.style.display='none'">`.
2. Re-apply the `LIVE` object + the HOME data-line bindings (§3).
3. Re-apply the steady-lamp fix (§6).

> The delivered design now includes its **own** Engines and AV screens — do **not** re-add
> the earlier hand-built versions. Earlier iterations didn't have them, so they were added
> locally; that is no longer needed.

Verify a render without launching the app:

```bash
msedge --headless=new --screenshot=out.png --window-size=1500,950 file:///…/WebUI/app.html
# change  screen:'home'  to  'engines' / 'av' / 'batteries'  to render other screens
```

---

## 3. Live telemetry bridge

`app.html` reads every live readout from a single `LIVE` object whose defaults equal the
design's demo values, so the UI still reads well before any hardware update arrives:

```js
var LIVE = {
  ac:{ v:222, a:9, hz:50 },
  tanks:{ waterPort:20, waterStbd:100, fuelAftPort:30, fuelFwdPort:28, fuelAftStbd:41, fuelFwdStbd:18 },
  waterAvg:60, fuelAvg:29,
  service:{ v:'0.0', low:true },
  engines:{ port:{…}, stbd:{…}, seaTemp:24, roomTemp:38 }
};
```

`ShellWindow` reads `Ip2slClient` fields on a 1 s timer and, **only when the iTach is
connected**, deep-merges a snapshot via `window.vomsApply({…})` and re-renders. With no
hardware connected, the design's demo values remain (nothing overwrites them).

### iTach field → LIVE mapping

| Reading | iTach field | Conversion | LIVE path |
|---|---|---|---|
| Fresh water — port | ch `00` f10 | value | `tanks.waterPort` |
| Fresh water — stbd | ch `00` f11 | value | `tanks.waterStbd` |
| Fuel — fwd port | ch `03` f2 | value | `tanks.fuelFwdPort` |
| Fuel — fwd stbd | ch `03` f3 | value | `tanks.fuelFwdStbd` |
| Fuel — aft port | ch `03` f10 | value | `tanks.fuelAftPort` |
| Fuel — aft stbd | ch `03` f11 | value | `tanks.fuelAftStbd` |
| Service bank | ch `00` f8 | ÷ 10 → V | `service.v` (+ `service.low` when < 22 V) |
| Shore 1 (home AC) | ch `02` f0/f1/f2 | Hz = f2 ÷ 10 | `ac.v` / `ac.a` / `ac.hz` |

`waterAvg` / `fuelAvg` are computed averages. `alarm` (topbar chip + battery tile) tracks
`service.low`. **Only the HOME tiles are bound so far**; the AC / batteries / tanks detail
screens still show demo values (future work).

---

## 4. Control bridge

Injected once via `AddScriptToExecuteOnDocumentCreatedAsync`, so `app.html` stays pristine:

- **Lighting master** — the design's `setAllZones(true/false)` is wrapped to also
  `postMessage({cmd:'all_on'|'all_off'})`; `ShellWindow` sends iTach `0x0600` / `0x0700`.
- **⚙ SETUP** — a rail entry appended after every render; posts `{cmd:'settings'}` →
  opens the WPF `SettingsWindow` (pairing, Claude key, kiosk, passcode shutdown).
- **AV controls** — TV lift / amplifier / remote post `{cmd:'av', action:…}`. Currently a
  no-op on the C# side (ready to wire to IR / amplifier hardware).

> Per-zone lighting, nav lights, and shades are **client-side visual state only** — they
> don't map 1:1 to the 8 iTach interior lights, so no hardware command is sent for them yet.

---

## 5. Screens

Nav rail (13): **HOME · ENGINES · AC POWER · BATTERIES · TANKS · NAV LIGHTS · LIGHTING ·
SHADES · AV / MEDIA · ELECTRONICS · AUTOMATION · SYSTEM · SETTINGS**, plus the injected
**⚙ SETUP**.

Two screens were added on top of the delivered design:

- **ENGINES** — twin-diesel cards: RPM, load bar, coolant, oil pressure, engine hours,
  fuel rate, plus sea-water / engine-room temps. Values live in `LIVE.engines`
  (demo / NMEA-2000-style; read-only).
- **AV / MEDIA** — Salon TV lift (Raise / Stop / Lower with an animated lift visual),
  amplifier (power / volume / mute / source), and a full TV remote (D-pad + OK, VOL/CH,
  playback). State in `S.av`.

---

## 6. Steady lamps

The design animated every **lit lamp** with a `pulseDot` glow (opacity 1 ↔ 0.35), so ON
lights blinked. That animation was removed from the two lamp spots (LIGHTING deck-plan
lamps and NAV LIGHTS position markers) — ON is now a steady glow. `pulseDot` is retained
on the genuine **alarms** (topbar service-battery-low chip, battery SoC fault, low-battery
banner, "Not Under Command" nav warning), which should blink. After any design re-extract,
`grep pulseDot WebUI/app.html` — only alarms should keep it.

---

## 6b. LAN access, discovery & remote control

`LocalServer.cs` (dependency-free `TcpListener`, port **8080**, no admin needed) serves the
UI to any device on the boat's Wi-Fi and exposes `GET /api/telemetry` + `POST /api/cmd`.
When serving `app.html` it injects a *remote bridge* (fakes the WebView2 channel over HTTP,
polls telemetry every 2 s) plus favicon/apple-touch-icon links — so an iPad gets live data
and control from plain Safari. Asset paths are URL-decoded (`%20` → space) because the
image filenames contain spaces. mDNS advertises `BoatDashboard._http._tcp` (Makaretu.Dns).
The LAN URL is shown in Settings → NETWORK.

**VNC**: TightVNC server runs as a Windows service on port **5900** (password `5577`,
firewall rule "TightVNC Server (TCP 5900)") for full remote desktop of the kiosk PC.

## 6c. Claude assistant + offline voice

The **ASSISTANT** screen (chat + mic) talks to `ClaudeAssistant.cs` — a tool-use loop on
`claude-opus-4-8` with live tools: `get_status` (tanks/batteries/AC), `get_pc_status`
(CPU load/temp, LAN URL, iTach link), `control_lights` (all 10 light commands). The page
posts `{cmd:'claude',text}` → `ShellWindow.AskClaudeAsync` → reply lands via
`window.claudeReply`. Voice is **fully offline** (`VoiceService.cs`, System.Speech):
mic button → one dictated utterance → auto-sends to Claude → the reply is spoken by
Windows TTS. Requires the API key in Settings → CLAUDE AI (assistant shows "no API key"
until set); mic errors surface in-chat.

## 7. Kiosk mode

Enabled in **Settings → KIOSK / DISPLAY** (`AppSettings.Kiosk`, `AppSettings.LaunchAtBoot`).

When kiosk is on, `ShellWindow.ApplyKiosk()`:

- Borderless (`WindowStyle=None`), sized to the **full primary screen** (Left/Top 0,
  Width/Height = `SystemParameters.PrimaryScreen…`) so it covers the **taskbar** — a plain
  Maximized borderless window only covers the work area.
- `Topmost`, cursor hidden (`Cursor=None` + injected `*{cursor:none}` in the page).
- `StateChanged` restores the window if anything minimises it.
- **`OnClosing` refuses to close** unless `App.AllowExit` is set — and the **only** thing
  that sets it is the passcode-**5577** *Shut Down* in Settings. So Alt+F4 / window chrome
  cannot exit the app.

**Launch at boot** writes the exe path to `HKCU\…\CurrentVersion\Run` (best-effort).

To exit a kiosk session: **⚙ SETUP → SHUT DOWN → passcode 5577**. (A force-kill via
`Stop-Process` also works for maintenance.)

---

## 8. Build & run

```powershell
dotnet build BoatDashboard.csproj -c Release
Start-Process bin\Release\net9.0-windows\BoatDashboard.exe
```

- Requires the **WebView2 Evergreen runtime** (present on current Windows 10/11 with Edge).
- After adding a new `.xaml` file, do a **clean rebuild** (`Remove-Item -Recurse obj,bin`)
  or WPF throws *"Cannot locate resource 'shellwindow.xaml'"* (incremental staleness).
- The running app loads `app.html` from the **bin output** `WebUI/` (copied Content) — after
  editing `WebUI/app.html`, rebuild (copies it) and relaunch.
```
