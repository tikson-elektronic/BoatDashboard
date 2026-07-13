using System.ComponentModel;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using Makaretu.Dns;
using Microsoft.Web.WebView2.Core;

namespace BoatDashboard;

/// <summary>
/// Hosts the Lagoon 630 "Vessel Monitor" design (WebUI/app.html) in a WebView2 and
/// feeds it live iTach telemetry. The HTML is the design handoff verbatim; the only
/// hooks are a <c>LIVE</c> object it reads from and a small message bridge injected here.
/// </summary>
public partial class ShellWindow : Window
{
    private readonly Ip2slClient _client = new();
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(1000) };
    private AppSettings _settings = SettingsStore.Load();
    private bool _kiosk;

    public const int HttpPort = 8080;
    private LocalServer? _server;
    private MulticastService? _mdns;
    private ServiceDiscovery? _sd;
    private CloudflareTunnelService? _cf;

    private MqttAgent? _mqttAgent;   // onboard VOMS agent (publishes to the Alive cloud when paired)
    private readonly AvService _av = new();   // AV device discovery + control (TVs, amplifiers)
    private readonly MemoryStore _memory = new();
    private AutomationService? _autos;
    private VisionService? _vision;
    private NmeaService? _nmea;
    private NavicoMfdService? _navico;

    // Claude assistant + offline voice. _vm mirrors live readings for the assistant's get_status tool.
    private readonly DashboardViewModel _vm = new();
    private ClaudeAssistant? _assistant;
    private VoiceService? _voice;
    private bool _replyAloud;   // speak the next reply (query came in by voice)

    // Injected before any page script. Defines window.vomsApply (live-data merge),
    // routes the lighting master switches to the real iTach, and adds a SETUP entry.
    private const string BridgeJs = @"
