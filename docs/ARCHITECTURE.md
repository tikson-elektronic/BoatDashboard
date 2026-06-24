# Architecture & Code Walkthrough

This document explains **how the Boat System Dashboard is built** — every source file,
the threading model, the end-to-end data flow, and how to build/release it. For the
wire protocol itself (frames, command codes, capture method) see
[`PROTOCOL.md`](PROTOCOL.md).

---

## 1. Big picture

```
                 ┌──────────────────────── BoatDashboard.exe (WPF / .NET 9) ───────────────────────┐
                 │                                                                                  │
  RS-232 bus     │   Ip2slClient            DashboardViewModel           MainWindow (UI)            │
 ┌──────────┐    │  ┌────────────┐  Field() ┌──────────────┐  binding  ┌──────────────┐            │
 │Controller│◄──►│  │ read loop  │ ───────► │ Tanks/Batt/AC │ ────────► │ cards + pills │            │
 │ (marine  │TCP │  │ frame parse│          │ Lights/Chat   │           │ buttons/chat  │            │
 │ switching│4999│  │ write+lock │ ◄─────── │  (INotify…)   │ ◄──────── │ click handlers│            │
 │  unit)   │    │  │ heartbeat  │  Send    └──────┬───────┘  commands  └──────┬───────┘            │
 └──────────┘    │  │ reconnect  │                 │ MqttPoints()              │ 800 ms DispatcherTimer│
   via Global    │  └────────────┘                 ▼                          ▼                     │
   Caché IP2SL   │                          ┌─────────────┐            ┌──────────────┐             │
   (IP⇄serial)   │                          │MqttPublisher│            │ClaudeAssistant│            │
                 │                          │ (retained)  │            │ tool-use loop │            │
                 │                          └─────┬───────┘            └──────┬───────┘             │
                 └────────────────────────────────┼───────────────────────────┼────────────────────┘
                                                   ▼                           ▼
                                           MQTT broker                 Anthropic API
                                       (Home Assistant…)             (claude-opus-4-8)
```

The app is a single-window WPF dashboard. One long-lived TCP client owns the link to
the boat; a view model holds the latest readings; the UI, MQTT publisher and AI
assistant are all consumers of that view model.

---

## 2. Source files

| File | Lines | Role |
|---|---|---|
| `BoatDashboard.csproj` | 19 | .NET 9 `net9.0-windows`, WPF, `Anthropic` 12.9.0 + `MQTTnet` 4.3.7. `WinExe`, custom `app.manifest`. |
| `App.xaml(.cs)` | — | WPF application bootstrap; merges `Theme.xaml`, starts `MainWindow`. |
| `Theme.xaml` | — | Dark theme: color palette + card/button/pill styles. |
| `Ip2slClient.cs` | 153 | **The hardware layer.** TCP read loop, frame parser, serialized writes, heartbeat, auto-reconnect, debug log. |
| `ViewModels.cs` | 206 | MVVM view models (`TankVm`/`BatteryVm`/`AcVm`/`LightVm`/`ChatMessage`) + `DashboardViewModel` with `Refresh`, `StatusJson`, `MqttPoints`. |
| `MainWindow.xaml(.cs)` | 169 | Dashboard layout + wiring: 800 ms poll timer, light clicks, settings dialog, MQTT lifecycle, chat. |
| `SettingsWindow.xaml(.cs)` | — | Modal dialog: Claude API key + MQTT broker config. |
| `Settings.cs` | 49 | `AppSettings`/`MqttSettings` models + `SettingsStore` JSON load/save under `%AppData%`. |
| `MqttPublisher.cs` | 47 | MQTTnet wrapper: connect/disconnect, retained `PublishAsync`. |
| `ClaudeAssistant.cs` | 147 | Anthropic SDK manual tool-use loop with `get_status` + `control_lights` tools. |

---

## 3. The hardware layer — `Ip2slClient`

The only class that touches the network. Constants `Host = 192.168.0.100`, `Port = 4999`.

**Two background tasks**, both started by `Start()`:

- **`ReadLoopAsync`** — connects (`TcpClient { NoDelay = true }`), then loops reading
  bytes, decoding as `Latin1` (so raw 0x80–0xFF survive), appending to a `StringBuilder`,
  and calling `ParseFrames`. On any exception it disposes the socket, raises
  `ConnectionChanged(false)`, waits 1.5 s, and reconnects. The buffer is trimmed to
  2000 chars once it passes 4000 so a stuck stream can't grow unbounded.
- **`HeartbeatLoopAsync`** — every 6 s writes `FF 00 00 00`, mirroring the iPad app so
  the controller keeps the session "live" (`f14` flag → `F000`).

**Frame parsing** (`ParseFrames`) uses the compiled regex `<([0-9A-Fa-f]{2}):([^<>]*)>`.
Each match's hex fields are parsed into an `int[]` and stored in a
`ConcurrentDictionary<string,int[]>` keyed by channel id. Consumers read with
`Field(channel, index)` → `int?` (null if not seen yet). The parsed-through prefix is
removed from the buffer so partial trailing frames survive to the next read.

**Writes are serialized** behind a `SemaphoreSlim(1,1)`. This is critical: the
heartbeat task and a command (from a click or the AI) both write the same socket, and
without the lock their bytes interleave and corrupt the command (see PROTOCOL §4).

