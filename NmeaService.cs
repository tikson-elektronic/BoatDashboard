using System.Globalization;
using System.IO;
using System.Net.Sockets;

namespace BoatDashboard;

/// <summary>
/// Feeds the NAVIGATION page with real instrument data from an NMEA 2000 network.
///
/// N2K is a binary CAN bus; a gateway (YachtDevices YDWG-02 / YDEN-02, Actisense W2K-1, etc.)
/// bridges it onto the boat LAN. This client connects to that gateway over TCP and decodes the
/// nav data. It understands the translated NMEA 0183 sentences every gateway can emit
/// (RMC/GGA/VTG/DPT/DBT/MWV/HDG/HDT/VHW) — position, SOG, COG, heading, depth, wind, fix quality —
/// which is exactly what the navigation screen shows.
///
/// Configure the gateway address in Settings (NmeaHost:NmeaPort). GRACEFUL: with no host set, or
/// the gateway unreachable, navigation just keeps its last/demo values — nothing else is affected.
/// Auto-reconnects.
/// </summary>
public sealed class NmeaService : IDisposable
{
    public sealed class NavState
    {
        public double? Lat, Lon;          // decimal degrees
        public double? Sog;               // knots (speed over ground)
        public double? Cog;               // ° true (course over ground)
        public double? Heading;           // ° (magnetic or true)
        public double? Variation;         // ° magnetic variation (+E / -W)
        public double? Depth;             // metres below transducer
        public double? WindSpeed;         // knots (apparent)
        public double? WindAngle;         // ° relative to bow
        public double? WaterSpeed;        // knots (through water)
        public int? Satellites;
        public double? Hdop;
        public int? FixQuality;           // 0 = no fix, 1 = GPS, 2 = DGPS
        public DateTime UpdatedUtc = DateTime.UtcNow;

        // Engines (N2K PGN 127488 rapid / 127489 dynamic, translated to RPM + XDR sentences).
        public readonly Engine[] Engines = { new() { Index = 0 }, new() { Index = 1 } };
        public bool AnyEngineData => Engines.Any(e => e.Rpm is not null || e.CoolantTempC is not null);
    }

    public sealed class Engine
    {
        public int Index;                 // 0 = port, 1 = stbd
        public double? Rpm;
        public double? CoolantTempC;
        public double? OilPressureKpa;
        public double? Hours;
        public double? AlternatorV;
        public double? FuelRateLph;
        public DateTime UpdatedUtc = DateTime.UtcNow;
    }

    private readonly string _host;
    private readonly int _port;
    private readonly CancellationTokenSource _cts = new();
    private readonly NavState _state = new();

    public bool Configured => !string.IsNullOrWhiteSpace(_host);
    public bool Connected { get; private set; }
    public NavState State => _state;

    /// <summary>Raised (throttled) after a batch of sentences updates the nav state.</summary>
    public event Action<NavState>? OnUpdate;

    public NmeaService(string? host, int port)
    {
        _host = (host ?? "").Trim();
        _port = port <= 0 ? 2000 : port;   // YDWG-02 default raw/0183 TCP port
    }

    public void Start()
    {
        if (!Configured) { Ip2slClient.Log("[nmea] no gateway configured — navigation stays on demo/last values"); return; }
        _ = Task.Run(LoopAsync);
    }

