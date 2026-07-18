using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace BoatDashboard;

/// <summary>
/// A tiny dependency-free HTTP server (TcpListener, no admin/urlacl needed) that serves the
/// vessel-monitor UI to any device on the boat's LAN and exposes live telemetry + control.
///
/// When it serves <c>app.html</c> it injects a small "remote bridge" so a plain browser (no
/// WebView2 channel) still gets live data (polls <c>/api/telemetry</c>) and can control
/// lights (routes the page's postMessage calls to <c>POST /api/cmd</c>). app.html itself is
/// left untouched.
/// </summary>
public sealed class LocalServer : IDisposable
{
    private readonly TcpListener _listener;
    private readonly string _webRoot;
    private readonly CancellationTokenSource _cts = new();

    /// <summary>Latest telemetry JSON (set by the host each tick); served at /api/telemetry.</summary>
    public volatile string TelemetryJson = "{}";

    /// <summary>Invoked for control messages posted by a remote browser: (cmd, code).</summary>
    public Action<string, uint>? OnCommand;

    /// <summary>AV discovery/control for remote (iPad) clients.</summary>
    public AvService? Av;

    /// <summary>iTach client, for the /api/raw debug dump (reads cached frames only).</summary>
    public Ip2slClient? Itach;

    /// <summary>Shelly motor control (TV lift, shades) over the embedded MQTT broker.</summary>
    public ShellyMqttService? Shelly;
    /// <summary>Persists a saved Shelly motor config (to settings.json). Set by ShellWindow.</summary>
    public Action<List<ShellyMotor>>? OnShellyConfig;

    /// <summary>Full Spotify control of the Sonos amps (Web API).</summary>
    public SpotifyService? Spotify;
    /// <summary>Persists the Spotify Client ID (to settings.json). Set by ShellWindow.</summary>
    public Action<string>? OnSpotifyClientId;

    /// <summary>Scenes + automations: read current (opaque JSON) and persist edits. Set by ShellWindow.</summary>
    public Func<string>? GetScenesJson;
    public Action<string>? OnScenesSave;
    public Action<string>? OnRunScene;           // run a scene by id (in the helm WebView)
    public Func<string>? GetAutomationsJson;
    public Action<string>? OnAutomationsSave;

    /// <summary>Configured cameras (name + feed URL). Served via /api/cameras and proxied via /api/cam.</summary>
    public IReadOnlyList<CameraDef>? Cameras;
    private static readonly HttpClient CamHttp = new() { Timeout = TimeSpan.FromSeconds(8) };

    /// <summary>Expected "Basic &lt;base64(user:pass)&gt;" value, or null when auth is disabled.</summary>
    private readonly string? _expectedAuth;

    // Device allowlist (IPs + MACs, normalized). Unknown devices must request access.
    private sealed record AllowSets(HashSet<string> Ips, HashSet<string> Macs);
    private volatile AllowSets _allow = new(new(StringComparer.OrdinalIgnoreCase), new(StringComparer.OrdinalIgnoreCase));

    /// <summary>A device asking to be let in (keyed by MAC when known, else IP).</summary>
    public sealed class AccessRequest
    {
        public required string Key { get; init; }
        public required string Ip { get; init; }
        public string? Mac { get; init; }
        public string Name { get; set; } = "";
        public DateTime At { get; set; } = DateTime.Now;
        /// <summary>0 = pending, 1 = approved, 2 = denied.</summary>
        public int Status { get; set; }
    }

    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, AccessRequest> _requests = new();

    /// <summary>Pending connection requests, newest first (for the Settings page).</summary>
    public IReadOnlyList<AccessRequest> PendingRequests =>
        _requests.Values.Where(r => r.Status == 0).OrderByDescending(r => r.At).ToList();

    /// <summary>Marks a request approved/denied. The caller persists the allowlist itself.</summary>
    public void ResolveRequest(string key, bool approve)
    {
        if (_requests.TryGetValue(key, out var r)) r.Status = approve ? 1 : 2;
    }

    [DllImport("iphlpapi.dll")]
    private static extern int SendARP(uint destIp, uint srcIp, byte[] macAddr, ref int macLen);

    public int Port { get; }