window.vomsApply = function(d){
  try{
    if(!window.LIVE) return;
    (function merge(t,s){ for(var k in s){ var v=s[k];
      if(v && typeof v==='object' && !Array.isArray(v) && t[k] && typeof t[k]==='object') merge(t[k],v);
      else t[k]=v; } })(window.LIVE, d);
    if('alarm' in d && typeof S!=='undefined') S.alarm = !!d.alarm;
    if(window.render) window.render();
  }catch(e){}
};
function __vpost(o){ try{ window.chrome.webview.postMessage(o); }catch(e){} }
function __vsetup(){
  /* Put SYSTEM SETUP (the WPF admin dialog) at the top of the design's SETTINGS page. */
  var main=document.getElementById('main');
  if(!main || typeof S==='undefined' || S.screen!=='settings') return;
  if(document.getElementById('vSetupBtn')) return;
  var b=document.createElement('div'); b.id='vSetupBtn';
  b.style.cssText='margin-bottom:18px;padding:18px 22px;border-radius:14px;border:1px solid #2fc4d1;background:rgba(47,196,209,0.08);cursor:pointer;display:flex;align-items:center;gap:14px;';
  b.innerHTML='<div style=""font-size:22px;line-height:1;"">⚙</div><div><div style=""font-size:14px;font-weight:700;letter-spacing:1.5px;color:#2fc4d1;"">SYSTEM SETUP</div><div style=""font-size:12px;color:#9fb4be;margin-top:2px;"">Vessel pairing · Network · Kiosk · Logs · Claude key</div></div>';
  b.onclick=function(){ __vpost({cmd:'settings'}); };
  main.insertBefore(b, main.firstChild);
}
window.addEventListener('DOMContentLoaded', function(){
  if(typeof window.setAllZones==='function'){
    var _saz=window.setAllZones;
    window.setAllZones=function(v){ __vpost({cmd: v?'all_on':'all_off'}); return _saz(v); };
  }
  if(typeof window.render==='function'){
    var _r=window.render;
    window.render=function(){ var x=_r.apply(this,arguments); __vsetup(); return x; };
  }
  __vsetup();
});
";

    public ShellWindow()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        StateChanged += OnStateChanged;
        Closed += (_, _) =>
        {
            _timer.Stop();
            _client.Dispose();
            try { _server?.Dispose(); } catch { }
            try { _cf?.Dispose(); } catch { }
            try { _sd?.Dispose(); _mdns?.Dispose(); } catch { }
            try { _voice?.Dispose(); } catch { }
            try { _ = _mqttAgent?.DisposeAsync(); } catch { }
            try { _av.StopBackgroundScan(); } catch { }
            try { _autos?.Stop(); } catch { }
            try { _vision?.Dispose(); } catch { }
            try { _nmea?.Dispose(); } catch { }
            try { _navico?.Dispose(); } catch { }
        };
    }

    private async void OnLoaded(object? sender, RoutedEventArgs e)
    {
        var userData = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "BoatDashboard", "WebView2");
        Directory.CreateDirectory(userData);

        var env = await CoreWebView2Environment.CreateAsync(null, userData);
        await Web.EnsureCoreWebView2Async(env);

        var core = Web.CoreWebView2;
        core.Settings.AreDefaultContextMenusEnabled = false;
        core.Settings.IsStatusBarEnabled = false;
        core.Settings.AreDevToolsEnabled = true; // handy during bring-up

        await core.AddScriptToExecuteOnDocumentCreatedAsync(BridgeJs);
        core.WebMessageReceived += OnWebMessage;
        core.NavigationCompleted += (_, _) => { ApplyCursor(); ApplyBlink(); PushAssistantState(); PushCameras(); StartCameraPush(); };
        // Self-heal: if the WebView render/browser process dies, reload the UI instead of showing a blank screen.
        core.ProcessFailed += (_, args) =>
        {
            Ip2slClient.Log("[selfheal] webview process failed: " + args.ProcessFailedKind);
            Dispatcher.BeginInvoke(() => { try { Web.CoreWebView2?.Navigate("https://vessel.local/app.html"); } catch { } });
        };

        // Serve the WebUI folder over a virtual host so relative asset paths resolve.
        var webDir = Path.Combine(AppContext.BaseDirectory, "WebUI");
        core.SetVirtualHostNameToFolderMapping(
            "vessel.local", webDir, CoreWebView2HostResourceAccessKind.Allow);

        // Intercept /api/cam* so camera feeds are same-origin in the WebView (no mixed-content block).
        core.AddWebResourceRequestedFilter("https://vessel.local/api/*", CoreWebView2WebResourceContext.All);
        core.WebResourceRequested += OnWebResourceRequested;

        core.Navigate("https://vessel.local/app.html");

        ApplyKiosk();

        _client.Start();
        _timer.Tick += OnTick;
        _timer.Start();

        StartLanServices(webDir);
        StartVesselBrain();
        ApplyAssistant();
        StartOnboardAgent();
    }

    /// <summary>Starts the always-on "being": AV discovery/watch + the automation engine, and
    /// makes new-device discoveries speak to the owner.</summary>
    private void StartVesselBrain()
    {
        _av.OnNewDevice += OnNewAvDevice;
        _av.StartBackgroundScan();

        _autos = new AutomationService(RunAutoActionAsync, SensorValue);
        _autos.Lat = _settings.VesselLat;
        _autos.Lon = _settings.VesselLon;
        _autos.Start();

        // YOLO visual sensors — analyses camera frames if a model is present (graceful no-op otherwise).
        _vision = new VisionService(() => _settings.Cameras ?? new());
        _vision.OnDetections += OnVisionDetections;
        _vision.Start();

        // NMEA 2000 (via a gateway) — real navigation + engine data (graceful no-op if no gateway).
        _nmea = new NmeaService(_settings.NmeaHost, _settings.NmeaPort);
        _nmea.OnUpdate += OnNmeaUpdate;
        _nmea.Start();

        // Navico HTML5 integration — advertise to Simrad/B&G/Lowrance MFDs + raise alarms on them.
        if (_settings.EnableNavicoMfd)
        {
            _navico = new NavicoMfdService(HttpPort, BuildMfdAlarms);
            _navico.Start();
        }
    }

    /// <summary>Current alarm summary for Navico MFDs, derived from live iTach state.</summary>
    private IReadOnlyList<NavicoMfdService.Alarm> BuildMfdAlarms()
    {
        var list = new List<NavicoMfdService.Alarm>();
        try
        {
            int? f(string ch, int i) => _client.Field(ch, i);
            // Service bank fault → Important. Only alarm on a PLAUSIBLE low reading: the channel reports
            // 0.0 V with a -3276.8 A sentinel when the sensor is disconnected/unconfigured (no data), which
            // is NOT a dead battery. A real 24 V bank in fault still reads several volts, so require ≥ 5 V.
            if (f("00", 8) is int sv && sv >= 50 && sv / 10.0 < 22.0)
                list.Add(new NavicoMfdService.Alarm("Important", 1, 1));
            // Any tank critically low (< 15%) → Warning.
            int lowTanks = 0;
            foreach (var (ch, i) in new[] { ("00", 10), ("00", 11), ("03", 2), ("03", 3), ("03", 10), ("03", 11) })
                if (f(ch, i) is int lvl && lvl < 15) lowTanks++;
            if (lowTanks > 0) list.Add(new NavicoMfdService.Alarm("Warning", lowTanks, lowTanks));
        }
        catch { }
        return list;
    }

    /// <summary>Pushes NMEA nav + engine data into the WebView's LIVE object and out to MQTT.</summary>
    private void OnNmeaUpdate(NmeaService.NavState s)
    {
        var nav = new
        {
            lat = s.Lat, lon = s.Lon, sog = s.Sog, cog = s.Cog, heading = s.Heading,
            variation = s.Variation, depth = s.Depth, windSpeed = s.WindSpeed, windAngle = s.WindAngle,
            satellites = s.Satellites, hdop = s.Hdop, fix = s.FixQuality,
        };
        var engines = new
        {
            running = s.AnyEngineData,
            port = new { rpm = s.Engines[0].Rpm, coolant = s.Engines[0].CoolantTempC, oil = s.Engines[0].OilPressureKpa, hours = s.Engines[0].Hours, alt = s.Engines[0].AlternatorV, fuel = s.Engines[0].FuelRateLph },
            stbd = new { rpm = s.Engines[1].Rpm, coolant = s.Engines[1].CoolantTempC, oil = s.Engines[1].OilPressureKpa, hours = s.Engines[1].Hours, alt = s.Engines[1].AlternatorV, fuel = s.Engines[1].FuelRateLph },
        };
        var json = JsonSerializer.Serialize(new { nav, engines });
        Dispatcher.BeginInvoke(() =>
        {
            try { Web?.CoreWebView2?.ExecuteScriptAsync($"window.vomsApply({json})"); } catch { }
        });

        // Publish the key nav + engine sensors to the cloud too.
        var m = _mqttAgent;
        if (m is not null)
        {
            void nv(string n, double? v, string u) { if (v is not null) _ = m.PublishNavAsync(n, v.Value, u); }
            nv("sog", s.Sog, "kn"); nv("cog", s.Cog, "deg"); nv("heading", s.Heading, "deg");
            nv("depth", s.Depth, "m"); nv("wind-speed", s.WindSpeed, "kn"); nv("wind-angle", s.WindAngle, "deg");
            nv("engine-port-rpm", s.Engines[0].Rpm, "rpm"); nv("engine-stbd-rpm", s.Engines[1].Rpm, "rpm");
            nv("engine-port-coolant", s.Engines[0].CoolantTempC, "C"); nv("engine-stbd-coolant", s.Engines[1].CoolantTempC, "C");
        }
    }

    private static readonly HashSet<string> NotableClasses = new(StringComparer.OrdinalIgnoreCase)
    { "person", "boat", "car", "truck", "bird", "dog", "cat" };
    private DateTime _lastVisionAlert = DateTime.MinValue;

    /// <summary>Forwards YOLO detections to the dashboard overlay, MQTT (visual sensors), and the assistant.</summary>
    private void OnVisionDetections(int camIndex, string cam, IReadOnlyList<VisionService.Detection> dets)
    {
        foreach (var d in dets)
            _ = _mqttAgent?.PublishVisionAsync(cam, d.Label, d.Confidence);

        // Draw the detections on the dashboard's camera screen (bounding boxes + labels).
        var overlay = dets.Select(d => new { label = d.Label, conf = Math.Round(d.Confidence, 2), x = d.X, y = d.Y, w = d.W, h = d.H }).ToArray();
        var oj = JsonSerializer.Serialize(overlay);
        Dispatcher.BeginInvoke(() =>
        {
            try { Web?.CoreWebView2?.ExecuteScriptAsync($"try{{window.camDetections&&window.camDetections({camIndex},{oj})}}catch(e){{}}"); } catch { }
        });

        // Proactive alert for notable objects, rate-limited so it doesn't spam.
        var notable = dets.Where(d => NotableClasses.Contains(d.Label)).OrderByDescending(d => d.Confidence).FirstOrDefault();
        if (notable is not null && (DateTime.UtcNow - _lastVisionAlert).TotalSeconds > 30)
        {
            _lastVisionAlert = DateTime.UtcNow;
            var msg = $"I can see a {notable.Label} on the {cam} camera.";
            Dispatcher.BeginInvoke(() =>
            {
                try { Web?.CoreWebView2?.ExecuteScriptAsync($"try{{window.assistNotify&&window.assistNotify({JsonSerializer.Serialize(msg)})}}catch(e){{}}"); } catch { }
            });
        }
    }

    /// <summary>The being announces a freshly-discovered device to the owner (chat + voice).</summary>
    private void OnNewAvDevice(AvDevice d)
    {
        if (d.Type is "media") return;   // only announce recognisable AV gear
        var article = "aeiou".Contains(char.ToLower(d.Type[0])) ? "an" : "a";
        var brand = string.IsNullOrWhiteSpace(d.Manufacturer) ? "" : d.Manufacturer + " ";
        var msg = d.NeedsPairing
            ? $"Hey — I found {article} {brand}{d.Type} “{d.Name}” on the network. It needs pairing before I can control it. Want me to start that?"
            : $"Hey — I found {article} {brand}{d.Type} “{d.Name}” on the network. Want me to add it to the system?";
        Dispatcher.BeginInvoke(() =>
        {
            var json = JsonSerializer.Serialize(msg);
            var dev = JsonSerializer.Serialize(d.Id);
            _ = Web.CoreWebView2?.ExecuteScriptAsync($"window.assistNotify&&window.assistNotify({json},{dev})");
            _voice?.Speak(msg);
        });
    }

    /// <summary>Current value for a sensor name (for automation conditions).</summary>
    private double? SensorValue(string name) => name switch
    {
        "battery-service" => (_client.Field("00", 8) ?? 0) / 10.0,
        "battery-genset" => (_client.Field("00", 2) ?? 0) / 10.0,
        "fresh-water-port" => _client.Field("00", 10),
        "fresh-water-stbd" => _client.Field("00", 11),
        "fuel-fwd-port" => _client.Field("03", 2),
        "shore-1-volts" => _client.Field("02", 0),
        _ => null,
    };

    /// <summary>Executes a structured automation action.</summary>
    private async Task RunAutoActionAsync(AutoAction a)
    {
        switch (a.Type)
        {
            case "light": if (a.Code != 0) await _client.SendCommandAsync((uint)a.Code); break;
            case "all_lights":
                await _client.SendCommandAsync(a.Value?.StartsWith("on", StringComparison.OrdinalIgnoreCase) == true ? 0x0600u : 0x0700u);
                break;
            case "av":
                if (a.Target is not null && _av.Match(a.Target) is { } dev)
                    await _av.ControlAsync(dev.Id, a.Value ?? "", null);
                break;
            case "notify":
                Dispatcher.BeginInvoke(() =>
                {
                    var json = JsonSerializer.Serialize(a.Value ?? "");
                    _ = Web.CoreWebView2?.ExecuteScriptAsync($"window.assistNotify&&window.assistNotify({json},null)");
                    _voice?.Speak(a.Value ?? "");
                });
                break;
        }
    }

    /// <summary>Starts the VOMS cloud agent if this PC is paired (creds in C:\voms\mqtt.env).</summary>
    private void StartOnboardAgent()
    {
        if (_mqttAgent is not null) return;
        var creds = PairingService.LoadSaved();
        if (creds is not { VesselId.Length: > 0 }) return;
        _mqttAgent = new MqttAgent(creds, _client, PairingService.HardwareId);
        _mqttAgent.Start();
        Ip2slClient.Log("[onboard] agent started for vessel " + creds.VesselId);
    }

    /// <summary>(Re)creates the Claude assistant from the saved API key and the voice service.</summary>
    private void ApplyAssistant()
    {
        _assistant = string.IsNullOrWhiteSpace(_settings.ClaudeApiKey)
            ? null
            : new ClaudeAssistant(_settings.ClaudeApiKey, _client, _vm, _av, _memory, _autos);

        if (_voice is null)
        {
            _voice = new VoiceService();
            _voice.OnListeningChanged += l => Dispatcher.BeginInvoke(() =>
                _ = Web.CoreWebView2?.ExecuteScriptAsync($"window.claudeState({{listening:{(l ? "true" : "false")}}})"));
            _voice.OnHeard += text => Dispatcher.BeginInvoke(() =>
            {
                _replyAloud = true;
                var heard = JsonSerializer.Serialize(text);
                _ = Web.CoreWebView2?.ExecuteScriptAsync($"window.claudeState({{heard:{heard}}})");
            });
        }
        PushAssistantState();
    }

    private void PushAssistantState() =>
        _ = Web.CoreWebView2?.ExecuteScriptAsync(
            $"window.claudeState({{ready:{(_assistant is null ? "false" : "true")}}})");

    private async Task AskClaudeAsync(string text)
    {
        string reply;
        if (_assistant is null)
        {
            reply = "No Claude API key configured. Open ⚙ SETUP and add your key under CLAUDE AI.";
        }
        else
        {
            try { reply = await _assistant.AskAsync(text); }
            catch (Exception ex) { reply = "Error talking to Claude: " + ex.Message; }
            if (string.IsNullOrWhiteSpace(reply)) reply = "(no response)";
        }

        if (_replyAloud) { _replyAloud = false; _voice?.Speak(reply); }
        var json = JsonSerializer.Serialize(reply);
        _ = Web.CoreWebView2?.ExecuteScriptAsync($"window.claudeReply({json})");
    }

    /// <summary>Runs AV discovery and pushes the device list to the page.</summary>
    private async Task DiscoverAvAsync()
    {
        _ = Web.CoreWebView2?.ExecuteScriptAsync("window.avScanning&&window.avScanning(true)");
        try { await _av.DiscoverAsync(); } catch { }
        var json = _av.DevicesJson();
        _ = Web.CoreWebView2?.ExecuteScriptAsync($"window.avDevices({json})");
    }

    /// <summary>Serves the UI over HTTP on the LAN and advertises the PC via mDNS.</summary>
    private void StartLanServices(string webDir)
    {
        try
        {
            _server = new LocalServer(webDir, HttpPort, _settings.LanUser, _settings.LanPass);
            _server.SetAllowList(_settings.LanAllowList);
            _server.Av = _av;
            _server.Itach = _client;
            _server.Cameras = _settings.Cameras;
            // Route remote-browser commands through the same handler as the WebView2 bridge.
            _server.OnCommand = (cmd, code) => Dispatcher.BeginInvoke(() => RunCommand(cmd, code));
            _server.Start();
        }
        catch (Exception ex) { Ip2slClient.Log("[lan] http server failed: " + ex.Message); }

        try
        {
            _mdns = new MulticastService();
            _sd = new ServiceDiscovery(_mdns);
            _sd.Advertise(new ServiceProfile("BoatDashboard", "_http._tcp", HttpPort));
            _mdns.Start();
        }
        catch (Exception ex) { Ip2slClient.Log("[lan] mdns failed: " + ex.Message); }

        // Optional Cloudflare quick tunnel — off unless the owner enabled it (exposes the boat publicly).
        if (_settings.EnableCloudflareTunnel)
        {
            try
            {
                _cf = new CloudflareTunnelService(HttpPort);
                _cf.UrlChanged += url => Dispatcher.BeginInvoke(() =>
                {
                    if (!string.IsNullOrEmpty(url))
                        Web?.CoreWebView2?.ExecuteScriptAsync(
                            $"try{{window.assistNotify&&window.assistNotify('Remote access is live at {url} (login required).')}}catch(e){{}}");
                });
                _cf.Start();
            }
            catch (Exception ex) { Ip2slClient.Log("[cloudflare] start failed: " + ex.Message); }
        }
    }

    /// <summary>Pushes the configured camera list to the WebView (which isn't a __remote client).</summary>
    private void PushCameras()
    {
        var cams = _settings.Cameras ?? new();
        var list = cams.Select((c, i) => new { i, name = string.IsNullOrWhiteSpace(c.Name) ? $"Camera {i + 1}" : c.Name });
        var json = JsonSerializer.Serialize(list);
        _ = Web.CoreWebView2?.ExecuteScriptAsync($"window.camApply({json})");
    }

    private CancellationTokenSource? _camPushCts;
    /// <summary>Streams camera frames INTO the WebView as data: URLs. WebView2's WebResourceRequested does
    /// not intercept fetch() to the virtual host (the folder mapping swallows it → "Failed to fetch"), so
    /// the in-page fetch never worked. Fetching server-side and pushing the bytes as a data URL sidesteps
    /// the WebView network stack entirely, so feeds render reliably. (LAN browsers still use /api/cam.)</summary>
    private void StartCameraPush()
    {
        if (_camPushCts is not null) return;   // already running
        _camPushCts = new CancellationTokenSource();
        var ct = _camPushCts.Token;
        _ = Task.Run(async () =>
        {
            while (!ct.IsCancellationRequested)
            {
                var cams = _settings.Cameras ?? new();
                for (int i = 0; i < cams.Count && !ct.IsCancellationRequested; i++)
                {
                    if (string.IsNullOrWhiteSpace(cams[i].Url)) continue;
                    byte[]? jpeg = null;
                    try { jpeg = await LocalServer.FetchSnapshotAsync(cams[i].Url); } catch { }
                    if (jpeg is null || jpeg.Length < 1024) continue;
                    var dataUrl = "data:image/jpeg;base64," + Convert.ToBase64String(jpeg);
                    int idx = i;
                    try { await Dispatcher.InvokeAsync(() =>
                        Web.CoreWebView2?.ExecuteScriptAsync($"window.camFrame&&window.camFrame({idx},{JsonSerializer.Serialize(dataUrl)})")); }
                    catch { }
                }
                try { await Task.Delay(TimeSpan.FromSeconds(3), ct); } catch { break; }
            }
        }, ct);
    }

    /// <summary>Serves /api/cameras and /api/cam?i=N to the WebView, same-origin, so camera feeds render
    /// without a mixed-content block. Camera frames are fetched server-side (any HTTP/MJPEG source).</summary>
    private async void OnWebResourceRequested(object? sender, CoreWebView2WebResourceRequestedEventArgs e)
    {
        string uri = e.Request.Uri;
        if (uri.IndexOf("/api/cam", StringComparison.OrdinalIgnoreCase) < 0) return;
        var env = Web.CoreWebView2.Environment;
        var deferral = e.GetDeferral();
        try
        {
            var u = new Uri(uri);
            var cams = _settings.Cameras ?? new();
            if (u.AbsolutePath.Equals("/api/cameras", StringComparison.OrdinalIgnoreCase))
            {
                var list = cams.Select((c, i) => new { i, name = string.IsNullOrWhiteSpace(c.Name) ? $"Camera {i + 1}" : c.Name });
                var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(list));
                e.Response = env.CreateWebResourceResponse(new MemoryStream(bytes), 200, "OK",
                    "Content-Type: application/json\r\nAccess-Control-Allow-Origin: *");
            }
            else if (u.AbsolutePath.Equals("/api/cam", StringComparison.OrdinalIgnoreCase))
            {
                int idx = 0;
                foreach (var kv in u.Query.TrimStart('?').Split('&'))
                    if (kv.StartsWith("i=") && int.TryParse(kv[2..], out var pi)) idx = pi;
                byte[]? jpeg = (idx >= 0 && idx < cams.Count && !string.IsNullOrWhiteSpace(cams[idx].Url))
                    ? await LocalServer.FetchSnapshotAsync(cams[idx].Url)
                    : null;
                e.Response = jpeg is not null
                    ? env.CreateWebResourceResponse(new MemoryStream(jpeg), 200, "OK", "Content-Type: image/jpeg\r\nCache-Control: no-store")
                    : env.CreateWebResourceResponse(null, 502, "Bad Gateway", "");
            }
        }
        catch (Exception ex) { Ip2slClient.Log("[webreq] handler EXCEPTION: " + ex); }
        finally { deferral.Complete(); }
    }

    /// <summary>Executes a hardware command from either the WebView2 bridge or a LAN client.</summary>
    private void RunCommand(string cmd, uint code)
    {
        switch (cmd)
        {
            case "all_on": _ = _client.SendCommandAsync(0x0600u); break;
            case "all_off": _ = _client.SendCommandAsync(0x0700u); break;
            case "light": if (code != 0) _ = _client.SendCommandAsync(code); break;
            case "alarm_ack": _navico?.Acknowledge(); break;   // silence active alarms on the MFD
        }
    }

    /// <summary>Applies (or removes) the fullscreen, cursor-less, locked kiosk chrome.</summary>
    private void ApplyKiosk()
    {
        _kiosk = _settings.Kiosk;
        if (_kiosk)
        {
            // Full-screen over the taskbar: borderless window sized to the whole primary
            // screen (Maximized on a borderless window only covers the work area).
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            WindowState = WindowState.Normal;
            Left = 0;
            Top = 0;
            Width = SystemParameters.PrimaryScreenWidth;
            Height = SystemParameters.PrimaryScreenHeight;
            Topmost = true;
            Cursor = _settings.ShowCursor ? null : Cursors.None;
        }
        else
        {
            WindowStyle = WindowStyle.SingleBorderWindow;
            ResizeMode = ResizeMode.CanResize;
            Topmost = false;
            Width = double.NaN;
            Height = double.NaN;
            WindowState = WindowState.Maximized;
            Cursor = null;
        }
        ApplyCursor();
    }

    /// <summary>Hides the pointer inside the web content when in kiosk mode.</summary>
    private void ApplyCursor()
    {
        var core = Web.CoreWebView2;
        if (core is null) return;
        var css = (_kiosk && !_settings.ShowCursor) ? "*{cursor:none !important;}" : "";
        _ = core.ExecuteScriptAsync(
            "(function(){var s=document.getElementById('__vcur')||document.createElement('style');" +
            "s.id='__vcur';s.textContent=" + JsonSerializer.Serialize(css) + ";" +
            "if(!s.parentNode)document.head.appendChild(s);})()");
    }

    /// <summary>Steadies the pulsing alarm indicators when blink is disabled (redefines the
    /// pulseDot keyframe to a no-op; the later definition wins).</summary>
    private void ApplyBlink()
    {
        var core = Web.CoreWebView2;
        if (core is null) return;
        var css = _settings.BlinkAlarms ? "" : "@keyframes pulseDot{0%,100%{opacity:1}50%{opacity:1}}";
        _ = core.ExecuteScriptAsync(
            "(function(){var s=document.getElementById('__vblink')||document.createElement('style');" +
            "s.id='__vblink';s.textContent=" + JsonSerializer.Serialize(css) + ";" +
            "if(!s.parentNode)document.head.appendChild(s);})()");
    }

    private void OnStateChanged(object? sender, EventArgs e)
    {
        // In kiosk mode never allow the window to be minimised away.
        if (_kiosk && WindowState == WindowState.Minimized)
            WindowState = WindowState.Maximized;
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        // Kiosk: refuse to close unless the passcode flow authorised it (App.AllowExit).
        if (_kiosk && !App.AllowExit)
        {
            e.Cancel = true;
            return;
        }
        base.OnClosing(e);
    }

    private void OnTick(object? sender, EventArgs e)
    {
        // With no hardware connected, leave the design's demo values in place.
        if (!_client.Connected) return;

        _vm.Refresh(_client, "");   // keep the assistant's get_status snapshot current

        int F(string ch, int i) => _client.Field(ch, i) ?? 0;
        double V(string ch, int i) => F(ch, i) / 10.0;

        double waterPort = F("00", 10), waterStbd = F("00", 11);
        double fFwdPort = F("03", 2), fFwdStbd = F("03", 3), fAftPort = F("03", 10), fAftStbd = F("03", 11);
        double serviceV = V("00", 8);
        double shoreV = F("02", 0), shoreA = F("02", 1), shoreHz = V("02", 2);

        // Previously-hidden electrical detail (confirmed live on the raw dump).
        double shore2V = F("02", 3), shore2A = F("02", 4), shore2Hz = V("02", 5);
        double genV = F("02", 6), genA = F("02", 7), genHz = V("02", 8);
        double invV = F("02", 9), invA = F("02", 10), invHz = V("02", 11);
        double bGenV = V("00", 2), bPortV = V("00", 4), bStbdV = V("00", 6);
        double bGenA = F("00", 3) - 512, bPortA = F("00", 5) - 512, bStbdA = F("00", 7) - 512;
        double fuelToTransfer = F("03", 8), fuelToGo = F("03", 9);

        double waterAvg = Math.Round((waterPort + waterStbd) / 2.0);
        double fuelAvg = Math.Round((fFwdPort + fFwdStbd + fAftPort + fAftStbd) / 4.0);
        // 0.0 V = disconnected/unconfigured sensor (no data), not a dead battery — don't alarm on it.
        bool serviceValid = serviceV >= 5.0;
        bool serviceLow = serviceValid && serviceV < 22.0; // 24 V bank; below ~22 V is a real fault

        var live = new
        {
            ac = new { v = (int)shoreV, a = (int)shoreA, hz = Math.Round(shoreHz, 1) },
            // Full AC picture — shore 1, shore 2, generator, inverter.
            acDetail = new
            {
                shore1 = new { v = (int)shoreV, a = (int)shoreA, hz = Math.Round(shoreHz, 1) },
                shore2 = new { v = (int)shore2V, a = (int)shore2A, hz = Math.Round(shore2Hz, 1) },
                generator = new { v = (int)genV, a = (int)genA, hz = Math.Round(genHz, 1) },
                inverter = new { v = (int)invV, a = (int)invA, hz = Math.Round(invHz, 1) },
            },
            batteries = new
            {
                genset = new { v = bGenV, a = (int)bGenA },
                portEngine = new { v = bPortV, a = (int)bPortA },
                stbdEngine = new { v = bStbdV, a = (int)bStbdA },
                service = new { v = serviceV, a = 0 },
            },
            tanks = new
            {
                waterPort = (int)waterPort,
                waterStbd = (int)waterStbd,
                fuelAftPort = (int)fAftPort,
                fuelFwdPort = (int)fFwdPort,
                fuelAftStbd = (int)fAftStbd,
                fuelFwdStbd = (int)fFwdStbd,
            },
            fuelTransfer = new { toTransfer = (int)fuelToTransfer, toGo = (int)fuelToGo },
            waterAvg = (int)waterAvg,
            fuelAvg = (int)fuelAvg,
            service = new { v = serviceV.ToString("0.0"), low = serviceLow, noData = !serviceValid },
            alarm = serviceLow,
        };

        var json = JsonSerializer.Serialize(live);
        if (_server is not null) _server.TelemetryJson = json;   // share with LAN clients
        _ = Web.CoreWebView2.ExecuteScriptAsync($"window.vomsApply({json})");
    }

    private void OnWebMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        string cmd, text = "", device = "", action = "", value = "";
        uint code = 0;
        try
        {
            using var doc = JsonDocument.Parse(e.WebMessageAsJson);
            var root = doc.RootElement;
            cmd = root.TryGetProperty("cmd", out var c) ? c.GetString() ?? "" : "";
            if (root.TryGetProperty("code", out var cd) && cd.TryGetInt64(out var n)) code = (uint)n;
            if (root.TryGetProperty("text", out var t)) text = t.GetString() ?? "";
            if (root.TryGetProperty("device", out var dv)) device = dv.GetString() ?? "";
            if (root.TryGetProperty("action", out var ac)) action = ac.GetString() ?? "";
            if (root.TryGetProperty("value", out var vl)) value = vl.ValueKind == JsonValueKind.String ? vl.GetString() ?? "" : vl.ToString();
        }
        catch { return; }

        switch (cmd)
        {
            case "settings":
                OpenSettings();
                break;
            case "av_discover":
                _ = DiscoverAvAsync();
                break;
            case "av_control":
                _ = _av.ControlAsync(device, action, string.IsNullOrEmpty(value) ? null : value);
                break;
            case "av_accept":
                _av.Accept(device);
                _ = Web.CoreWebView2?.ExecuteScriptAsync($"window.avDevices({_av.DevicesJson()})");
                break;
            case "claude":
                if (text.Length > 0) _ = AskClaudeAsync(text);
                break;
            case "voice_start":
                if (_voice is not null && !_voice.TryStartListening(out var err))
                    _ = Web.CoreWebView2?.ExecuteScriptAsync(
                        $"window.claudeReply({JsonSerializer.Serialize("Microphone unavailable: " + err)})");
                break;
            case "voice_stop":
                _voice?.StopListening();
                break;
            default:
                RunCommand(cmd, code);
                break;
        }
    }

    private void OpenSettings()
    {
        var dlg = new SettingsWindow(_settings, _server) { Owner = this };
        if (dlg.ShowDialog() == true)
        {
            _settings = dlg.Settings;
            ApplyKiosk();
            ApplyBlink();
            ApplyAssistant();
            _server?.SetAllowList(_settings.LanAllowList);   // takes effect immediately
            StartOnboardAgent();                             // start publishing if just paired
        }
    }
}