**`SendCommandAsync(uint code)`** packs the value little-endian into 4 bytes and writes
it **3×, 250 ms apart**, replicating the reference app's burst.

**`Log`** appends timestamped lines to `debug.log` next to the exe (gitignored).

> Threading note: the dictionary is concurrent and `_connected` is `volatile`, so the
> UI thread can call `Field()`/`Connected` freely while the read loop writes.

---

## 4. View models — `ViewModels.cs`

`Observable` is the tiny `INotifyPropertyChanged` base (`Set`/`Raise` with
`[CallerMemberName]`). `Palette` holds frozen brushes (thread-safe, shareable).

Each reading is a small VM that derives display text **and** a status color from its
value, so the XAML stays declarative:

- `TankVm.Fill` → green ≥50 %, amber ≥20 %, red below.
- `BatteryVm` → `OFF` ≤0.5 V, `CHARGED` ≥12.4 V, else `LOW`; brush graded similarly.
- `AcVm` → `ONLINE` when volts>1 or Hz>1; exposes `(V, A, Hz)` text.
- `LightVm` → carries the 4-byte `Code` and a commanded `IsOn` (optimistic — see §7).

`DashboardViewModel` owns `ObservableCollection`s of these plus connection/MQTT status
strings. Three methods bridge to the rest of the app:

- **`Refresh(client, nowText)`** — the heart of the read path. A local `F(ch,i)` helper
  pulls fields with a 0 fallback and maps them per the PROTOCOL table: AC from channel
  `02` triplets, batteries from `00` (÷10 → V), tanks as raw % from `00`/`03`.
- **`StatusJson()`** — compact JSON snapshot of tanks/batteries/AC, fed to the AI's
  `get_status` tool and published as `boat/status`.
- **`MqttPoints()`** — yields `(topic, value)` pairs (slugged names) for publishing.

---

## 5. UI & orchestration — `MainWindow`

Constructs the VM, the `Ip2slClient`, the `MqttPublisher`, and an 800 ms
`DispatcherTimer`. On the UI thread:

- **`OnTick`** calls `_vm.Refresh(...)` every 800 ms, and — if MQTT is enabled and
  connected — publishes every reading once per 5 s (`_lastPublish` gate).
- **`OnConnectionChanged`** marshals the link state onto the UI via `Dispatcher.Invoke`
  and flips the connection pill green/red.
- **Lighting**: `Light_Click` sends the bound `LightVm.Code` then optimistically toggles
  `IsOn`; `AllOn_Click`/`AllOff_Click` send the scene codes and set all lights.
- **Settings**: opens `SettingsWindow` modally; on OK re-applies (rebuilds the assistant,
  reconnects MQTT).
- **Chat**: `SendAsync` is re-entrancy-guarded (`_busy`), shows a "…" placeholder, awaits
  `ClaudeAssistant.AskAsync`, then swaps in the reply. Enter submits.

---

## 6. AI assistant — `ClaudeAssistant`

A manual **tool-use loop** on `claude-opus-4-8` via the official `Anthropic` C# SDK.
Two tools are declared:

- `get_status` (no input) → returns `DashboardViewModel.StatusJson()`.
- `control_lights` (`action` enum) → maps the action string to a 4-byte code and calls
  `Ip2slClient.SendCommandAsync`.

`AskAsync` keeps a running `_history`, then loops up to 6 turns: send messages → for each
content block, collect text and execute any tool-use → append the assistant turn and a
tool-result turn → repeat until the model stops requesting tools. The system prompt keeps
answers terse and tells it to flag unsafe readings (0 V battery, near-empty tank).

> The API key lives only in `%AppData%\BoatDashboard\settings.json` and is never logged
> or committed.

---

## 7. Known simplifications

- **Light state is optimistic.** The controller's telemetry doesn't expose a clean
  per-light on/off bit (the `f14` flag is app-connected, not light state — PROTOCOL §2),
  so the UI tracks *commanded* state. Because individual lights are toggles, an external
  change (someone using the iPad) can desync the dots until the next command.
- **AC neighbour fields** (f3/f5/f7 ≈ 512 on the battery channel) are unconfirmed and
  not surfaced.
- **One client at a time** is cleanest on the shared serial bus (PROTOCOL §4).

---

## 8. Build & release

```bash
# Dev run
dotnet run

# Self-contained single-file release (no .NET install needed on target)
dotnet publish -c Release -r win-x64 --self-contained true \
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true \
  -p:EnableCompressionInSingleFile=true
# → bin/Release/net9.0-windows/win-x64/publish/BoatDashboard.exe
```

The published single-file exe is attached to the GitHub Release rather than committed to
the repo (binaries don't belong in git history). `bin/` and `obj/` are gitignored.

---

## 9. Threading model at a glance

| Thread | Owns |
|---|---|
| WPF UI (dispatcher) | All VM mutation, the 800 ms timer, click/chat handlers |
| `ReadLoopAsync` task | Socket read, frame parse → concurrent dictionary |
| `HeartbeatLoopAsync` task | 6 s keepalive write |
| Awaited continuations | Command bursts, MQTT publish, Claude calls |

Cross-thread safety rests on three things: the **concurrent dictionary** for telemetry,
the **write semaphore** for the socket, and **`Dispatcher.Invoke`** for pushing link-state
changes back to the UI.
