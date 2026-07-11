using System.IO;
using System.Net;
using System.Net.Sockets;
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

    public int Port { get; }

    private const string RemoteBridge = @"<link rel=""icon"" type=""image/png"" href=""/uploads/lagoon-logo.png"">
<link rel=""apple-touch-icon"" href=""/uploads/lagoon-logo.png"">
<script>(function(){
  if(!(window.chrome&&window.chrome.webview)){
    window.chrome={webview:{postMessage:function(o){try{fetch('/api/cmd',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify(o)});}catch(e){}}}};
  }
  window.vomsApply=function(d){try{if(!window.LIVE)return;(function m(t,s){for(var k in s){var v=s[k];if(v&&typeof v==='object'&&!Array.isArray(v)&&t[k]&&typeof t[k]==='object')m(t[k],v);else t[k]=v;}})(window.LIVE,d);if('alarm' in d&&typeof S!=='undefined')S.alarm=!!d.alarm;if(window.render)render();}catch(e){}};
  window.addEventListener('DOMContentLoaded',function(){
    if(typeof window.setAllZones==='function'){var _s=window.setAllZones;window.setAllZones=function(v){if(window.chrome&&window.chrome.webview)window.chrome.webview.postMessage({cmd:v?'all_on':'all_off'});return _s(v);};}
    setInterval(function(){fetch('/api/telemetry').then(function(r){return r.json();}).then(function(d){window.vomsApply(d);}).catch(function(){});},2000);
  });
})();</script>";

    public LocalServer(string webRoot, int port)
    {
        _webRoot = webRoot;
        Port = port;
        _listener = new TcpListener(IPAddress.Any, port);
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

                await ServeFileAsync(stream, path);
            }
        }
        catch { /* per-connection errors are non-fatal */ }
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