    private const string RemoteBridge = @"<link rel=""icon"" type=""image/png"" href=""/uploads/lagoon-logo.png"">
<link rel=""apple-touch-icon"" href=""/uploads/lagoon-logo.png"">
<script>(function(){
  if(!(window.chrome&&window.chrome.webview)){
    window.__remote=true;
    window.chrome={webview:{postMessage:function(o){try{fetch('/api/cmd',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify(o)});}catch(e){}}}};
  }
  window.vomsApply=function(d){try{if(!window.LIVE)return;(function m(t,s){for(var k in s){var v=s[k];if(v&&typeof v==='object'&&!Array.isArray(v)&&t[k]&&typeof t[k]==='object')m(t[k],v);else t[k]=v;}})(window.LIVE,d);if('alarm' in d&&typeof S!=='undefined')S.alarm=!!d.alarm;if(d.navStatus&&typeof S!=='undefined'){S.anchorLight=!!d.navStatus.anchor;S.navLights=!!d.navStatus.running;S.electronics=!!d.navStatus.electronics;}if(window.render)render();}catch(e){}};
  window.addEventListener('DOMContentLoaded',function(){
    if(typeof window.setAllZones==='function'){var _s=window.setAllZones;window.setAllZones=function(v){if(window.chrome&&window.chrome.webview)window.chrome.webview.postMessage({cmd:v?'all_on':'all_off'});return _s(v);};}
    setInterval(function(){fetch('/api/telemetry').then(function(r){return r.json();}).then(function(d){window.vomsApply(d);}).catch(function(){});},1500);
    // Poll the TVs' real power state so the remote's power button + control gating reflect reality.
    setInterval(function(){fetch('/api/av/devices').then(function(r){return r.json();}).then(function(d){if(window.avDevices)window.avDevices(d);}).catch(function(){});},3000);
  });
})();</script>";

    public LocalServer(string webRoot, int port, string? user = null, string? pass = null)
    {
        _webRoot = webRoot;
        Port = port;
        _listener = new TcpListener(IPAddress.Any, port);
        if (!string.IsNullOrWhiteSpace(user))
            _expectedAuth = "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes($"{user}:{pass}"));
    }

    /// <summary>Replaces the device allowlist (IPs and/or MACs, any separator style). Empty → open.</summary>
    public void SetAllowList(IEnumerable<string>? entries)
    {
        var ips = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var macs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in entries ?? Enumerable.Empty<string>())
        {
            var e = raw.Trim();
            if (e.Length == 0) continue;
            var hex = e.Replace(":", "").Replace("-", "").Replace(".", "");
            if (hex.Length == 12 && hex.All(Uri.IsHexDigit)) macs.Add(hex.ToLowerInvariant());
            else if (IPAddress.TryParse(e, out var ip)) ips.Add(ip.ToString());
        }
        _allow = new AllowSets(ips, macs);
    }

    /// <summary>MAC of a LAN peer via ARP ("aabbccddeeff"), or null if unresolvable.</summary>
    private static string? MacOf(IPAddress ip)
    {
        try
        {
            if (ip.AddressFamily != AddressFamily.InterNetwork) return null;
            var dest = BitConverter.ToUInt32(ip.GetAddressBytes(), 0);
            var mac = new byte[6];
            int len = 6;
            if (SendARP(dest, 0, mac, ref len) != 0 || len < 6) return null;
            return Convert.ToHexString(mac, 0, 6).ToLowerInvariant();
        }
        catch { return null; }
    }

    /// <summary>True if this client may use the dashboard (loopback always may).</summary>
    private bool IsAllowed(IPAddress ip, out string? mac)
    {
        mac = null;
        if (IPAddress.IsLoopback(ip)) return true;
        var allow = _allow;
        // Empty allowlist = open LAN: the owner's boat network isn't per-device gated (the
        // internet tunnel is still protected by the loopback Basic Auth). This avoids locking out
        // devices like iPads, which rotate their Wi-Fi MAC and would otherwise fail the gate.
        if (allow.Ips.Count == 0 && allow.Macs.Count == 0) { mac = MacOf(ip); return true; }
        if (allow.Ips.Contains(ip.ToString())) { mac = MacOf(ip); return true; }
        mac = MacOf(ip);
        return mac is not null && allow.Macs.Contains(mac);
    }

    public void Start()
    {
        _listener.Start();
        _ = AcceptLoopAsync();
    }

    private async Task AcceptLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await _listener.AcceptTcpClientAsync(_cts.Token); }
            catch { break; }
            _ = HandleAsync(client);
        }
    }

    private async Task HandleAsync(TcpClient client)
    {
        try
        {
            using (client)
            {
                client.NoDelay = true;
                var remoteIp = (client.Client.RemoteEndPoint as IPEndPoint)?.Address ?? IPAddress.Loopback;
                using var stream = client.GetStream();

                // Read headers (up to the blank line).
                var head = new StringBuilder();
                var buf = new byte[1];
                int consecutive = 0;
                while (head.Length < 16384)
                {
                    int n = await stream.ReadAsync(buf, _cts.Token);
                    if (n == 0) break;
                    char c = (char)buf[0];
                    head.Append(c);
                    if (c == '\n') { if (++consecutive == 2) break; }
                    else if (c != '\r') consecutive = 0;
                }

                var lines = head.ToString().Split("\r\n");
                if (lines.Length == 0 || lines[0].Length == 0) return;
                var parts = lines[0].Split(' ');
                if (parts.Length < 2) return;
                string method = parts[0], path = parts[1];
                int q = path.IndexOf('?');
                string rawQuery = q >= 0 ? path[(q + 1)..] : "";
                if (q >= 0) path = path[..q];
                path = Uri.UnescapeDataString(path);   // "%20" → " " for asset filenames with spaces
                var query = rawQuery.Split('&', StringSplitOptions.RemoveEmptyEntries);

                // CORS preflight — a Flutter (or any browser) client sends OPTIONS before a cross-origin
                // POST. Answer it with the CORS headers and no body, before auth (browsers omit creds here).
                if (method == "OPTIONS")
                {
                    await WriteAsync(stream, "204 No Content", "text/plain", Array.Empty<byte>());
                    return;
                }

                // Device gate: unknown IP/MAC devices get a "request access" flow instead of
                // the dashboard. They ask once; the captain accepts on the Settings page and
                // the device's MAC is added to the allowlist automatically.
                if (!IsAllowed(remoteIp, out var remoteMac))
                {
                    var reqKey = remoteMac ?? remoteIp.ToString();

                    if (method == "POST" && path == "/api/request-access")
                    {
                        int rlen = 0;
                        foreach (var l in lines)
                            if (l.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                                int.TryParse(l[15..].Trim(), out rlen);
                        var rbody = new byte[Math.Clamp(rlen, 0, 4096)];
                        int rgot = 0;
                        while (rgot < rbody.Length)
                        {
                            int n = await stream.ReadAsync(rbody.AsMemory(rgot), _cts.Token);
                            if (n == 0) break;
                            rgot += n;
                        }
                        var devName = "";
                        try
                        {
                            using var rdoc = JsonDocument.Parse(Encoding.UTF8.GetString(rbody, 0, rgot));
                            devName = rdoc.RootElement.TryGetProperty("name", out var dn) ? dn.GetString() ?? "" : "";
                        }
                        catch { }
                        var req = _requests.GetOrAdd(reqKey, _ => new AccessRequest
                            { Key = reqKey, Ip = remoteIp.ToString(), Mac = remoteMac });
                        req.Name = devName.Length > 0 ? devName[..Math.Min(40, devName.Length)] : req.Name;
                        req.At = DateTime.Now;
                        if (req.Status == 2) req.Status = 0;   // let a denied device ask again
                        await WriteAsync(stream, "200 OK", "application/json", Encoding.UTF8.GetBytes("{\"ok\":true}"));
                        return;
                    }

                    if (path == "/api/request-status")
                    {
                        var st = _requests.TryGetValue(reqKey, out var r)
                            ? (r.Status == 1 ? "approved" : r.Status == 2 ? "denied" : "pending")
                            : "none";
                        await WriteAsync(stream, "200 OK", "application/json",
                            Encoding.UTF8.GetBytes($"{{\"status\":\"{st}\"}}"));
                        return;
                    }

                    await WriteAsync(stream, "200 OK", "text/html; charset=utf-8",
                        Encoding.UTF8.GetBytes(RequestPage(remoteIp.ToString(), remoteMac,
                            _requests.TryGetValue(reqKey, out var existing) ? existing.Status : -1)));
                    return;
                }

                // Basic Auth applies to LOOPBACK-origin traffic only — that's where a Cloudflare
                // tunnel (or any reverse proxy) lands, and it bypasses the LAN device allowlist.
                // LAN devices are gated by the allowlist above and never see a password prompt.
                if (_expectedAuth is not null && IPAddress.IsLoopback(remoteIp))
                {
                    string? got = null;
                    foreach (var l in lines)
                        if (l.StartsWith("Authorization:", StringComparison.OrdinalIgnoreCase))
                            got = l[14..].Trim();
                    if (!string.Equals(got, _expectedAuth, StringComparison.Ordinal))
                    {
                        var body401 = Encoding.UTF8.GetBytes("Authentication required.");
                        var hdr401 = $"HTTP/1.1 401 Unauthorized\r\nWWW-Authenticate: Basic realm=\"BoatDashboard\"\r\n" +
                                     $"Content-Type: text/plain\r\nContent-Length: {body401.Length}\r\nConnection: close\r\n\r\n";
                        await stream.WriteAsync(Encoding.ASCII.GetBytes(hdr401), _cts.Token);
                        await stream.WriteAsync(body401, _cts.Token);
                        return;
                    }
                }

                if (method == "POST" && path == "/api/cmd")
                {
                    int len = 0;
                    foreach (var l in lines)
                        if (l.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                            int.TryParse(l[15..].Trim(), out len);
                    var body = new byte[Math.Clamp(len, 0, 8192)];
                    int got = 0;
                    while (got < body.Length)
                    {
                        int n = await stream.ReadAsync(body.AsMemory(got), _cts.Token);
                        if (n == 0) break;
                        got += n;
                    }
                    HandleCommand(Encoding.UTF8.GetString(body, 0, got));
                    await WriteAsync(stream, "200 OK", "application/json", Encoding.UTF8.GetBytes("{\"ok\":true}"));
                    return;
                }

                if (path == "/api/telemetry")
                {
                    await WriteAsync(stream, "200 OK", "application/json", Encoding.UTF8.GetBytes(TelemetryJson));
                    return;
                }

                // Debug: dump every iTach channel + field (reads cached frames — no new iTach traffic).
                // Reveals data the dashboard doesn't decode, and expands the digital-status words to bits.
                if (path == "/api/raw" && Itach is not null)
                {
                    await WriteAsync(stream, "200 OK", "application/json", Encoding.UTF8.GetBytes(RawDumpJson(Itach)));
                    return;
                }
                // Raw-bus byte capture for reverse-engineering command codes. ?clear=1 empties the ring first
                // (call it, press a physical button, then call again to see exactly what crossed the wire).
                if (path.StartsWith("/api/rawbytes") && Itach is not null)
                {
                    if (rawQuery.Contains("clear=1")) { Itach.RawClear(); await WriteAsync(stream, "200 OK", "application/json", Encoding.UTF8.GetBytes("{\"cleared\":true}")); return; }
                    var raw = Itach.RawSnapshot();
                    var hex = string.Join(" ", raw.Select(x => x.ToString("X2")));
                    var payload = JsonSerializer.Serialize(new { count = raw.Length, hex });
                    await WriteAsync(stream, "200 OK", "application/json", Encoding.UTF8.GetBytes(payload));
                    return;
                }

                // Camera list (name + index) — never exposes raw URLs to remote clients.
                if (path == "/api/cameras")
                {
                    var cams = Cameras ?? Array.Empty<CameraDef>();
                    var list = cams.Select((c, idx) => new { i = idx, name = string.IsNullOrWhiteSpace(c.Name) ? $"Camera {idx + 1}" : c.Name });
                    await WriteAsync(stream, "200 OK", "application/json", Encoding.UTF8.GetBytes(JsonSerializer.Serialize(list)));
                    return;
                }

                // Camera snapshot proxy: /api/cam?i=N → fetches camera N's feed server-side and returns one JPEG.
                // Works for HTTP/HTTPS snapshot URLs and MJPEG streams (extracts the first frame), so the page
                // just polls this same-origin endpoint — no mixed-content, no CORS, RTSP excluded (needs transcode).
                if (path == "/api/cam")
                {
                    int idx = 0;
                    var qi = query.FirstOrDefault(l => l.StartsWith("i="));
                    if (qi is not null) int.TryParse(qi[2..], out idx);
                    var cams = Cameras ?? Array.Empty<CameraDef>();
                    if (idx < 0 || idx >= cams.Count || string.IsNullOrWhiteSpace(cams[idx].Url))
                    {
                        await WriteAsync(stream, "404 Not Found", "text/plain", Encoding.UTF8.GetBytes("no camera"));
                        return;
                    }
                    var jpeg = await FetchSnapshotAsync(cams[idx].Url);
                    if (jpeg is null)
                        await WriteAsync(stream, "502 Bad Gateway", "text/plain", Encoding.UTF8.GetBytes("camera unreachable"));
                    else
                        await WriteAsync(stream, "200 OK", "image/jpeg", jpeg);
                    return;
                }

                if (path == "/api/av/devices" && Av is not null)
                {
                    await WriteAsync(stream, "200 OK", "application/json", Encoding.UTF8.GetBytes(Av.DevicesJson()));
                    return;
                }
                // Sonos "My Sonos" favorites for the per-zone music picker (?ip=192.168.20.119).
                if (path.StartsWith("/api/av/favorites") && Av is not null)
                {
                    string favIp = "192.168.20.119";
                    var qi = path.IndexOf("ip=", StringComparison.Ordinal);
                    if (qi >= 0) { var s = path.Substring(qi + 3); var amp = s.IndexOf('&'); favIp = amp >= 0 ? s.Substring(0, amp) : s; }
                    var favJson = await Av.SonosFavoritesJsonAsync(favIp);
                    await WriteAsync(stream, "200 OK", "application/json", Encoding.UTF8.GetBytes(favJson));
                    return;
                }
                // Live per-amp now-playing/source (?ip=…) — reflects whatever device actually drives the amp.
                if (path == "/api/sonos/now" && Av is not null)
                {
                    var nip = Qv(query, "ip");
                    if (string.IsNullOrEmpty(nip)) { await WriteAsync(stream, "400 Bad Request", "application/json", Encoding.UTF8.GetBytes("{}")); return; }
                    var nj = await Av.SonosNowPlayingJsonAsync(nip);
                    await WriteAsync(stream, "200 OK", "application/json", Encoding.UTF8.GetBytes(nj));
                    return;
                }
                if (path == "/api/av/discover" && Av is not null)
                {
                    await Av.DiscoverAsync();
                    await WriteAsync(stream, "200 OK", "application/json", Encoding.UTF8.GetBytes(Av.DevicesJson()));
                    return;
                }
                // Manually add a device by IP + protocol (for gear on another subnet that discovery can't reach).
                if (method == "POST" && path == "/api/av/add" && Av is not null)
                {
                    var b = await ReadBodyAsync(stream, lines);
                    string ip = "", proto = "", nm = "";
                    try
                    {
                        using var d = JsonDocument.Parse(b);
                        ip = d.RootElement.TryGetProperty("ip", out var i) ? i.GetString() ?? "" : "";
                        proto = d.RootElement.TryGetProperty("protocol", out var p) ? p.GetString() ?? "" : "";
                        nm = d.RootElement.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                    }
                    catch { }
                    if (ip.Length == 0 || proto.Length == 0)
                        await WriteAsync(stream, "400 Bad Request", "application/json", Encoding.UTF8.GetBytes("{\"error\":\"ip and protocol required\"}"));
                    else
                    {
                        var dev = await Av.AddByIpAsync(ip, proto, string.IsNullOrWhiteSpace(nm) ? null : nm);
                        await WriteAsync(stream, "200 OK", "application/json", Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { added = dev.Id, name = dev.Name })));
                    }
                    return;
                }
                // ---- Shelly motors (TV lift, shades) over MQTT ----
                if (path == "/api/shelly/status" && Shelly is not null)
                {
                    await WriteAsync(stream, "200 OK", "application/json", Encoding.UTF8.GetBytes(Shelly.StatusJson()));
                    return;
                }
                if (path == "/api/shelly/discover" && Shelly is not null)
                {
                    await WriteAsync(stream, "200 OK", "application/json", Encoding.UTF8.GetBytes(await Shelly.DiscoverJsonAsync()));
                    return;
                }
                if (method == "POST" && path == "/api/shelly/provision" && Shelly is not null)
                {
                    var b = await ReadBodyAsync(stream, lines);
                    string sip = "", topic = "";
                    try { using var d = JsonDocument.Parse(b);
                        sip = d.RootElement.TryGetProperty("ip", out var i) ? i.GetString() ?? "" : "";
                        topic = d.RootElement.TryGetProperty("topic", out var t) ? t.GetString() ?? "" : ""; } catch { }
                    bool ok = sip.Length > 0 && await Shelly.ProvisionAsync(sip, topic);
                    await WriteAsync(stream, "200 OK", "application/json", Encoding.UTF8.GetBytes(ok ? "{\"ok\":true}" : "{\"ok\":false}"));
                    return;
                }
                if (method == "GET" && path == "/api/shelly/config" && Shelly is not null)
                {
                    await WriteAsync(stream, "200 OK", "application/json", Encoding.UTF8.GetBytes(Shelly.ConfigJson()));
                    return;
                }
                if (method == "POST" && path == "/api/shelly/config" && Shelly is not null)
                {
                    var b = await ReadBodyAsync(stream, lines);
                    List<ShellyMotor> motors = new();
                    try { motors = JsonSerializer.Deserialize<List<ShellyMotor>>(b) ?? new(); } catch { }
                    Shelly.SetConfig(motors);
                    OnShellyConfig?.Invoke(motors);   // persist to settings.json
                    await WriteAsync(stream, "200 OK", "application/json", Encoding.UTF8.GetBytes("{\"ok\":true}"));
                    return;
                }
                if (method == "POST" && path == "/api/shelly/timer" && Shelly is not null)
                {
                    var b = await ReadBodyAsync(stream, lines);
                    string key = ""; int secs = 0;
                    try { using var d = JsonDocument.Parse(b);
                        key = d.RootElement.TryGetProperty("key", out var k) ? k.GetString() ?? "" : "";
                        secs = d.RootElement.TryGetProperty("secs", out var s) && s.TryGetInt32(out var sv) ? sv : 0; } catch { }
                    if (!string.IsNullOrEmpty(key) && secs > 0)
                    {
                        var updated = Shelly.SetTimer(key, secs);   // updates travel timer + pushes firmware auto-off
                        OnShellyConfig?.Invoke(updated);            // persist to settings.json
                    }
                    await WriteAsync(stream, "200 OK", "application/json", Encoding.UTF8.GetBytes("{\"ok\":true}"));
                    return;
                }
                if (method == "POST" && path == "/api/shelly/set" && Shelly is not null)
                {
                    var b = await ReadBodyAsync(stream, lines);
                    string key = "", act = ""; bool hold = false; double target = -1;
                    try { using var d = JsonDocument.Parse(b);
                        key = d.RootElement.TryGetProperty("key", out var k) ? k.GetString() ?? "" : "";
                        act = d.RootElement.TryGetProperty("action", out var a) ? a.GetString() ?? "" : "";
                        hold = d.RootElement.TryGetProperty("hold", out var h) && (h.ValueKind == JsonValueKind.True || (h.ValueKind == JsonValueKind.String && h.GetString() == "1"));
                        if (d.RootElement.TryGetProperty("target", out var tg) && tg.TryGetDouble(out var tv)) target = tv; } catch { }
                    var kc = key; var ac = act; var hc = hold; var tc = target;
                    if (ac == "goto" && tc >= 0)
                        _ = Task.Run(() => Shelly.GotoAsync(kc, tc / 100.0));   // move to a specific % (HALF etc.)
                    else
                        _ = Task.Run(() => Shelly.CommandAsync(kc, ac, hc));   // fire-and-forget so the UI never blocks
                    await WriteAsync(stream, "200 OK", "application/json", Encoding.UTF8.GetBytes("{\"ok\":true}"));
                    return;
                }

                // ---- Spotify (full Web-API control of the Sonos amps) ----
                if (path == "/api/spotify/status" && Spotify is not null)
                {
                    var js = "{\"clientId\":" + (Spotify.HasClientId ? "true" : "false") + ",\"connected\":" + (Spotify.Connected ? "true" : "false") + "}";
                    await WriteAsync(stream, "200 OK", "application/json", Encoding.UTF8.GetBytes(js));
                    return;
                }
                if (method == "POST" && path == "/api/spotify/clientid" && Spotify is not null)
                {
                    var b = await ReadBodyAsync(stream, lines);
                    string cid = "";
                    try { using var d = JsonDocument.Parse(b); cid = d.RootElement.TryGetProperty("clientId", out var c) ? c.GetString()?.Trim() ?? "" : ""; } catch { }
                    Spotify.SetClientId(cid);   // keep existing refresh token
                    OnSpotifyClientId?.Invoke(cid);
                    await WriteAsync(stream, "200 OK", "application/json", Encoding.UTF8.GetBytes("{\"ok\":true}"));
                    return;
                }
                if (path == "/api/spotify/login" && Spotify is not null)
                {
                    var url = Spotify.BuildAuthUrl();
                    try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true }); } catch { }
                    await WriteAsync(stream, "200 OK", "application/json", Encoding.UTF8.GetBytes("{\"ok\":true,\"url\":" + JsonSerializer.Serialize(url) + "}"));
                    return;
                }
                if (path == "/spotify/callback" && Spotify is not null)
                {
                    string code = Qv(query, "code"), st = Qv(query, "state");
                    bool ok = !string.IsNullOrEmpty(code) && await Spotify.HandleCallbackAsync(code, st);
                    var html = "<!doctype html><meta name=viewport content='width=device-width,initial-scale=1'><body style='background:#0b1218;color:#e8f0f2;font-family:system-ui;text-align:center;padding-top:20vh'><h2>" + (ok ? "✓ Spotify connected" : "✗ Spotify connection failed") + "</h2><p style='color:#7d939e'>You can close this tab and return to the dashboard.</p></body>";
                    await WriteAsync(stream, ok ? "200 OK" : "400 Bad Request", "text/html", Encoding.UTF8.GetBytes(html));
                    return;
                }
                if (path == "/api/spotify/search" && Spotify is not null)
                {
                    var js = await Spotify.SearchJsonAsync(Qv(query, "q"));
                    await WriteAsync(stream, "200 OK", "application/json", Encoding.UTF8.GetBytes(js));
                    return;
                }
                if (path == "/api/spotify/devices" && Spotify is not null)
                {
                    var js = await Spotify.DevicesJsonAsync();
                    await WriteAsync(stream, "200 OK", "application/json", Encoding.UTF8.GetBytes(js));
                    return;
                }
                if (path == "/api/spotify/now" && Spotify is not null)
                {
                    var js = await Spotify.NowPlayingJsonAsync();
                    await WriteAsync(stream, "200 OK", "application/json", Encoding.UTF8.GetBytes(js));
                    return;
                }
                if (method == "POST" && path == "/api/spotify/play" && Spotify is not null)
                {
                    var b = await ReadBodyAsync(stream, lines);
                    string zone = "", uri = ""; bool ctx = false;
                    try { using var d = JsonDocument.Parse(b);
                        zone = d.RootElement.TryGetProperty("zone", out var z) ? z.GetString() ?? "" : "";
                        uri = d.RootElement.TryGetProperty("uri", out var u) ? u.GetString() ?? "" : "";
                        ctx = d.RootElement.TryGetProperty("ctx", out var c) && (c.ValueKind == JsonValueKind.True || (c.ValueKind == JsonValueKind.String && c.GetString() == "1")); } catch { }
                    var zc = zone; var uc = uri; var cc = ctx;
                    _ = Task.Run(() => Spotify.PlayAsync(zc, uc, cc));
                    await WriteAsync(stream, "200 OK", "application/json", Encoding.UTF8.GetBytes("{\"ok\":true}"));
                    return;
                }
                if (method == "POST" && path == "/api/spotify/control" && Spotify is not null)
                {
                    var b = await ReadBodyAsync(stream, lines);
                    string zone = "", act = "";
                    try { using var d = JsonDocument.Parse(b);
                        zone = d.RootElement.TryGetProperty("zone", out var z) ? z.GetString() ?? "" : "";
                        act = d.RootElement.TryGetProperty("action", out var a) ? a.GetString() ?? "" : ""; } catch { }
                    var zc = zone; var ac2 = act;
                    _ = Task.Run(() => Spotify.ControlAsync(zc, ac2));
                    await WriteAsync(stream, "200 OK", "application/json", Encoding.UTF8.GetBytes("{\"ok\":true}"));
                    return;
                }

                // ---- Scenes + automations ----
                if (path == "/api/scenes" && method == "GET" && GetScenesJson is not null)
                {
                    await WriteAsync(stream, "200 OK", "application/json", Encoding.UTF8.GetBytes(GetScenesJson() ?? "[]"));
                    return;
                }
                if (path == "/api/scenes" && method == "POST" && OnScenesSave is not null)
                {
                    var b = await ReadBodyAsync(stream, lines);
                    try { using var _ = JsonDocument.Parse(b); OnScenesSave(b); } catch { }   // only persist valid JSON
                    await WriteAsync(stream, "200 OK", "application/json", Encoding.UTF8.GetBytes("{\"ok\":true}"));
                    return;
                }
                if (path == "/api/scene/run" && method == "POST" && OnRunScene is not null)
                {
                    var b = await ReadBodyAsync(stream, lines);
                    string id = "";
                    try { using var d = JsonDocument.Parse(b); id = d.RootElement.TryGetProperty("id", out var i) ? i.GetString() ?? "" : ""; } catch { }
                    OnRunScene(id);
                    await WriteAsync(stream, "200 OK", "application/json", Encoding.UTF8.GetBytes("{\"ok\":true}"));
                    return;
                }
                if (path == "/api/automations" && method == "GET" && GetAutomationsJson is not null)
                {
                    await WriteAsync(stream, "200 OK", "application/json", Encoding.UTF8.GetBytes(GetAutomationsJson() ?? "[]"));
                    return;
                }
                if (path == "/api/automations" && method == "POST" && OnAutomationsSave is not null)
                {
                    var b = await ReadBodyAsync(stream, lines);
                    try { using var _ = JsonDocument.Parse(b); OnAutomationsSave(b); } catch { }
                    await WriteAsync(stream, "200 OK", "application/json", Encoding.UTF8.GetBytes("{\"ok\":true}"));
                    return;
                }

                if (method == "POST" && path == "/api/av/control" && Av is not null)
                {
                    var b = await ReadBodyAsync(stream, lines);
                    string devId = "", act = "", val = "";
                    try
                    {
                        using var d = JsonDocument.Parse(b);
                        devId = d.RootElement.TryGetProperty("id", out var i) ? i.GetString() ?? "" : "";
                        act = d.RootElement.TryGetProperty("action", out var a) ? a.GetString() ?? "" : "";
                        val = d.RootElement.TryGetProperty("value", out var v) ? (v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : v.ToString()) : "";
                    }
                    catch { }
                    // Fire-and-forget: respond instantly so a slow or powered-off device (e.g. a Samsung TV
                    // whose WebSocket can hang ~45s) can NEVER stall the UI or pile up connections. The device
                    // reacts when ready; the browser UI updates optimistically and ignores this response body.
                    var devIdC = devId; var actC = act; var valC = val;
                    _ = Task.Run(() => Av.ControlAsync(devIdC, actC, string.IsNullOrEmpty(valC) ? null : valC));
                    await WriteAsync(stream, "200 OK", "application/json", Encoding.UTF8.GetBytes("{\"ok\":true}"));
                    return;
                }

                await ServeFileAsync(stream, path);
            }
        }
        catch { /* per-connection errors are non-fatal */ }
    }

    /// <summary>Reads the request body using the Content-Length header.</summary>
    /// <summary>Get a URL-decoded query-string value from the parsed "k=v" array ("" if absent).</summary>
    private static string Qv(string[] query, string key)
    {
        var pre = key + "=";
        var hit = query.FirstOrDefault(l => l.StartsWith(pre, StringComparison.Ordinal));
        if (hit is null) return "";
        try { return Uri.UnescapeDataString(hit[pre.Length..].Replace('+', ' ')); } catch { return hit[pre.Length..]; }
    }

    private async Task<string> ReadBodyAsync(NetworkStream stream, string[] lines)
    {
        int len = 0;
        foreach (var l in lines)
            if (l.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                int.TryParse(l[15..].Trim(), out len);
        var body = new byte[Math.Clamp(len, 0, 8192)];
        int got = 0;
        while (got < body.Length)
        {
            int n = await stream.ReadAsync(body.AsMemory(got), _cts.Token);
            if (n == 0) break;
            got += n;
        }
        return Encoding.UTF8.GetString(body, 0, got);
    }

    /// <summary>
    /// Builds a JSON dump of every iTach channel: raw hex + decimal fields, a best-effort decode
    /// (reconciling the Scheiber/E-Plex GUI field map with the app's verified indices), and a bit
    /// expansion of the digital-status words so undecoded discrete inputs (bilge, pumps, breakers)
    /// are visible. Reads cached frames only — sends nothing to the Global Cache.
    /// </summary>
    private static string RawDumpJson(Ip2slClient itach)
    {
        var chans = itach.SnapshotChannels();
        var outChans = new List<object>();
        foreach (var ch in chans.Keys.OrderBy(k => k))
        {
            var vals = chans[ch];
            var fields = new List<object>();
            for (int i = 0; i < vals.Length; i++)
            {
                int v = vals[i];
                fields.Add(new
                {
                    i,
                    hex = v.ToString("X4"),
                    dec = v,
                    bits = Convert.ToString(v & 0xFFFF, 2).PadLeft(16, '0'),
                    guess = GuessField(ch, i, v),
                });
            }
            outChans.Add(new { channel = ch, count = vals.Length, fields });
        }
        return JsonSerializer.Serialize(new
        {
            note = "raw iTach channel dump (cached frames; no new iTach traffic). 'guess' = best-effort decode.",
            channels_seen = chans.Count,
            channels = outChans,
        });
    }

    // Best-effort per-field interpretation. Verified indices come from MqttAgent (live-confirmed);
    // others are hypotheses from the Scheiber Bloc-7 / E-Plex GUI field map, marked with '?'.
    private static string GuessField(string ch, int i, int v)
    {
        double d10 = v / 10.0;
        return (ch, i) switch
        {
            // CH00 — verified voltages + water; hypothesised amps at odd indices
            ("00", 2) => $"genset batt {d10:0.0} V (verified)",
            ("00", 3) => $"? genset batt amps ({v - 512})",
            ("00", 4) => $"port-engine batt {d10:0.0} V (verified)",
            ("00", 5) => $"? port-engine batt amps ({v - 512})",
            ("00", 6) => $"stbd-engine batt {d10:0.0} V (verified)",
            ("00", 7) => $"? stbd-engine batt amps ({v - 512})",
            ("00", 8) => $"service batt {d10:0.0} V (verified)",
            ("00", 9) => $"? service batt amps ({(v - 32768) / 10.0:0.0})",
            ("00", 10) => $"fresh water port {v}% (verified)",
            ("00", 11) => $"fresh water stbd {v}% (verified)",
            ("00", 12) => "? digital-status word 1 (see bits)",
            ("00", 13) => "? digital-status word 2 (see bits)",
            ("00", 14) => "? digital-status word 3 (see bits)",
            ("00", 15) => "? digital-status word 4 (see bits)",
            // CH01 — SOC / setup / scheiber digital
            ("01", 0) => $"? service batt SOC {d10:0.0}%",
            ("01", 1) => "? cabin setup / digital",
            // CH02 — AC: shore1/shore2/gen verified; inverter hypothesised at 9-11
            ("02", 0) => $"shore-1 {v} V (verified)",
            ("02", 1) => $"shore-1 {v} A (verified)",
            ("02", 2) => $"shore-1 {d10:0.0} Hz (verified)",
            ("02", 3) => $"shore-2 {v} V (verified)",
            ("02", 4) => $"shore-2 {v} A (verified)",
            ("02", 5) => $"shore-2 {d10:0.0} Hz (verified)",
            ("02", 6) => $"generator {v} V (verified)",
            ("02", 7) => $"generator {v} A (verified)",
            ("02", 8) => $"generator {d10:0.0} Hz (verified)",
            ("02", 9) => $"? inverter {v} V",
            ("02", 10) => $"? inverter {v} A",
            ("02", 11) => $"? inverter {d10:0.0} Hz",
            // CH03 — fuel tanks verified; transfer/time hypothesised
            ("03", 2) => $"fuel fwd-port {v}% (verified)",
            ("03", 3) => $"fuel fwd-stbd {v}% (verified)",
            ("03", 10) => $"fuel aft-port {v}% (verified)",
            ("03", 11) => $"fuel aft-stbd {v}% (verified)",
            _ => "",
        };
    }

    /// <summary>
    /// Fetches one JPEG frame from a camera URL. A plain HTTP/HTTPS JPEG snapshot is returned as-is;
    /// an MJPEG (multipart/x-mixed-replace) stream is read until the first complete JPEG frame
    /// (SOI 0xFFD8 … EOI 0xFFD9) is found. RTSP is unsupported (returns null — needs a transcoder).
    /// </summary>
    internal static async Task<byte[]?> FetchSnapshotAsync(string url)
    {
        if (url.StartsWith("rtsp://", StringComparison.OrdinalIgnoreCase)) return null; // browser/WebView can't play RTSP
        try
        {
            using var resp = await CamHttp.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
            if (!resp.IsSuccessStatusCode) return null;
            var ct = resp.Content.Headers.ContentType?.MediaType ?? "";

            if (ct.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
                return await resp.Content.ReadAsByteArrayAsync();

            // MJPEG (or unknown): scan the stream for the first full JPEG frame.
            await using var s = await resp.Content.ReadAsStreamAsync();
            var buf = new byte[64 * 1024];
            var acc = new List<byte>(128 * 1024);
            int start = -1;
            int total = 0;
            while (total < 4 * 1024 * 1024) // 4 MB safety cap
            {
                int n = await s.ReadAsync(buf);
                if (n <= 0) break;
                total += n;
                for (int i = 0; i < n; i++) acc.Add(buf[i]);
                for (int i = 1; i < acc.Count; i++)
                {
                    if (start < 0 && acc[i - 1] == 0xFF && acc[i] == 0xD8) start = i - 1;
                    else if (start >= 0 && acc[i - 1] == 0xFF && acc[i] == 0xD9)
                        return acc.GetRange(start, i - start + 1).ToArray();
                }
            }
            return null;
        }
        catch { return null; }
    }

    private void HandleCommand(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var cmd = root.TryGetProperty("cmd", out var c) ? c.GetString() ?? "" : "";
            uint code = 0;
            if (root.TryGetProperty("code", out var cd) && cd.TryGetInt64(out var n)) code = (uint)n;
            OnCommand?.Invoke(cmd, code);
        }
        catch { }
    }

    private async Task ServeFileAsync(NetworkStream stream, string path)
    {
        if (path is "/" or "/index.html" or "/app.html")
        {
            var file = Path.Combine(_webRoot, "app.html");
            if (!File.Exists(file)) { await WriteAsync(stream, "404 Not Found", "text/plain", Encoding.UTF8.GetBytes("no app")); return; }
            var html = await File.ReadAllTextAsync(file, _cts.Token);
            int head = html.IndexOf("</head>", StringComparison.OrdinalIgnoreCase);
            html = head >= 0 ? html.Insert(head, RemoteBridge) : RemoteBridge + html;
            await WriteAsync(stream, "200 OK", "text/html; charset=utf-8", Encoding.UTF8.GetBytes(html));
            return;
        }

        // Static assets under the web root (e.g. /uploads/*). Guard against traversal.
        var rel = path.TrimStart('/').Replace('/', Path.DirectorySeparatorChar);
        var full = Path.GetFullPath(Path.Combine(_webRoot, rel));
        if (!full.StartsWith(Path.GetFullPath(_webRoot), StringComparison.OrdinalIgnoreCase) || !File.Exists(full))
        {
            await WriteAsync(stream, "404 Not Found", "text/plain", Encoding.UTF8.GetBytes("not found"));
            return;
        }
        var bytes = await File.ReadAllBytesAsync(full, _cts.Token);
        await WriteAsync(stream, "200 OK", ContentType(full), bytes, cacheSeconds: 86400);  // static asset → cache 1 day
    }

    /// <summary>The page an unknown device sees: request access + wait for helm approval.</summary>
    private static string RequestPage(string ip, string? mac, int status)
    {
        var macTxt = mac is null ? "unknown" :
            string.Join(":", Enumerable.Range(0, 6).Select(i => mac.Substring(i * 2, 2)));
        var stateJs = status switch { 0 => "'pending'", 1 => "'approved'", 2 => "'denied'", _ => "null" };
        return @"<!doctype html><html><head><meta name=""viewport"" content=""width=device-width, initial-scale=1"">
<title>Lagoon 630 — Connect</title></head>
<body style=""background:#0a1014;color:#e8f0f2;font-family:-apple-system,'Helvetica Neue',sans-serif;display:flex;align-items:center;justify-content:center;min-height:100vh;margin:0;"">
<div style=""text-align:center;max-width:420px;padding:24px;"">
  <div style=""font-size:22px;font-weight:600;letter-spacing:6px;"">LAGOON</div>
  <div style=""font-size:12px;letter-spacing:2px;color:#7d939e;margin-top:4px;"">630 MOTOR YACHT · VESSEL MONITOR</div>
  <div id=""box"" style=""margin-top:34px;background:#131b22;border:1px solid #1f2b34;border-radius:14px;padding:26px;"">
    <div id=""ask"">
      <div style=""font-size:15px;font-weight:600;"">This device is not paired with the vessel.</div>
      <div style=""font-size:13px;color:#9fb4be;margin-top:10px;line-height:1.5;"">Request access — the captain accepts it on the helm dashboard (⚙ SETUP → Network).</div>
      <input id=""nm"" placeholder=""Device name (e.g. Salon iPad)"" maxlength=""40""
        style=""margin-top:18px;width:100%;box-sizing:border-box;height:46px;padding:0 14px;border-radius:10px;background:#0d151b;border:1px solid #2a3844;color:#e8f0f2;font-size:14px;outline:none;"">
      <div onclick=""ask()"" style=""margin-top:14px;height:48px;display:flex;align-items:center;justify-content:center;border-radius:10px;background:#2fc4d1;color:#0a1014;font-size:13px;font-weight:700;letter-spacing:2px;cursor:pointer;"">REQUEST ACCESS</div>
    </div>
    <div id=""wait"" style=""display:none;"">
      <div style=""font-size:15px;font-weight:600;"">Waiting for approval…</div>
      <div style=""font-size:13px;color:#9fb4be;margin-top:10px;line-height:1.5;"">Accept this device on the helm dashboard:<br>⚙ SETUP → NETWORK → Connection requests.</div>
      <div style=""margin-top:16px;color:#2fc4d1;font-size:26px;"" id=""dots"">·</div>
    </div>
    <div id=""denied"" style=""display:none;"">
      <div style=""font-size:15px;font-weight:600;color:#ff8082;"">Request declined.</div>
      <div onclick=""showAsk()"" style=""margin-top:16px;height:44px;display:flex;align-items:center;justify-content:center;border-radius:10px;border:1px solid #2a3844;color:#9fb4be;font-size:12px;font-weight:700;letter-spacing:1.5px;cursor:pointer;"">ASK AGAIN</div>
    </div>
  </div>
  <div style=""margin-top:14px;font-size:11px;color:#4a5b66;font-family:monospace;"">IP " + ip + @" · MAC " + macTxt + @"</div>
</div>
<script>
var st=" + stateJs + @";
function show(id){['ask','wait','denied'].forEach(function(x){document.getElementById(x).style.display=x===id?'block':'none';});}
function showAsk(){show('ask');}
function ask(){
  var n=document.getElementById('nm').value||'Unnamed device';
  fetch('/api/request-access',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({name:n})})
    .then(function(){show('wait');});
}
function poll(){
  fetch('/api/request-status').then(function(r){return r.json();}).then(function(d){
    if(d.status==='approved'){location.reload();}
    else if(d.status==='denied'){show('denied');}
    else if(d.status==='pending'){show('wait');}
  }).catch(function(){});
  var el=document.getElementById('dots'); if(el){el.textContent=el.textContent.length>=5?'·':el.textContent+' ·';}
}
if(st==='pending')show('wait'); else if(st==='denied')show('denied');
setInterval(poll,3000);
</script></body></html>";
    }

    private static string ContentType(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".html" => "text/html; charset=utf-8",
        ".png" => "image/png",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".svg" => "image/svg+xml",
        ".json" => "application/json",
        ".js" => "text/javascript",
        ".css" => "text/css",
        _ => "application/octet-stream",
    };

    private async Task WriteAsync(NetworkStream stream, string status, string contentType, byte[] body, int cacheSeconds = 0)
    {
        // Telemetry/API stay no-store; static assets (images/css/js) get a real max-age so the iPad
        // caches them — otherwise the 1 s dashboard re-render re-fetches the boat image and it blinks.
        var cache = cacheSeconds > 0 ? $"Cache-Control: max-age={cacheSeconds}" : "Cache-Control: no-store, no-cache, must-revalidate, max-age=0";
        var header = $"HTTP/1.1 {status}\r\nContent-Type: {contentType}\r\nContent-Length: {body.Length}\r\n" +
                     "Access-Control-Allow-Origin: *\r\nAccess-Control-Allow-Methods: GET, POST, OPTIONS\r\n" +
                     "Access-Control-Allow-Headers: Content-Type, Authorization\r\n" +
                     cache + "\r\nConnection: close\r\n\r\n";
        var head = Encoding.ASCII.GetBytes(header);
        await stream.WriteAsync(head, _cts.Token);
        await stream.WriteAsync(body, _cts.Token);
        await stream.FlushAsync(_cts.Token);
    }

    /// <summary>First non-loopback IPv4 address, for showing the LAN URL.</summary>
    public static string LocalIPv4()
    {
        // Prefer an interface that actually has a default gateway (i.e. is routable) over an isolated
        // segment. This PC has a dead 192.168.0.x NIC with no gateway; announcing its IP to the MFD /
        // LAN clients gives them a URL nothing can reach ("Failed to load the page"). Pick a routable one.
        try
        {
            string? fallback = null;
            foreach (var ni in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up) continue;
                if (ni.NetworkInterfaceType == System.Net.NetworkInformation.NetworkInterfaceType.Loopback) continue;
                var props = ni.GetIPProperties();
                bool hasGw = props.GatewayAddresses.Any(g =>
                    g.Address.AddressFamily == AddressFamily.InterNetwork && !g.Address.Equals(IPAddress.Any));
                foreach (var ua in props.UnicastAddresses)
                {
                    if (ua.Address.AddressFamily != AddressFamily.InterNetwork || IPAddress.IsLoopback(ua.Address)) continue;
                    if (hasGw) return ua.Address.ToString();   // routable — the address clients can reach
                    fallback ??= ua.Address.ToString();
                }
            }
            return fallback ?? "127.0.0.1";
        }
        catch { }
        return "127.0.0.1";
    }
    /// <summary>All up, non-loopback IPv4 addresses (routable ones first).</summary>
    public static IReadOnlyList<string> AllLocalIPv4()
    {
        var withGw = new List<string>(); var without = new List<string>();
        try
        {
            foreach (var ni in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up) continue;
                if (ni.NetworkInterfaceType == System.Net.NetworkInformation.NetworkInterfaceType.Loopback) continue;
                var props = ni.GetIPProperties();
                bool hasGw = props.GatewayAddresses.Any(g =>
                    g.Address.AddressFamily == AddressFamily.InterNetwork && !g.Address.Equals(IPAddress.Any));
                foreach (var ua in props.UnicastAddresses)
                {
                    if (ua.Address.AddressFamily != AddressFamily.InterNetwork || IPAddress.IsLoopback(ua.Address)) continue;
                    (hasGw ? withGw : without).Add(ua.Address.ToString());
                }
            }
        }
        catch { }
        withGw.AddRange(without);
        return withGw;
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _listener.Stop(); } catch { }
        _cts.Dispose();
    }
}
