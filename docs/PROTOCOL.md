# Boat Controller Protocol — Reverse-Engineering Notes

This documents how the lighting/monitoring protocol behind the **Global Caché iTach
IP2SL** was reverse-engineered, and the full decoded format. The controller is a
multi-channel marine digital-switching/monitoring unit on an RS-232 bus; the IP2SL
bridges it to TCP `192.168.0.100:4999` (57600 8-O-1).

## 1. Discovery

- Network scan found a `Global Cache` MAC (`00:0c:1e:05:03:50`) at `192.168.0.100`.
- iTach API on `4998`:
  - `getversion` → `710-1009-05`
  - `get_NET,0:1` → `NET,0:1,UNLOCKED,DHCP,192.168.0.100,255.255.255.0,192.168.0.1`
  - `get_SERIAL,1:1` → `SERIAL,1:1,57600,FLOW_NONE,PARITY_ODD`
- Serial passthrough on `4999` streams the controller's telemetry to any client.

## 2. Telemetry frame format

ASCII frames terminated by `>\r\n`:

```
<CC:f0,f1,f2,f3,f4,f5,f6,f7,f8,f9,f10,f11,f12,f13,f14,f15>
```

- `CC` — channel id (`00`–`03` real; `FE`/`FF` are framing/echo artifacts that
  mirror the previous channel and must be ignored).
- 16 comma-separated 4-hex-digit fields.
- Fields 12–15 are constants/flags across a session:
  - f12: alternates `0000`/`0400` every frame — a **heartbeat/sequence bit** (noise).
  - f13: `FFDF` constant.
  - f14: `F100` when no app is connected, `F000` when an app session is active —
    an **app-connected flag**, *not* a light state.
  - f15: `77E5` framing footer (not a checksum — constant).

### Sensor field map (confirmed against the iPad app's on-screen values)

| Reading | Ch · field | Conversion |
|---|---|---|
| Fresh water port | `00` f10 | % |
| Fresh water stbd | `00` f11 | % |
| Fuel fwd port | `03` f2 | % |
| Fuel fwd stbd | `03` f3 | % |
| Fuel aft port | `03` f10 | % |
| Fuel aft stbd | `03` f11 | % |
| Battery Genset | `00` f2 | ÷10 → V |
| Battery Port engine | `00` f4 | ÷10 → V |
| Battery Stbd engine | `00` f6 | ÷10 → V |
| Battery Service | `00` f8 | ÷10 → V |
| Shore 1 (V,A,Hz) | `02` f0,f1,f2 | f2÷10 → Hz |
| Shore 2 (V,A,Hz) | `02` f3,f4,f5 | f5÷10 → Hz |
| Generator (V,A,Hz) | `02` f6,f7,f8 | f8÷10 → Hz |

Battery voltage fields sit at f2/f4/f6/f8 (every other slot); each has a neighbour
(f3/f5/f7 ≈ 512) that is likely per-bank current or SOC (unconfirmed). AC is laid
out as `(V, A, Hz×10)` triplets across channel `02`.

## 3. Command format

The iPad app keeps one persistent TCP connection to `4999` and:

- sends a **heartbeat** `FF 00 00 00` every ~6 s, and
- sends a light command as a **4-byte little-endian value**, burst a few times per tap.

Commands are read from the device-bound (TX) direction, so they are only visible by
capturing the **iPad → controller** traffic.

### Capture method (ARP-redirect MITM on the LAN)

The iPad↔controller traffic is unicast and invisible to a third host on a switched
LAN, so it was captured by:

1. `netsh interface ipv4 set interface "<iface>" forwarding=enabled` + global forwarding.
2. Static ARP entries pinning the **real** MACs of the controller and iPad on the
   capturing host (so forwarding works while their caches are poisoned).
3. A scapy ARP spoofer telling the iPad that the controller is at our MAC and vice
   versa (gratuitous `op=2` replies every 2 s; restore on exit).
4. `dumpcap -f "host <controller> and host <ipad> and tcp port 4999"`.
5. Decode `tshark -Y "ip.src==<ipad> && tcp.len>0" -T fields -e tcp.payload`.

The capture rig is fully reversible (ARP restored, forwarding disabled, static
entries removed afterward).

### Decoded command codes

| Control | LE value | Bytes |
|---|---|---|
| All On | `0x0600` | `00 06 00 00` |
| All Off | `0x0700` | `00 07 00 00` |
| Interior Courtesy | `0x0009` | `09 00 00 00` |
| Port Fwd Cabin | `0x0100` | `00 01 00 00` |
| Port Fwd Gangway | `0x0200` | `00 02 00 00` |
| Port Mid Cabin | `0x0300` | `00 03 00 00` |
| Galley | `0x0500` | `00 05 00 00` |
| Salon | `0x0800` | `00 08 00 00` |
| Stbd Fwd Cabin | `0x0900` | `00 09 00 00` |
| Stbd Aft Cabin | `0x0D00` | `00 0D 00 00` |

Notes:

- Individual lights act as **toggles**; an odd number of sends nets one change.
- **All On/All Off** are absolute scene commands.
- Codes `0x0400`, `0x0A00`–`0x0C00` were not mapped (gaps → likely more lights).
- **Byte order is the classic trap:** only Interior Courtesy keeps its value in
  byte 0; every other code's significant byte is byte 1. The app must encode the
  full 32-bit value little-endian, not a `uint16` in the low byte.

## 4. Gotchas learned the hard way

- **Serialize socket writes.** The heartbeat and a command writing to the same
  socket concurrently interleave their bytes and corrupt the command — guard all
  writes with a lock/semaphore.
- **One client at a time is cleanest.** Multiple app instances (or the iPad +
  dashboard together) on the same serial bus can fight; prefer a single controller.
- **Telemetry only flows on the monitor screen** of the reference app — but it flows
  to *any* direct TCP connection regardless, so reading status needs no MITM.
