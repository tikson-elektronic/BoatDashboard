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
    private int _tick;

    public NavicoMfdService(int httpPort, Func<IReadOnlyList<Alarm>> alarms)
    {
        _httpPort = httpPort;
        _alarms = alarms;
    }

    public void Start() => _ = Task.Run(LoopAsync);

    private async Task LoopAsync()
    {
        Ip2slClient.Log("[navico] announcing on 239.2.1.1 (CAL 2053 / alarms 2054), per-interface");
        while (!_cts.IsCancellationRequested)
        {
            _tick++;
            // Navico MFDs live on the 169.254.x.x link-local (Zeroconfig) network. If this PC has a
            // link-local address (i.e. it's on the Navico wire), announce ONLY those URLs — a 192.168.x
            // URL is on a different L3 subnet the MFD can't route to and only causes "Failed to load".
            // With no link-local address, fall back to announcing every interface.
            var ips = LocalServer.AllLocalIPv4();
            var linkLocal = ips.Where(a => a.StartsWith("169.254.", StringComparison.Ordinal)).ToList();
            foreach (var ip in linkLocal.Count > 0 ? linkLocal : ips)
            {
                try
                {
                    // Do NOT bind the socket to the unicast address — Windows rejects binding to a manually
                    // added link-local (169.254.x) address ("address not valid in its context"). Instead
                    // leave the socket unbound and select the egress interface by INDEX, which works for
                    // link-local. The advertised URL still uses this interface's IP.
                    using var udp = new UdpClient(AddressFamily.InterNetwork) { Ttl = 2, MulticastLoopback = false };
                    int idx = InterfaceIndexForIp(ip);
                    if (idx >= 0) { try { udp.Client.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastInterface, IPAddress.HostToNetworkOrder(idx)); } catch { } }
                    SendCal(udp, ip);
                    SendAlarms(udp, ip);
                }
                catch (Exception ex) { Ip2slClient.Log($"[navico] send {ip}: {ex.Message}"); }
            }
            try { await Task.Delay(TimeSpan.FromSeconds(5), _cts.Token); } catch { break; }
        }
    }

    // Client Application Link — advertises the app + the URL of our own web UI.
    private void SendCal(UdpClient udp, string ip)
    {
        var baseUrl = $"http://{ip}:{_httpPort}";
        var cal = new Dictionary<string, object?>
        {
            ["Version"] = "1",
            ["Source"] = Source,
            ["FeatureName"] = Feature,
            ["IP"] = ip,
            ["Text"] = new[] { new { Language = "en", Name = "Vessel Monitor" } },
            ["Image"] = $"{baseUrl}/uploads/mfd-icon.png",
            ["Icon"] = $"{baseUrl}/uploads/mfd-icon.png",
            ["URL"] = $"{baseUrl}/",
            ["BrowserPanel"] = new
            {
                Enable = true,
                ProgressBarEnable = false,
                MenuText = new[] { new { Language = "en", Name = "Home" } },
            },
        };
        Send(udp, cal, CalPort);
    }

    // Alarm announcement — current alarm summary with a watchdog + changing tick.
    private void SendAlarms(UdpClient udp, string ip)
    {
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
        Send(udp, msg, AlarmPort);
    }

    private static void Send(UdpClient udp, object payload, int port)
    {
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payload));
        udp.Send(bytes, bytes.Length, new IPEndPoint(Group, port));
    }

    // IPv4 interface index that owns the given unicast address (for multicast egress selection).
    private static int InterfaceIndexForIp(string ip)
    {
        try
        {
            foreach (var ni in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up) continue;
                var props = ni.GetIPProperties();
                if (props.UnicastAddresses.Any(ua => ua.Address.ToString() == ip))
                    return props.GetIPv4Properties()?.Index ?? -1;
            }
        }
        catch { }
        return -1;
    }

    public void Dispose()
    {
        _cts.Cancel();
        _cts.Dispose();
    }
}