    private async Task LoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                using var tcp = new TcpClient { NoDelay = true };
                await tcp.ConnectAsync(_host, _port, _cts.Token);
                Connected = true;
                Ip2slClient.Log($"[nmea] connected to gateway {_host}:{_port}");
                using var reader = new StreamReader(tcp.GetStream());
                var lastPush = DateTime.UtcNow;
                while (!_cts.IsCancellationRequested)
                {
                    var line = await reader.ReadLineAsync(_cts.Token);
                    if (line is null) break;
                    if (ParseSentence(line) && (DateTime.UtcNow - lastPush).TotalMilliseconds > 500)
                    {
                        lastPush = DateTime.UtcNow;
                        _state.UpdatedUtc = DateTime.UtcNow;
                        OnUpdate?.Invoke(_state);
                    }
                }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { Ip2slClient.Log("[nmea] " + ex.Message); }
            Connected = false;
            try { await Task.Delay(3000, _cts.Token); } catch { break; }
        }
    }

    /// <summary>Parses one NMEA 0183 sentence into the nav state. Returns true if anything changed.</summary>
    public bool ParseSentence(string line)
    {
        line = line.Trim();
        int star = line.IndexOf('*');
        if (star >= 0) line = line[..star];
        if (line.Length < 6 || line[0] != '$' && line[0] != '!') return false;
        var f = line.Split(',');
        if (f.Length < 2) return false;
        var type = f[0].Length >= 6 ? f[0][3..6] : f[0].TrimStart('$', '!');

        try
        {
            switch (type)
            {
                case "RMC":  // recommended minimum: time, status, lat, lon, sog, cog, date, magvar
                    if (f.Length > 9 && f[2] == "A")
                    {
                        _state.Lat = ParseLat(f[3], f[4]); _state.Lon = ParseLon(f[5], f[6]);
                        _state.Sog = Num(f[7]); _state.Cog = Num(f[8]);
                        if (f.Length > 10) { var v = Num(f[10]); if (v is not null) _state.Variation = f.Length > 11 && f[11] == "W" ? -v : v; }
                        return true;
                    }
                    return false;
                case "GGA":  // fix: lat, lon, quality, satellites, HDOP
                    if (f.Length > 8)
                    {
                        _state.Lat = ParseLat(f[2], f[3]); _state.Lon = ParseLon(f[4], f[5]);
                        _state.FixQuality = (int?)Num(f[6]); _state.Satellites = (int?)Num(f[7]); _state.Hdop = Num(f[8]);
                        return true;
                    }
                    return false;
                case "VTG":  // track + ground speed: COG true (f1), SOG knots (f5)
                    if (f.Length > 5) { _state.Cog = Num(f[1]) ?? _state.Cog; _state.Sog = Num(f[5]) ?? _state.Sog; return true; }
                    return false;
                case "DPT":  // depth of water: metres below transducer (f1)
                    if (f.Length > 1) { _state.Depth = Num(f[1]); return true; }
                    return false;
                case "DBT":  // depth below transducer: metres in f3
                    if (f.Length > 3) { _state.Depth = Num(f[3]); return true; }
                    return false;
                case "MWV":  // wind: angle (f1), reference (f2 R/T), speed (f3), unit (f4), status (f5 A/V)
                    if (f.Length > 4 && (f.Length <= 5 || f[5] != "V"))
                    {
                        _state.WindAngle = Num(f[1]);
                        var ws = Num(f[3]);
                        _state.WindSpeed = f[4] switch { "N" => ws, "K" => ws * 0.539957, "M" => ws * 1.94384, _ => ws };
                        return true;
                    }
                    return false;
                case "HDG":  // heading magnetic (f1) + deviation/variation
                    if (f.Length > 1) { _state.Heading = Num(f[1]) ?? _state.Heading; return true; }
                    return false;
                case "HDT":  // heading true (f1)
                    if (f.Length > 1) { _state.Heading = Num(f[1]) ?? _state.Heading; return true; }
                    return false;
                case "VHW":  // water speed + heading: heading true (f1), speed knots (f5)
                    if (f.Length > 5) { _state.Heading = Num(f[1]) ?? _state.Heading; _state.WaterSpeed = Num(f[5]); return true; }
                    return false;
                case "RPM":  // engine/shaft revolutions: source (f1 S/E), number (f2), speed rpm (f3)
                    if (f.Length > 3)
                    {
                        int n = (int)(Num(f[2]) ?? 0);
                        int eng = Math.Clamp(n <= 1 ? n : n - 1, 0, 1);   // #0/#1 or #1/#2 numbering
                        var rpm = Num(f[3]);
                        if (rpm is not null) { _state.Engines[eng].Rpm = rpm; _state.Engines[eng].UpdatedUtc = DateTime.UtcNow; return true; }
                    }
                    return false;
                case "XDR":  // transducer measurements — engine temp/pressure/voltage/hours (groups of 4)
                    return ParseXdr(f);
                default: return false;
            }
        }
        catch { return false; }
    }

    // XDR carries generic transducer readings in groups of 4: type, value, unit, id.
    // Engine gateways tag them with ids like "ENGINE#0"/"ENGINE1"/"STBD"; map best-effort.
    private bool ParseXdr(string[] f)
    {
        bool any = false;
        for (int i = 1; i + 3 < f.Length; i += 4)
        {
            var t = f[i]; var val = Num(f[i + 1]); var unit = f[i + 2]; var id = (f[i + 3] ?? "").ToUpperInvariant();
            if (val is null) continue;
            int eng = (id.Contains('1') || id.Contains("STBD") || id.Contains("STARBOARD")) ? 1 : 0;
            var e = _state.Engines[Math.Clamp(eng, 0, 1)];
            switch (t)
            {
                case "C": e.CoolantTempC = unit == "K" ? val - 273.15 : val; e.UpdatedUtc = DateTime.UtcNow; any = true; break;      // temperature
                case "P": e.OilPressureKpa = unit == "B" ? val * 100 : val / 1000.0; e.UpdatedUtc = DateTime.UtcNow; any = true; break; // pressure (bar or pascal → kPa)
                case "U": e.AlternatorV = val; any = true; break;                                                                      // voltage
                case "T": e.Rpm = val; any = true; break;                                                                              // tachometer
                case "G": if (id.Contains("HOUR")) { e.Hours = val; any = true; } break;                                              // generic (engine hours)
            }
        }
        return any;
    }

    private static double? Num(string s) =>
        double.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out var v) ? v : null;

    // NMEA lat/lon are ddmm.mmmm / dddmm.mmmm + hemisphere.
    private static double? ParseLat(string v, string h)
    {
        if (v.Length < 4 || !double.TryParse(v, NumberStyles.Any, CultureInfo.InvariantCulture, out var raw)) return null;
        double deg = Math.Floor(raw / 100), min = raw - deg * 100, dec = deg + min / 60.0;
        return h == "S" ? -dec : dec;
    }
    private static double? ParseLon(string v, string h)
    {
        if (v.Length < 5 || !double.TryParse(v, NumberStyles.Any, CultureInfo.InvariantCulture, out var raw)) return null;
        double deg = Math.Floor(raw / 100), min = raw - deg * 100, dec = deg + min / 60.0;
        return h == "W" ? -dec : dec;
    }

    public void Dispose()
    {
        _cts.Cancel();
        _cts.Dispose();
    }
}
