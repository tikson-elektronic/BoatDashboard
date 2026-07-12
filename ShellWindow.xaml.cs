using System.ComponentModel;
using System.IO;
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

    private MqttAgent? _mqttAgent;   // onboard VOMS agent (publishes to the Alive cloud when paired)
    private readonly AvService _av = new();   // AV device discovery + control (TVs, amplifiers)
    private readonly MemoryStore _memory = new();
    private AutomationService? _autos;

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
            try { _sd?.Dispose(); _mdns?.Dispose(); } catch { }
            try { _voice?.Dispose(); } catch { }
            try { _ = _mqttAgent?.DisposeAsync(); } catch { }
            try { _av.StopBackgroundScan(); } catch { }
            try { _autos?.Stop(); } catch { }
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
        core.NavigationCompleted += (_, _) => { ApplyCursor(); ApplyBlink(); PushAssistantState(); };

        // Serve the WebUI folder over a virtual host so relative asset paths resolve.
        var webDir = Path.Combine(AppContext.BaseDirectory, "WebUI");
        core.SetVirtualHostNameToFolderMapping(
            "vessel.local", webDir, CoreWebView2HostResourceAccessKind.Allow);
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
    }

    /// <summary>Executes a hardware command from either the WebView2 bridge or a LAN client.</summary>
    private void RunCommand(string cmd, uint code)
    {
        switch (cmd)
        {
            case "all_on": _ = _client.SendCommandAsync(0x0600u); break;
            case "all_off": _ = _client.SendCommandAsync(0x0700u); break;
            case "light": if (code != 0) _ = _client.SendCommandAsync(code); break;
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
            Cursor = Cursors.None;
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
        var css = _kiosk ? "*{cursor:none !important;}" : "";
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

        double waterAvg = Math.Round((waterPort + waterStbd) / 2.0);
        double fuelAvg = Math.Round((fFwdPort + fFwdStbd + fAftPort + fAftStbd) / 4.0);
        bool serviceLow = serviceV < 22.0; // 24 V bank; below ~22 V is a fault

        var live = new
        {
            ac = new { v = (int)shoreV, a = (int)shoreA, hz = Math.Round(shoreHz, 1) },
            tanks = new
            {
                waterPort = (int)waterPort,
                waterStbd = (int)waterStbd,
                fuelAftPort = (int)fAftPort,
                fuelFwdPort = (int)fFwdPort,
                fuelAftStbd = (int)fAftStbd,
                fuelFwdStbd = (int)fFwdStbd,
            },
            waterAvg = (int)waterAvg,
            fuelAvg = (int)fuelAvg,
            service = new { v = serviceV.ToString("0.0"), low = serviceLow },
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
