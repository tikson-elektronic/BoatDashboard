using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace BoatDashboard;

/// <summary>
/// Implements the Navico HTML5 Integration Protocol (NOS 20.2/20.3) so the dashboard appears as an
/// app icon on Simrad / B&amp;G / Lowrance MFDs (chartplotters) and can raise alarms on them.
///
/// Two multicast announcements on 239.2.1.1:
///  • Client Application Link (port 2053) every 5 s — advertises the app (name, icon, and the URL of
///    the dashboard's own LAN web server). The MFD adds an icon to its home page; tapping it opens the
///    dashboard in the MFD's built-in browser (with ?mfd_name/mfd_model/lang/mode/brand appended).
///  • Alarms (port 2054) every 5 s — the current alarm summary (service battery, tanks, …) with a
///    watchdog + changing TickCount, so the MFD shows native alarm notifications.
///
/// Uses IP addresses (never names) per the spec. Opt-in via AppSettings.EnableNavicoMfd.
/// </summary>
public sealed class NavicoMfdService : IDisposable
{
    public sealed record Alarm(string Type, int Count, int New);

    private static readonly IPAddress Group = IPAddress.Parse("239.2.1.1");
    private const int CalPort = 2053;
    private const int AlarmPort = 2054;
    private const string Source = "VOMS";
    private const string Feature = "VesselMonitor";

    private readonly int _httpPort;
    private readonly Func<IReadOnlyList<Alarm>> _alarms;
    private readonly CancellationTokenSource _cts = new();
    private UdpClient? _udp;
    private int _tick;

    public NavicoMfdService(int httpPort, Func<IReadOnlyList<Alarm>> alarms)
    {
        _httpPort = httpPort;
        _alarms = alarms;
    }

    public void Start() => _ = Task.Run(LoopAsync);

    private async Task LoopAsync()
    {
        try
        {
            _udp = new UdpClient();
            _udp.Ttl = 2;                       // reach the local marine LAN segment
            _udp.MulticastLoopback = false;
        }
        catch (Exception ex) { Ip2slClient.Log("[navico] udp init failed: " + ex.Message); return; }

        Ip2slClient.Log("[navico] announcing on 239.2.1.1 (CAL 2053 / alarms 2054)");
        while (!_cts.IsCancellationRequested)
        {
            var ip = LocalServer.LocalIPv4();
            try { SendCal(ip); } catch (Exception ex) { Ip2slClient.Log("[navico] CAL send: " + ex.Message); }
            try { SendAlarms(ip); } catch (Exception ex) { Ip2slClient.Log("[navico] alarm send: " + ex.Message); }
            try { await Task.Delay(TimeSpan.FromSeconds(5), _cts.Token); } catch { break; }
        }
    }

    // Client Application Link — advertises the app + the URL of our own web UI.
    private void SendCal(string ip)
    {
        var baseUrl = $"http://{ip}:{_httpPort}";
        var cal = new Dictionary<string, object?>
        {
            ["Version"] = "1",
            ["Source"] = Source,
            ["FeatureName"] = Feature,
            ["IP"] = ip,
            ["Text"] = new[] { new { Language = "en", Name = "Vessel Monitor" } },
            ["Image"] = $"{baseUrl}/uploads/lagoon-logo.png",
            ["Icon"] = $"{baseUrl}/uploads/lagoon-logo.png",
            ["URL"] = $"{baseUrl}/",
            ["BrowserPanel"] = new
            {
                Enable = true,
                ProgressBarEnable = false,
                MenuText = new[] { new { Language = "en", Name = "Home" } },
            },
        };
        Send(cal, CalPort);
    }

    // Alarm announcement — current alarm summary with a watchdog + changing tick.
    private void SendAlarms(string ip)
    {
        _tick++;
        var alarms = _alarms();
        var msg = new Dictionary<string, object?>
        {
            ["Version"] = "1",
            ["Source"] = Source,
            ["FeatureName"] = Feature,
            ["IP"] = ip,
            ["WatchdogInterval"] = 20,
            ["URL"] = $"http://{ip}:{_httpPort}/",
            ["TickCount"] = _tick,
            ["Alarms"] = alarms.Select(a => new { a.Type, a.Count, New = a.New, NewTickCount = _tick }).ToArray(),
        };
        Send(msg, AlarmPort);
    }

    private void Send(object payload, int port)
    {
        if (_udp is null) return;
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payload));
        _udp.Send(bytes, bytes.Length, new IPEndPoint(Group, port));
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _udp?.Dispose(); } catch { }
        _cts.Dispose();
    }
}
