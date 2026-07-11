using System.ComponentModel;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
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
  var rail=document.getElementById('rail');
  if(!rail || document.getElementById('vSetup')) return;
  var b=document.createElement('div'); b.id='vSetup'; b.textContent='⚙ SETUP';
  b.style.cssText='height:46px;display:flex;align-items:center;padding:0 16px;cursor:pointer;font-size:11px;font-weight:700;letter-spacing:2px;color:#4a5b66;';
  b.onmouseenter=function(){b.style.color='#2fc4d1';};
  b.onmouseleave=function(){b.style.color='#4a5b66';};
  b.onclick=function(){ __vpost({cmd:'settings'}); };
  rail.appendChild(b);
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
        Closed += (_, _) => { _timer.Stop(); _client.Dispose(); };
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
        core.NavigationCompleted += (_, _) => ApplyCursor();

        // Serve the WebUI folder over a virtual host so relative asset paths resolve.
        var webDir = Path.Combine(AppContext.BaseDirectory, "WebUI");
        core.SetVirtualHostNameToFolderMapping(
            "vessel.local", webDir, CoreWebView2HostResourceAccessKind.Allow);
        core.Navigate("https://vessel.local/app.html");

        ApplyKiosk();

        _client.Start();
        _timer.Tick += OnTick;
        _timer.Start();
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
        _ = Web.CoreWebView2.ExecuteScriptAsync($"window.vomsApply({json})");
    }

    private void OnWebMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        string cmd;
        try
        {
            using var doc = JsonDocument.Parse(e.WebMessageAsJson);
            cmd = doc.RootElement.TryGetProperty("cmd", out var c) ? c.GetString() ?? "" : "";
        }
        catch { return; }

        switch (cmd)
        {
            case "all_on":
                _ = _client.SendCommandAsync(0x0600u);
                break;
            case "all_off":
                _ = _client.SendCommandAsync(0x0700u);
                break;
            case "settings":
                OpenSettings();
                break;
        }
    }

    private void OpenSettings()
    {
        var dlg = new SettingsWindow(_settings) { Owner = this };
        if (dlg.ShowDialog() == true)
        {
            _settings = dlg.Settings;
            ApplyKiosk();
        }
    }
}
