using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace BoatDashboard;

/// <summary>One discovered AV device (TV, AV receiver/amplifier, speaker, media player).</summary>
public sealed class AvDevice
{
    public required string Id { get; init; }        // stable: ip + protocol
    public required string Ip { get; init; }
    public string Name { get; set; } = "";
    public string Type { get; set; } = "media";     // tv | amplifier | speaker | mediaplayer | media
    public string Manufacturer { get; set; } = "";
    public string Model { get; set; } = "";
    public string Protocol { get; set; } = "upnp";  // roku_ecp | upnp | samsung | lg | airplay | cast
    public string? AvTransportUrl { get; set; }      // absolute UPnP control URLs (when UPnP)
    public string? RenderingControlUrl { get; set; }
    public bool NeedsPairing { get; set; }           // true = auto-control needs a pairing/approval step
    public bool AutoAdded { get; set; }              // added automatically by the background scanner
    public DateTime LastSeen { get; set; } = DateTime.UtcNow;
    public bool Accepted { get; set; }               // user approved it into the system
    public bool Online => (DateTime.UtcNow - LastSeen).TotalSeconds < 150;   // watched: 3 missed scans
}

/// <summary>
/// Discovers AV gear on the boat LAN (SSDP/UPnP + a Roku probe) and controls the tractable
/// protocols: Roku (ECP over HTTP) and generic UPnP MediaRenderer (SOAP AVTransport +
/// RenderingControl — covers many DLNA TVs, Sonos, and AV receivers). Brand adapters
/// (Samsung/LG/Denon) can be added behind the same ControlAsync switch.
/// </summary>
public sealed class AvService
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(5) };
    private readonly Dictionary<string, AvDevice> _devices = new();
    private readonly object _lock = new();
    private CancellationTokenSource? _scanCts;

    public IReadOnlyList<AvDevice> Devices { get { lock (_lock) return _devices.Values.OrderBy(d => d.Name).ToList(); } }

    /// <summary>Fired whenever the background scanner finds a device it hasn't seen before.</summary>
    public event Action<AvDevice>? OnNewDevice;

    /// <summary>Starts the always-on background thread that watches the LAN for new devices,
    /// investigates each, and auto-adds controllable AV/automation gear (flagging any that
    /// need a pairing/approval step). Idempotent.</summary>
    public void StartBackgroundScan()
    {
        if (_scanCts is not null) return;
        _scanCts = new CancellationTokenSource();
        _ = ScanLoopAsync(_scanCts.Token);
    }

    public void StopBackgroundScan() { try { _scanCts?.Cancel(); } catch { } _scanCts = null; }

    private async Task ScanLoopAsync(CancellationToken ct)
    {
        // First sweep shortly after startup, then re-scan periodically.
        try { await Task.Delay(3000, ct); } catch { return; }
        while (!ct.IsCancellationRequested)
        {
            try { await DiscoverAsync(); }
            catch (Exception ex) { Ip2slClient.Log("[av] scan error: " + ex.Message); }
            try { await Task.Delay(TimeSpan.FromSeconds(45), ct); } catch { break; }
        }
    }

    /// <summary>Adds/updates a device; returns true if it is brand-new (fires OnNewDevice).</summary>
    private bool Upsert(AvDevice d, bool background)
    {
        bool isNew;
        lock (_lock)
        {
            if (_devices.TryGetValue(d.Id, out var existing))
            {
                // Keep watching a known device: refresh last-seen, preserve acceptance.
                existing.LastSeen = DateTime.UtcNow;
                existing.Name = d.Name.Length > 0 ? d.Name : existing.Name;
                return false;
            }
            isNew = true;
            d.AutoAdded = background;
            d.LastSeen = DateTime.UtcNow;
            _devices[d.Id] = d;
        }
        if (isNew)
        {
            Ip2slClient.Log($"[av] {(background ? "auto-discovered" : "found")} {d.Type} '{d.Name}' ({d.Ip}, {d.Protocol})" +
                            (d.NeedsPairing ? " — needs pairing" : ""));
            OnNewDevice?.Invoke(d);
        }
        return isNew;
    }

    /// <summary>Runs an SSDP sweep on every interface and returns the merged device list.</summary>
    public async Task<IReadOnlyList<AvDevice>> DiscoverAsync(bool background = false)
    {
        var locations = new HashSet<string>();
        foreach (var st in new[] { "ssdp:all", "roku:ecp", "urn:schemas-upnp-org:device:MediaRenderer:1" })
            await SsdpSearchAsync(st, locations);

        foreach (var loc in locations)
        {
            try { await IngestDescriptionAsync(loc, background); } catch { }
        }
        return Devices;
    }

    private static async Task SsdpSearchAsync(string st, HashSet<string> locations)
    {
        var req = Encoding.ASCII.GetBytes(
            "M-SEARCH * HTTP/1.1\r\nHOST: 239.255.255.250:1900\r\nMAN: \"ssdp:discover\"\r\nMX: 2\r\nST: " + st + "\r\n\r\n");
        var multicast = new IPEndPoint(IPAddress.Parse("239.255.255.250"), 1900);

        foreach (var local in LocalIPv4s())
        {
            using var udp = new UdpClient();
            try
            {
                udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                udp.Client.Bind(new IPEndPoint(local, 0));
                udp.Client.ReceiveTimeout = 3000;
                for (int i = 0; i < 2; i++) await udp.SendAsync(req, req.Length, multicast);

                var deadline = DateTime.UtcNow.AddSeconds(3);
                while (DateTime.UtcNow < deadline)
                {
                    try
                    {
                        var task = udp.ReceiveAsync();
                        if (await Task.WhenAny(task, Task.Delay(1500)) != task) break;
                        var text = Encoding.ASCII.GetString(task.Result.Buffer);
                        var m = Regex.Match(text, @"(?im)^LOCATION:\s*(.+)$");
                        if (m.Success) locations.Add(m.Groups[1].Value.Trim());
                    }
                    catch { break; }
                }
            }
            catch { }
        }
    }

    private async Task IngestDescriptionAsync(string location, bool background)
    {
        var xml = await Http.GetStringAsync(location);
        var doc = XDocument.Parse(xml);
        XNamespace ns = "urn:schemas-upnp-org:device-1-0";
        var dev = doc.Descendants(ns + "device").FirstOrDefault();
        if (dev is null) return;

        string Val(string n) => dev.Element(ns + n)?.Value ?? "";
        var ip = new Uri(location).Host;
        var manu = Val("manufacturer");
        var model = Val("modelName");
        var name = Val("friendlyName");
        var dtype = Val("deviceType");

        // Skip routers / gateways — we only want AV renderers.
        if (dtype.Contains("WFADevice") || dtype.Contains("InternetGatewayDevice") || dtype.Contains("WANDevice"))
            return;
        var isRenderer = dtype.Contains("MediaRenderer");

        var lo = (manu + " " + model + " " + name).ToLowerInvariant();
        var needsPairing = lo.Contains("samsung") || lo.Contains("lg ") || lo.Contains("webos");
        var dev2 = new AvDevice
        {
            Id = ip + ":upnp",
            Ip = ip,
            Name = name.Length > 0 ? name : ip,
            Manufacturer = manu,
            Model = model,
            Protocol = "upnp",
            Type = ClassifyType(dtype, manu, model),
            NeedsPairing = needsPairing,
        };

        // Extract AVTransport / RenderingControl control URLs (make absolute).
        var baseUri = new Uri(location);
        foreach (var svc in doc.Descendants(ns + "service"))
        {
            var type = svc.Element(ns + "serviceType")?.Value ?? "";
            var ctrl = svc.Element(ns + "controlURL")?.Value ?? "";
            if (ctrl.Length == 0) continue;
            var abs = new Uri(baseUri, ctrl).ToString();
            if (type.Contains("AVTransport")) dev2.AvTransportUrl = abs;
            else if (type.Contains("RenderingControl")) dev2.RenderingControlUrl = abs;
        }

        if (isRenderer || dev2.AvTransportUrl is not null || dev2.RenderingControlUrl is not null)
            Upsert(dev2, background);

        // Roku exposes ECP on :8060 — detect and add a Roku control entry.
        if (manu.Contains("Roku", StringComparison.OrdinalIgnoreCase) ||
            model.Contains("Roku", StringComparison.OrdinalIgnoreCase))
        {
            Upsert(new AvDevice
            {
                Id = ip + ":roku", Ip = ip, Name = name.Length > 0 ? name : "Roku",
                Manufacturer = "Roku", Model = model, Protocol = "roku_ecp", Type = "mediaplayer",
            }, background);
        }
    }

    /// <summary>Finds a device by id, or by a fuzzy name/type match (for Claude's NL control).</summary>
    public AvDevice? Match(string idOrName)
    {
        lock (_lock)
        {
            if (_devices.TryGetValue(idOrName, out var exact)) return exact;
            var q = idOrName.ToLowerInvariant();
            return _devices.Values.FirstOrDefault(d => d.Name.ToLowerInvariant().Contains(q))
                ?? _devices.Values.FirstOrDefault(d => d.Type.Equals(q, StringComparison.OrdinalIgnoreCase))
                ?? _devices.Values.FirstOrDefault(d => d.Type.Contains(q));
        }
    }

    private static string ClassifyType(string deviceType, string manu, string model)
    {
        var s = (deviceType + " " + manu + " " + model).ToLowerInvariant();
        if (s.Contains("tv") || s.Contains("television")) return "tv";
        if (s.Contains("receiver") || s.Contains("amp") || s.Contains("denon") || s.Contains("marantz") ||
            s.Contains("yamaha") || s.Contains("onkyo") || s.Contains("fusion")) return "amplifier";
        if (s.Contains("sonos") || s.Contains("speaker") || s.Contains("audio")) return "speaker";
        if (s.Contains("mediarenderer")) return "mediaplayer";
        return "media";
    }

    /// <summary>Executes an action against a device. Returns a short status string.</summary>
    public async Task<string> ControlAsync(string id, string action, string? value)
    {
        if (!_devices.TryGetValue(id, out var d)) return "Unknown device.";
        try
        {
            return d.Protocol switch
            {
                "roku_ecp" => await RokuAsync(d, action),
                "upnp" => await UpnpAsync(d, action, value),
                _ => $"Control for '{d.Protocol}' not implemented.",
            };
        }
        catch (Exception ex) { return "Error: " + ex.Message; }
    }

    // ---- Roku ECP (HTTP) ----
    private static readonly Dictionary<string, string> RokuKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        ["power"] = "Power", ["power_on"] = "PowerOn", ["power_off"] = "PowerOff",
        ["vol_up"] = "VolumeUp", ["vol_down"] = "VolumeDown", ["mute"] = "VolumeMute",
        ["play"] = "Play", ["pause"] = "Play", ["home"] = "Home", ["back"] = "Back",
        ["up"] = "Up", ["down"] = "Down", ["left"] = "Left", ["right"] = "Right",
        ["select"] = "Select", ["ok"] = "Select", ["input"] = "InputHDMI1",
    };

    private static async Task<string> RokuAsync(AvDevice d, string action)
    {
        if (!RokuKeys.TryGetValue(action, out var key)) return $"Roku: unknown action '{action}'.";
        var resp = await Http.PostAsync($"http://{d.Ip}:8060/keypress/{key}", null);
        return resp.IsSuccessStatusCode ? $"Roku {key} sent." : $"Roku {key} failed ({(int)resp.StatusCode}).";
    }

    // ---- Generic UPnP MediaRenderer (SOAP) ----
    private static async Task<string> UpnpAsync(AvDevice d, string action, string? value)
    {
        switch (action)
        {
            case "play":
                return await SoapAsync(d.AvTransportUrl, "AVTransport", "Play",
                    "<InstanceID>0</InstanceID><Speed>1</Speed>");
            case "pause":
                return await SoapAsync(d.AvTransportUrl, "AVTransport", "Pause", "<InstanceID>0</InstanceID>");
            case "stop":
                return await SoapAsync(d.AvTransportUrl, "AVTransport", "Stop", "<InstanceID>0</InstanceID>");
            case "mute":
                return await SoapAsync(d.RenderingControlUrl, "RenderingControl", "SetMute",
                    "<InstanceID>0</InstanceID><Channel>Master</Channel><DesiredMute>1</DesiredMute>");
            case "unmute":
                return await SoapAsync(d.RenderingControlUrl, "RenderingControl", "SetMute",
                    "<InstanceID>0</InstanceID><Channel>Master</Channel><DesiredMute>0</DesiredMute>");
            case "volume":
                var v = int.TryParse(value, out var n) ? Math.Clamp(n, 0, 100) : 20;
                return await SoapAsync(d.RenderingControlUrl, "RenderingControl", "SetVolume",
                    $"<InstanceID>0</InstanceID><Channel>Master</Channel><DesiredVolume>{v}</DesiredVolume>");
            default:
                return $"UPnP: unsupported action '{action}'.";
        }
    }

    private static async Task<string> SoapAsync(string? controlUrl, string service, string action, string args)
    {
        if (string.IsNullOrEmpty(controlUrl)) return $"Device has no {service} control endpoint.";
        var svcType = $"urn:schemas-upnp-org:service:{service}:1";
        var body =
            "<?xml version=\"1.0\"?><s:Envelope xmlns:s=\"http://schemas.xmlsoap.org/soap/envelope/\" " +
            "s:encodingStyle=\"http://schemas.xmlsoap.org/soap/encoding/\"><s:Body>" +
            $"<u:{action} xmlns:u=\"{svcType}\">{args}</u:{action}></s:Body></s:Envelope>";
        using var req = new HttpRequestMessage(HttpMethod.Post, controlUrl)
        { Content = new StringContent(body, Encoding.UTF8, "text/xml") };
        req.Headers.Add("SOAPACTION", $"\"{svcType}#{action}\"");
        var resp = await Http.SendAsync(req);
        return resp.IsSuccessStatusCode ? $"{action} sent." : $"{action} failed ({(int)resp.StatusCode}).";
    }

    public string DevicesJson()
    {
        lock (_lock)
            return JsonSerializer.Serialize(_devices.Values.OrderBy(d => d.Name).Select(d => new
            {
                id = d.Id, name = d.Name, ip = d.Ip, type = d.Type,
                manufacturer = d.Manufacturer, model = d.Model, protocol = d.Protocol,
                online = d.Online, needsPairing = d.NeedsPairing, accepted = d.Accepted,
            }));
    }

    public void Accept(string id) { lock (_lock) { if (_devices.TryGetValue(id, out var d)) d.Accepted = true; } }

    private static IEnumerable<IPAddress> LocalIPv4s()
    {
        foreach (var ni in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
        {
            if (ni.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up) continue;
            if (ni.NetworkInterfaceType == System.Net.NetworkInformation.NetworkInterfaceType.Loopback) continue;
            foreach (var ua in ni.GetIPProperties().UnicastAddresses)
                if (ua.Address.AddressFamily == AddressFamily.InterNetwork) yield return ua.Address;
        }
    }
}
