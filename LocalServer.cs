using System.IO;
using System.Net;
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
  window.vomsApply=function(d){try{if(!window.LIVE)return;(function m(t,s){for(var k in s){var v=s[k];if(v&&typeof v==='object'&&!Array.isArray(v)&&t[k]&&typeof t[k]==='object')m(t[k],v);else t[k]=v;}})(window.LIVE,d);if('alarm' in d&&typeof S!=='undefined')S.alarm=!!d.alarm;if(window.render)render();}catch(e){}};
  window.addEventListener('DOMContentLoaded',function(){
    if(typeof window.setAllZones==='function'){var _s=window.setAllZones;window.setAllZones=function(v){if(window.chrome&&window.chrome.webview)window.chrome.webview.postMessage({cmd:v?'all_on':'all_off'});return _s(v);};}
    setInterval(function(){fetch('/api/telemetry').then(function(r){return r.json();}).then(function(d){window.vomsApply(d);}).catch(function(){});},2000);
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
                if (q >= 0) path = path[..q];
                path = Uri.UnescapeDataString(path);   // "%20" → " " for asset filenames with spaces

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

                if (path == "/api/av/devices" && Av is not null)
                {
                    await WriteAsync(stream, "200 OK", "application/json", Encoding.UTF8.GetBytes(Av.DevicesJson()));
                    return;
                }
                if (path == "/api/av/discover" && Av is not null)
                {
                    await Av.DiscoverAsync();
                    await WriteAsync(stream, "200 OK", "application/json", Encoding.UTF8.GetBytes(Av.DevicesJson()));
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
                    var res = await Av.ControlAsync(devId, act, string.IsNullOrEmpty(val) ? null : val);
                    await WriteAsync(stream, "200 OK", "application/json", Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { result = res })));
                    return;
                }

                await ServeFileAsync(stream, path);
            }
        }
        catch { /* per-connection errors are non-fatal */ }
    }

    /// <summary>Reads the request body using the Content-Length header.</summary>
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
        await WriteAsync(stream, "200 OK", ContentType(full), bytes);
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

    private async Task WriteAsync(NetworkStream stream, string status, string contentType, byte[] body)
    {
        var header = $"HTTP/1.1 {status}\r\nContent-Type: {contentType}\r\nContent-Length: {body.Length}\r\n" +
                     "Access-Control-Allow-Origin: *\r\nCache-Control: no-store\r\nConnection: close\r\n\r\n";
        var head = Encoding.ASCII.GetBytes(header);
        await stream.WriteAsync(head, _cts.Token);
        await stream.WriteAsync(body, _cts.Token);
        await stream.FlushAsync(_cts.Token);
    }

    /// <summary>First non-loopback IPv4 address, for showing the LAN URL.</summary>
    public static string LocalIPv4()
    {
        try
        {
            foreach (var ni in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up) continue;
                if (ni.NetworkInterfaceType == System.Net.NetworkInformation.NetworkInterfaceType.Loopback) continue;
                foreach (var ua in ni.GetIPProperties().UnicastAddresses)
                    if (ua.Address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(ua.Address))
                        return ua.Address.ToString();
            }
        }
        catch { }
        return "127.0.0.1";
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _listener.Stop(); } catch { }
        _cts.Dispose();
    }
}
