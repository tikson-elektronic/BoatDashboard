using System.IO;
using System.Runtime.InteropServices;

namespace BoatDashboard;

/// <summary>
/// Reads the NMEA 2000 bus directly through a PEAK PCAN-USB adapter (USB→CAN) and decodes the
/// PGNs the dashboard shows: engines (RPM/temp/oil/hours/alternator/fuel rate), position, SOG/COG,
/// heading, depth, wind, fluid levels and battery status. Complements <see cref="NmeaService"/>
/// (which needs an Ethernet gateway) — same NavState sink, so the UI doesn't care which source fed it.
///
/// GRACEFUL: if PCANBasic.dll or the adapter is missing, Start() logs once and does nothing — the
/// navigation/engine pages just keep their demo/last values. Auto-reconnects if the adapter drops.
///
/// N2K frames are 29-bit extended CAN IDs at 250 kbit/s. PGNs larger than 8 bytes use "fast packet"
/// framing (first byte = sequence/frame counter, second byte of frame 0 = total length) which this
/// decoder reassembles per (source, PGN).
/// </summary>
public sealed class N2kService : IDisposable
{
    // ---- PCAN-Basic P/Invoke (loaded dynamically so a missing DLL never crashes the app) ----
    private const ushort PCAN_USBBUS1 = 0x51;
    private const ushort PCAN_BAUD_250K = 0x011C;
    private const uint PCAN_ERROR_OK = 0;
    private const uint PCAN_ERROR_QRCVEMPTY = 0x20;
    private const byte PCAN_MESSAGE_EXTENDED = 0x02;

    [StructLayout(LayoutKind.Sequential)]
    private struct TPCANMsg
    {
        public uint ID;
        public byte MSGTYPE;
        public byte LEN;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 8)] public byte[] DATA;
    }

    [DllImport("PCANBasic.dll", EntryPoint = "CAN_Initialize")]
    private static extern uint CanInitialize(ushort channel, ushort btr0btr1, byte hwType, uint ioPort, ushort interrupt);
    [DllImport("PCANBasic.dll", EntryPoint = "CAN_Uninitialize")]
    private static extern uint CanUninitialize(ushort channel);
    [DllImport("PCANBasic.dll", EntryPoint = "CAN_Read")]
    private static extern uint CanRead(ushort channel, out TPCANMsg msg, IntPtr timestamp);

    private readonly CancellationTokenSource _cts = new();
    private readonly NmeaService.NavState _state;
    public bool Connected { get; private set; }

    /// <summary>Raised (throttled) after decoded PGNs update the nav state.</summary>
    public event Action<NmeaService.NavState>? OnUpdate;

    public N2kService(NmeaService.NavState sharedState) => _state = sharedState;

    public void Start() => _ = Task.Run(LoopAsync);

    private async Task LoopAsync()
    {
        // Missing DLL → single log line, no retry storm (nothing to talk to until it's installed).
        if (!NativeLibrary.TryLoad("PCANBasic.dll", out _))
        {
            Ip2slClient.Log("[n2k] PCANBasic.dll not found — install PEAK PCAN-Basic; N2K stays idle");
            return;
        }

        while (!_cts.IsCancellationRequested)
        {
            uint rc;
            try { rc = CanInitialize(PCAN_USBBUS1, PCAN_BAUD_250K, 0, 0, 0); }
            catch (Exception ex) { Ip2slClient.Log("[n2k] init threw: " + ex.Message); return; }

            if (rc != PCAN_ERROR_OK)
            {
                // Adapter unplugged / driver hiccup — quiet retry.
                try { await Task.Delay(10000, _cts.Token); } catch { break; }
                continue;
            }

            Connected = true;
            Ip2slClient.Log("[n2k] PCAN-USB open @250k — reading NMEA 2000 bus");
            var lastPush = DateTime.UtcNow;
            var idleSince = DateTime.UtcNow;

            while (!_cts.IsCancellationRequested)
            {
                uint r = CanRead(PCAN_USBBUS1, out var msg, IntPtr.Zero);
                if (r == PCAN_ERROR_QRCVEMPTY)
                {
                    if ((DateTime.UtcNow - idleSince).TotalSeconds > 30) break;   // bus dead → reinit
                    try { await Task.Delay(5, _cts.Token); } catch { break; }
                    continue;
                }
                if (r != PCAN_ERROR_OK) break;   // hardware error → reinit
                idleSince = DateTime.UtcNow;
                if ((msg.MSGTYPE & PCAN_MESSAGE_EXTENDED) == 0) continue;   // N2K is 29-bit only

                if (HandleFrame(msg.ID, msg.DATA, msg.LEN) && (DateTime.UtcNow - lastPush).TotalMilliseconds > 500)
                {
                    lastPush = DateTime.UtcNow;
                    _state.UpdatedUtc = DateTime.UtcNow;
                    OnUpdate?.Invoke(_state);
                }
            }

            Connected = false;
            try { CanUninitialize(PCAN_USBBUS1); } catch { }
            try { await Task.Delay(3000, _cts.Token); } catch { break; }
        }
    }

    // ---- fast-packet reassembly: key = (source, pgn) ----
    private sealed class FpBuf { public byte Seq; public int Len; public int Got; public byte[] Data = new byte[233]; }
    private readonly Dictionary<uint, FpBuf> _fp = new();

    /// <summary>Decodes one CAN frame; returns true if the nav state changed.</summary>
    public bool HandleFrame(uint canId, byte[] data, int len)
    {
        // 29-bit id → priority(3) | PGN(18) | source(8). PDU1 (PS<240): PS is destination, not part of PGN.
        uint pgn = (canId >> 8) & 0x3FFFF;
        byte src = (byte)(canId & 0xFF);
        if (((pgn >> 8) & 0xFF) < 240) pgn &= 0x3FF00;

        switch (pgn)
        {
            // ---- single-frame PGNs ----
            case 127488: return EngineRapid(data);
            case 127245: return Rudder(data);
            case 127250: return Heading(data);
            case 128267: return Depth(data);
            case 129025: return PositionRapid(data);
            case 129026: return CogSog(data);
            case 130306: return Wind(data);
            case 127505: return FluidLevel(data);
            case 127508: return Battery(data);

            // ---- fast-packet PGNs ----
            case 127489:   // engine dynamic
                // Key by source+PGN+sequence: port and stbd streams (or two messages from one engine
                // gateway) interleave on the bus, and a shared buffer cross-contaminates them.
                byte seqBits = (byte)(len > 0 ? data[0] & 0xE0 : 0);
                var full = Reassemble((uint)(src << 24) | (pgn << 3) | (uint)(seqBits >> 5), data, len, out var ready);
                return ready && EngineDynamic(full!);
            default: return false;
        }
    }

    private byte[]? Reassemble(uint key, byte[] d, int len, out bool complete)
    {
        complete = false;
        if (len < 2) return null;
        byte seq = (byte)(d[0] & 0xE0);
        int frame = d[0] & 0x1F;
        if (frame == 0)
        {
            var b = new FpBuf { Seq = seq, Len = Math.Min((int)d[1], 233) };
            int n = Math.Min(6, b.Len);
            Array.Copy(d, 2, b.Data, 0, n); b.Got = n;
            _fp[key] = b;
        }
        else if (_fp.TryGetValue(key, out var b) && b.Seq == seq)
        {
            int off = 6 + (frame - 1) * 7;
            if (off < b.Len)
            {
                int n = Math.Min(7, b.Len - off);
                Array.Copy(d, 1, b.Data, off, Math.Min(n, len - 1));
                b.Got += n;
            }
            if (b.Got >= b.Len) { _fp.Remove(key); complete = true; return b.Data; }
        }
        return null;
    }

    // ---- field helpers (N2K is little-endian; 0xFF.. = not available) ----
    private static ushort U16(byte[] d, int i) => (ushort)(d[i] | d[i + 1] << 8);
    private static short S16(byte[] d, int i) => (short)U16(d, i);
    private static uint U32(byte[] d, int i) => (uint)(d[i] | d[i + 1] << 8 | d[i + 2] << 16 | d[i + 3] << 24);
    private static int S32(byte[] d, int i) => (int)U32(d, i);
    private static bool Avail16(ushort v) => v < 0xFFFE;   // 0xFFFF = not available, 0xFFFE = error
    private static bool AvailS16(short v) => v != short.MaxValue && v != short.MaxValue - 1;   // 0x7FFF/0x7FFE

    private NmeaService.Engine Eng(int instance) => _state.Engines[Math.Clamp(instance, 0, 1)];

    private bool EngineRapid(byte[] d)   // PGN 127488: instance, RPM (0.25 rpm)
    {
        var rpm = U16(d, 1);
        if (!Avail16(rpm)) return false;
        var e = Eng(d[0]); e.Rpm = rpm * 0.25; e.UpdatedUtc = DateTime.UtcNow;
        return true;
    }

    private bool EngineDynamic(byte[] d) // PGN 127489 (fast packet, 26 bytes) — canboat field layout
    {
        // 0: instance | 1-2: oil pressure (100 Pa) | 3-4: oil temp (0.1 K) | 5-6: engine temp (0.01 K)
        // 7-8: alternator V (s16, 0.01 V) | 9-10: fuel rate (s16, 0.1 L/h) | 11-14: hours (u32, s)
        // NO leading SID byte — earlier offsets were +2 and produced garbage (-260°C, 1.2M hours).
        if (d.Length < 15) return false;
        var e = Eng(d[0]); bool any = false;
        var oil = U16(d, 1);                     // 100 Pa → kPa
        if (Avail16(oil)) { e.OilPressureKpa = oil * 0.1; any = true; }
        var temp = U16(d, 5);                    // 0.01 K → °C
        if (Avail16(temp)) { e.CoolantTempC = temp * 0.01 - 273.15; any = true; }
        var alt = S16(d, 7);                     // 0.01 V (signed; 0x7FFF = N/A)
        if (AvailS16(alt)) { e.AlternatorV = alt * 0.01; any = true; }
        var fuel = S16(d, 9);                    // 0.1 L/h (signed; 0x7FFF = N/A)
        if (AvailS16(fuel)) { e.FuelRateLph = fuel * 0.1; any = true; }
        var hours = U32(d, 11);                  // seconds
        if (hours < 0xFFFFFFFE && hours < 60_000u * 3600u) { e.Hours = hours / 3600.0; any = true; }   // >60k h = corrupt
        if (any) e.UpdatedUtc = DateTime.UtcNow;
        return any;
    }

    private bool Rudder(byte[] d)        // PGN 127245: instance, dir-order, angle-order, position (1e-4 rad, signed)
    {
        if (d[0] != 0) return false;     // only rudder instance 0 — a 2nd instance/device was making it flip-flop
        var pos = S16(d, 4);             // bytes 4-5 = actual rudder position
        if (!AvailS16(pos)) return false;
        _state.Rudder = pos * 1e-4 * 180.0 / Math.PI;
        return true;
    }

    private bool Heading(byte[] d)       // PGN 127250: sid, heading (1e-4 rad), dev, var, ref
    {
        var h = U16(d, 1);
        if (!Avail16(h)) return false;
        _state.Heading = h * 1e-4 * 180.0 / Math.PI;
        var v = S16(d, 5);
        if (AvailS16(v)) _state.Variation = v * 1e-4 * 180.0 / Math.PI;   // signed field: 0x7FFF/0x7FFE = N/A
        return true;
    }

    private bool Depth(byte[] d)         // PGN 128267: sid, depth (0.01 m), offset (0.001 m)
    {
        var dep = U32(d, 1);
        if (dep == 0xFFFFFFFF) return false;
        _state.Depth = dep * 0.01;
        return true;
    }

    private bool PositionRapid(byte[] d) // PGN 129025: lat/lon 1e-7 deg
    {
        int lat = S32(d, 0), lon = S32(d, 4);
        if ((uint)lat == 0x7FFFFFFF || (uint)lon == 0x7FFFFFFF) return false;
        _state.Lat = lat * 1e-7; _state.Lon = lon * 1e-7;
        return true;
    }

    private bool CogSog(byte[] d)        // PGN 129026: sid, ref, COG (1e-4 rad), SOG (0.01 m/s)
    {
        bool any = false;
        var cog = U16(d, 2);
        if (Avail16(cog)) { _state.Cog = cog * 1e-4 * 180.0 / Math.PI; any = true; }
        var sog = U16(d, 4);
        if (Avail16(sog)) { _state.Sog = sog * 0.01 * 1.94384; any = true; }   // m/s → kn
        return any;
    }

    private bool Wind(byte[] d)          // PGN 130306: sid, speed (0.01 m/s), angle (1e-4 rad), ref
    {
        var ws = U16(d, 1); var wa = U16(d, 3);
        if (!Avail16(ws)) return false;
        _state.WindSpeed = ws * 0.01 * 1.94384;
        if (Avail16(wa)) _state.WindAngle = wa * 1e-4 * 180.0 / Math.PI;
        return true;
    }

    // Fluid levels / battery flow into the same nav state via the engines array is wrong — the
    // dashboard's tank/battery tiles are fed by the iTach today. Decode + log them so the data is
    // visible in /api/raw-style debugging; wiring them to tiles is a UI decision for later.
    private bool FluidLevel(byte[] d)    // PGN 127505: instance/type, level (0.004 %), capacity
    {
        var lvl = S16(d, 1);
        if (!Avail16((ushort)lvl)) return false;
        N2kAux.Fluid[(byte)(d[0] & 0x0F)] = (d[0] >> 4, lvl * 0.004);
        return false;   // not shown on nav/engine pages (yet) — don't trigger UI pushes
    }

    private bool Battery(byte[] d)       // PGN 127508: instance, V (0.01), A (0.1), temp
    {
        var v = U16(d, 1);
        if (!Avail16(v)) return false;
        N2kAux.Battery[d[0]] = (v * 0.01, Avail16((ushort)S16(d, 3)) ? S16(d, 3) * 0.1 : null);
        return false;
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { CanUninitialize(PCAN_USBBUS1); } catch { }
        _cts.Dispose();
    }
}

/// <summary>Decoded-but-not-yet-displayed N2K data (tanks, batteries) — inspectable for wiring later.</summary>
public static class N2kAux
{
    public static readonly Dictionary<byte, (int type, double percent)> Fluid = new();
    public static readonly Dictionary<byte, (double volts, double? amps)> Battery = new();
}
