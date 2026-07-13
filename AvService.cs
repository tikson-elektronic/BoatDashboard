using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Makaretu.Dns;

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
    public string Psk { get; set; } = "0000";        // Sony IP-control pre-shared key (set the same on the TV)
    public string? ClientKey { get; set; }           // LG webOS pairing key / Samsung token, persisted after first pair
    public string? SamsungAppName { get; set; }      // the name shown in the TV's Allow prompt / device list
    public string? Mac { get; set; }                 // hardware MAC, for Wake-on-LAN power-on
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

    // Persisted pairing tokens (Samsung/LG), so a device stays paired across restarts.
    private static readonly string TokenFile = @"C:\voms\av-tokens.json";
    private static readonly Dictionary<string, string> _tokens = LoadTokens();
    private static Dictionary<string, string> LoadTokens()
    {
        try { if (File.Exists(TokenFile)) return JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(TokenFile)) ?? new(); }
        catch { }
        return new();
    }
    internal static string? GetToken(string id) => _tokens.TryGetValue(id, out var t) ? t : null;

    // Persisted MAC addresses. A TV only reports its MAC over its HTTP API while it's ON, but Wake-on-LAN
    // is needed precisely when it's OFF — so once we learn a MAC we save it and reuse it forever.
    private static readonly string MacFile = @"C:\voms\av-macs.json";
    private static readonly Dictionary<string, string> _macs = LoadMacs();
    private static Dictionary<string, string> LoadMacs()
    {
        try { if (File.Exists(MacFile)) return JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(MacFile)) ?? new(); }
        catch { }
        return new();
    }
    internal static string? GetMac(string id) => _macs.TryGetValue(id, out var m) ? m : null;
    internal static void SaveMac(string id, string mac)
    {
        try { lock (_macs) { _macs[id] = mac; Directory.CreateDirectory(Path.GetDirectoryName(MacFile)!); File.WriteAllText(MacFile, JsonSerializer.Serialize(_macs)); } }
        catch { }
    }

    // Samsung ties its auth token to the CONTROLLER NAME we present. We pin the name that actually
    // paired (alongside the token) so every later connect reuses it; until paired we mint a fresh name
    // each run so a stale/denied TV entry can't silently block us.
    private static readonly string NamesFile = @"C:\voms\av-samsung-names.json";
    private static Dictionary<string, string>? _samsungNames;
    private static Dictionary<string, string> SamsungNames()
    {
        if (_samsungNames != null) return _samsungNames;
        try { _samsungNames = File.Exists(NamesFile) ? JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(NamesFile)) ?? new() : new(); }
        catch { _samsungNames = new(); }
        return _samsungNames;
    }
    private static void PinSamsungName(string id, string name)
    {
        var map = SamsungNames();
        lock (map) { map[id] = name; try { Directory.CreateDirectory(Path.GetDirectoryName(NamesFile)!); File.WriteAllText(NamesFile, JsonSerializer.Serialize(map)); } catch { } }
    }
    private static string SamsungPairName(string id, bool hasToken)
    {
        var map = SamsungNames();
        if (map.TryGetValue(id, out var pinned) && !string.IsNullOrEmpty(pinned)) return pinned;
        // Not yet paired on this TV → fresh name forces a clean Allow prompt. Persisted only on success.
        return "Lagoon630-" + Guid.NewGuid().ToString("N").Substring(0, 4).ToUpperInvariant();
    }
    internal static void SaveToken(string id, string token)
    {
        try
        {
            lock (_tokens) { _tokens[id] = token; Directory.CreateDirectory(Path.GetDirectoryName(TokenFile)!); File.WriteAllText(TokenFile, JsonSerializer.Serialize(_tokens)); }
        }
        catch { }
    }

    [DllImport("iphlpapi.dll", ExactSpelling = true)]
    private static extern int SendARP(uint destIp, uint srcIp, byte[] macAddr, ref int macLen);
    /// <summary>Best-effort MAC lookup for an IP via the ARP cache (fallback when we didn't read it from the device).</summary>
    private static string? ArpLookup(string ip)
    {
        try
        {
            var bytes = IPAddress.Parse(ip).GetAddressBytes();
            uint dest = BitConverter.ToUInt32(bytes, 0);
            var mac = new byte[6]; int len = 6;
            if (SendARP(dest, 0, mac, ref len) == 0 && len == 6 && mac.Any(b => b != 0))
                return string.Concat(mac.Select(b => b.ToString("X2")));
        }
        catch { }
        return null;
    }

    /// <summary>Sends a Wake-on-LAN magic packet to power on a device that's fully off (e.g. a Samsung TV,
    /// which can't be woken by its WebSocket API once asleep). Broadcasts on the LAN to the given MAC.</summary>
    public static bool WakeOnLan(string? mac, string? targetIp = null)
    {
        var m = (mac ?? "").Replace(":", "").Replace("-", "").Replace(".", "");
        if (m.Length != 12) return false;
        try
        {
            var macBytes = Enumerable.Range(0, 6).Select(i => Convert.ToByte(m.Substring(i * 2, 2), 16)).ToArray();
            var packet = new byte[102];
            for (int i = 0; i < 6; i++) packet[i] = 0xFF;
            for (int i = 1; i <= 16; i++) Array.Copy(macBytes, 0, packet, i * 6, 6);

            // Destinations: the global broadcast plus the TV's own /24 directed broadcast (e.g. 192.168.20.255)
            // so the packet lands on the TV's subnet even though this PC is multi-homed.
            var dests = new List<IPAddress> { IPAddress.Broadcast };
            if (IPAddress.TryParse(targetIp, out var tip) && tip.AddressFamily == AddressFamily.InterNetwork)
            {
                var bb = tip.GetAddressBytes(); bb[3] = 255;
                dests.Add(new IPAddress(bb));
            }

            int sent = 0;
            // Blast from EVERY up IPv4 interface — don't rely on the default route choosing the TV's NIC.
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up) continue;
                foreach (var ua in ni.GetIPProperties().UnicastAddresses)
                {
                    if (ua.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                    try
                    {
                        using var udp = new UdpClient(new IPEndPoint(ua.Address, 0)) { EnableBroadcast = true };
                        foreach (var dst in dests)
                            foreach (var port in new[] { 9, 7 })
                            { udp.Send(packet, packet.Length, new IPEndPoint(dst, port)); sent++; }
                    }
                    catch { }
                }
            }
            if (sent == 0)   // fallback: default interface only
            {
                using var udp = new UdpClient { EnableBroadcast = true };
                udp.Send(packet, packet.Length, new IPEndPoint(IPAddress.Broadcast, 9));
                udp.Send(packet, packet.Length, new IPEndPoint(IPAddress.Broadcast, 7));
                sent = 2;
            }
            Ip2slClient.Log($"[av] Wake-on-LAN sent to {m} via {sent} endpoint(s), target={targetIp}");
            return true;
        }
        catch (Exception ex) { Ip2slClient.Log("[av] WoL failed: " + ex.Message); return false; }
    }

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
    /// <summary>
    /// Manually add an AV device by IP + protocol. Needed for devices on a different subnet than the
    /// dashboard, which SSDP/mDNS discovery (multicast, link-local) can't reach even when they're
    /// routable by unicast IP (e.g. a Samsung TV on 192.168.20.x). Enriches Samsung name/model from
    /// the TV's own API when possible.
    /// </summary>
    public async Task<AvDevice> AddByIpAsync(string ip, string protocol, string? name = null)
    {
        protocol = (protocol ?? "").Trim().ToLowerInvariant();
        if (protocol == "roku") protocol = "roku_ecp";
        string type = protocol switch
        {
            "samsung" or "lg" or "sony" or "roku_ecp" or "firetv" => "tv",
            "denon" or "yamaha" => "amplifier",
            "sonos" or "upnp" => "speaker",
            _ => "media",
        };
        string? mac = null;
        if (protocol == "samsung")
        {
            try
            {
                using var doc = JsonDocument.Parse(await Http.GetStringAsync($"http://{ip}:8001/api/v2/"));
                if (doc.RootElement.TryGetProperty("device", out var dev))
                {
                    if (string.IsNullOrWhiteSpace(name))
                        name = dev.TryGetProperty("name", out var n) ? n.GetString() : dev.TryGetProperty("modelName", out var m) ? m.GetString() : null;
                    mac = dev.TryGetProperty("wifiMac", out var wm) ? wm.GetString() : (dev.TryGetProperty("mac", out var mm) ? mm.GetString() : null);
                }
            }
            catch { }
        }
        var id = $"{ip}:{protocol}";
        if (!string.IsNullOrWhiteSpace(mac)) SaveMac(id, mac!);   // TV was on and told us its MAC — remember it for Wake-on-LAN when it's off
        else mac = GetMac(id);                                    // TV off / no API — use the MAC we saved earlier
        var d = new AvDevice
        {
            Id = id,
            Ip = ip,
            Name = string.IsNullOrWhiteSpace(name) ? $"{protocol.ToUpperInvariant()} {ip}" : name!,
            Type = type,
            Protocol = protocol,
            NeedsPairing = protocol is "samsung" or "lg",
            Accepted = true,
            Mac = mac,
            ClientKey = GetToken(id),   // restore a previously-saved pairing token so it stays paired
        };
        Upsert(d, background: false);
        return d;
    }

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
                // Heal pairing state from whichever source has it: discovery finds the TV with no token,
                // while the manual add / a live pairing carries the saved token+MAC. Never lose them just
                // because a tokenless discovery arrived later (that caused a TV prompt on every command).
                if (string.IsNullOrEmpty(existing.ClientKey) && !string.IsNullOrEmpty(d.ClientKey)) existing.ClientKey = d.ClientKey;
                if (string.IsNullOrEmpty(existing.Mac) && !string.IsNullOrEmpty(d.Mac)) existing.Mac = d.Mac;
                existing.AvTransportUrl ??= d.AvTransportUrl;
                existing.RenderingControlUrl ??= d.RenderingControlUrl;
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

        try { await DiscoverEspHomeAsync(background); } catch (Exception ex) { Ip2slClient.Log("[av] esphome error: " + ex.Message); }
        return Devices;
    }

    /// <summary>Discovers ESPHome nodes over mDNS (_esphomelib._tcp). With the web_server
    /// component they are controlled over plain HTTP (§ ControlAsync 'esphome').</summary>
    private async Task DiscoverEspHomeAsync(bool background)
    {
        using var mdns = new MulticastService();
        var srv = new Dictionary<string, (string host, int port)>(StringComparer.OrdinalIgnoreCase);
        var addr = new Dictionary<string, IPAddress>(StringComparer.OrdinalIgnoreCase);

        mdns.AnswerReceived += (_, e) =>
        {
            foreach (var rec in e.Message.Answers.Concat(e.Message.AdditionalRecords))
            {
                if (rec is SRVRecord s) srv[s.Name.ToString()] = (s.Target.ToString(), s.Port);
                else if (rec is ARecord a) addr[a.Name.ToString()] = a.Address;
            }
        };

        mdns.Start();
        try
        {
            mdns.SendQuery("_esphomelib._tcp.local", type: DnsType.PTR);
            await Task.Delay(3000);
        }
        finally { mdns.Stop(); }

        foreach (var (instance, target) in srv)
        {
            if (!addr.TryGetValue(target.host, out var ip)) continue;
            var friendly = instance.Split('.')[0];
            Upsert(new AvDevice
            {
                Id = ip + ":esphome",
                Ip = ip.ToString(),
                Name = friendly,
                Manufacturer = "ESPHome",
                Protocol = "esphome",
                Type = "automation",
            }, background);
        }
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
        var protocol = BrandProtocol(lo);
        var needsPairing = protocol is "samsung" or "lg";   // these prompt on the TV to authorise
        var discId = ip + ":" + protocol;
        var dev2 = new AvDevice
        {
            Id = discId,
            Ip = ip,
            Name = name.Length > 0 ? name : ip,
            Manufacturer = manu,
            Model = model,
            Protocol = protocol,
            Type = ClassifyType(dtype, manu, model),
            NeedsPairing = needsPairing,
            ClientKey = GetToken(discId),   // stay paired: reuse the saved token so no TV prompt on control
            Mac = GetMac(discId),           // keep the MAC so Wake-on-LAN works even with the TV off
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

        if (isRenderer || dev2.AvTransportUrl is not null || dev2.RenderingControlUrl is not null || protocol != "upnp")
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

    /// <summary>Maps a device's brand text to its control protocol.</summary>
    private static string BrandProtocol(string lo)
    {
        if (lo.Contains("samsung")) return "samsung";
        if (lo.Contains("webos") || Regex.IsMatch(lo, @"\blg\b")) return "lg";
        if (lo.Contains("sony") || lo.Contains("bravia")) return "sony";
        if (lo.Contains("yamaha")) return "yamaha";
        if (lo.Contains("denon") || lo.Contains("marantz")) return "denon";
        if (lo.Contains("sonos")) return "sonos";
        if (lo.Contains("roku")) return "roku_ecp";
        if (lo.Contains("fire tv") || lo.Contains("aftt") || lo.Contains("firestick") || lo.Contains("amazon")) return "firetv";
        return "upnp";
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
        // Accept an exact device id OR a fuzzy name/type (e.g. "salon tv", "sonos") from any caller —
        // the LAN and WebView control paths pass raw user input, so resolve it here centrally.
        if (!_devices.TryGetValue(id, out var d))
        {
            d = Match(id);
            if (d is null) return $"No AV device matching '{id}'.";
        }
        try
        {
            return d.Protocol switch
            {
                "roku_ecp" => await RokuAsync(d, action),
                "upnp" or "sonos" => await UpnpAsync(d, action, value),
                "esphome" => await EspHomeAsync(d, action, value),
                "denon" => await DenonAsync(d, action, value),
                "yamaha" => await YamahaAsync(d, action, value),
                "sony" => await SonyAsync(d, action),
                "samsung" => await SamsungAsync(d, action),
                "lg" => await LgAsync(d, action),
                "firetv" => await FireTvAsync(d, action),
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

    private static readonly Dictionary<string, string> RokuApps = new(StringComparer.OrdinalIgnoreCase)
    { ["netflix"] = "12", ["youtube"] = "837", ["prime"] = "13", ["disney"] = "291097", ["spotify"] = "22297" };

    private static async Task<string> RokuAsync(AvDevice d, string action)
    {
        if (RokuApps.TryGetValue(action, out var appId))
        {
            var lr = await Http.PostAsync($"http://{d.Ip}:8060/launch/{appId}", null);
            return lr.IsSuccessStatusCode ? $"Roku launched {action}." : $"Roku launch {action} failed ({(int)lr.StatusCode}).";
        }
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

    // ---- Denon / Marantz AVR (HTTP goform) ----
    private static async Task<string> DenonAsync(AvDevice d, string action, string? value)
    {
        string cmd = action switch
        {
            "power" or "power_on" => "PWON", "power_off" => "PWSTANDBY",
            "vol_up" => "MVUP", "vol_down" => "MVDOWN",
            "mute" => "MUON", "unmute" => "MUOFF",
            "volume" => "MV" + (int.TryParse(value, out var v) ? Math.Clamp(v, 0, 98).ToString("D2") : "50"),
            "input" => "SI" + (value ?? "TV").ToUpperInvariant(),
            _ => "",
        };
        if (cmd.Length == 0) return $"Denon: unknown action '{action}'.";
        var r = await Http.GetAsync($"http://{d.Ip}/goform/formiPhoneAppDirect.xml?{cmd}");
        return r.IsSuccessStatusCode ? $"Denon {cmd} sent." : $"Denon {cmd} failed ({(int)r.StatusCode}).";
    }

    // ---- Yamaha AVR (YXC HTTP) ----
    private static async Task<string> YamahaAsync(AvDevice d, string action, string? value)
    {
        string path = action switch
        {
            "power" or "power_on" => "setPower?power=on", "power_off" => "setPower?power=standby",
            "vol_up" => "setVolume?volume=up", "vol_down" => "setVolume?volume=down",
            "volume" => "setVolume?volume=" + (int.TryParse(value, out var v) ? v : 50),
            "mute" => "setMute?enable=true", "unmute" => "setMute?enable=false",
            "input" => "setInput?input=" + (value ?? "hdmi1"),
            _ => "",
        };
        if (path.Length == 0) return $"Yamaha: unknown action '{action}'.";
        var r = await Http.GetAsync($"http://{d.Ip}/YamahaExtendedControl/v1/main/{path}");
        return r.IsSuccessStatusCode ? $"Yamaha {action} sent." : $"Yamaha {action} failed ({(int)r.StatusCode}).";
    }

    // ---- Sony Bravia (IRCC over HTTP; needs the TV's IP-control pre-shared key) ----
    private static async Task<string> SonyAsync(AvDevice d, string action)
    {
        var codes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["power"] = "AAAAAQAAAAEAAAAVAw==", ["vol_up"] = "AAAAAQAAAAEAAAASAw==", ["vol_down"] = "AAAAAQAAAAEAAAATAw==",
            ["mute"] = "AAAAAQAAAAEAAAAUAw==", ["home"] = "AAAAAQAAAAEAAAAQAw==", ["netflix"] = "AAAAAgAAABoAAAB8Aw==",
            ["up"] = "AAAAAQAAAAEAAAB0Aw==", ["down"] = "AAAAAQAAAAEAAAB1Aw==", ["left"] = "AAAAAQAAAAEAAAA0Aw==",
            ["right"] = "AAAAAQAAAAEAAAAzAw==", ["select"] = "AAAAAQAAAAEAAABlAw==",
        };
        if (!codes.TryGetValue(action, out var code)) return $"Sony: unknown action '{action}'.";
        var soap = "<?xml version=\"1.0\"?><s:Envelope xmlns:s=\"http://schemas.xmlsoap.org/soap/envelope/\" " +
            "s:encodingStyle=\"http://schemas.xmlsoap.org/soap/encoding/\"><s:Body>" +
            $"<u:X_SendIRCC xmlns:u=\"urn:schemas-sony-com:service:IRCC:1\"><IRCCCode>{code}</IRCCCode></u:X_SendIRCC></s:Body></s:Envelope>";
        using var req = new HttpRequestMessage(HttpMethod.Post, $"http://{d.Ip}/sony/IRCC")
        { Content = new StringContent(soap, Encoding.UTF8, "text/xml") };
        req.Headers.Add("SOAPACTION", "\"urn:schemas-sony-com:service:IRCC:1#X_SendIRCC\"");
        req.Headers.Add("X-Auth-PSK", string.IsNullOrEmpty(d.Psk) ? "0000" : d.Psk);   // set the same PSK on the TV (Settings → Network → IP control)
        var r = await Http.SendAsync(req);
        return r.IsSuccessStatusCode ? $"Sony {action} sent." : $"Sony {action} failed ({(int)r.StatusCode}) — set the TV's IP-control PSK to match ('{(string.IsNullOrEmpty(d.Psk) ? "0000" : d.Psk)}').";
    }

    // ---- Samsung Tizen (WebSocket remote) ----
    private static async Task<string> SamsungAsync(AvDevice d, string action)
    {
        var keys = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            // power / volume
            ["power"] = "KEY_POWER", ["power_off"] = "KEY_POWER", ["poweroff"] = "KEY_POWER",
            ["vol_up"] = "KEY_VOLUP", ["volume_up"] = "KEY_VOLUP", ["vol_down"] = "KEY_VOLDOWN", ["volume_down"] = "KEY_VOLDOWN",
            ["mute"] = "KEY_MUTE", ["unmute"] = "KEY_MUTE",   // KEY_MUTE toggles
            // navigation
            ["home"] = "KEY_HOME", ["up"] = "KEY_UP", ["down"] = "KEY_DOWN", ["left"] = "KEY_LEFT", ["right"] = "KEY_RIGHT",
            ["select"] = "KEY_ENTER", ["enter"] = "KEY_ENTER", ["ok"] = "KEY_ENTER", ["back"] = "KEY_RETURN", ["return"] = "KEY_RETURN", ["exit"] = "KEY_EXIT",
            // menus / settings / info
            ["menu"] = "KEY_MENU", ["settings"] = "KEY_MENU", ["tools"] = "KEY_TOOLS", ["info"] = "KEY_INFO",
            ["guide"] = "KEY_GUIDE", ["smarthub"] = "KEY_HOME", ["hub"] = "KEY_HOME", ["contents"] = "KEY_CONTENTS",
            ["caption"] = "KEY_CAPTION", ["subtitle"] = "KEY_CAPTION", ["sleep"] = "KEY_SLEEP", ["extra"] = "KEY_MORE",
            // channels
            ["ch_up"] = "KEY_CHUP", ["channel_up"] = "KEY_CHUP", ["ch_down"] = "KEY_CHDOWN", ["channel_down"] = "KEY_CHDOWN",
            ["ch_list"] = "KEY_CH_LIST", ["prech"] = "KEY_PRECH", ["last"] = "KEY_PRECH",
            // inputs / source
            ["source"] = "KEY_SOURCE", ["input"] = "KEY_SOURCE", ["hdmi"] = "KEY_HDMI",
            ["hdmi1"] = "KEY_HDMI1", ["hdmi2"] = "KEY_HDMI2", ["hdmi3"] = "KEY_HDMI3", ["hdmi4"] = "KEY_HDMI4",
            ["tv"] = "KEY_TV", ["dtv"] = "KEY_DTV", ["antenna"] = "KEY_ANTENA",
            // transport
            ["play"] = "KEY_PLAY", ["pause"] = "KEY_PAUSE", ["playpause"] = "KEY_PLAY_BACK", ["stop"] = "KEY_STOP",
            ["rewind"] = "KEY_REWIND", ["rw"] = "KEY_REWIND", ["ff"] = "KEY_FF", ["fastforward"] = "KEY_FF",
            ["record"] = "KEY_REC", ["next"] = "KEY_FF_", ["prev"] = "KEY_REWIND_",
            // number pad
            ["0"] = "KEY_0", ["1"] = "KEY_1", ["2"] = "KEY_2", ["3"] = "KEY_3", ["4"] = "KEY_4",
            ["5"] = "KEY_5", ["6"] = "KEY_6", ["7"] = "KEY_7", ["8"] = "KEY_8", ["9"] = "KEY_9",
            // colour keys
            ["red"] = "KEY_RED", ["green"] = "KEY_GREEN", ["yellow"] = "KEY_YELLOW", ["blue"] = "KEY_BLUE",
        };

        // Power ON a TV that's fully off — the WebSocket API can't wake it (its network is asleep),
        // so send a Wake-on-LAN magic packet to its MAC. "power" (toggle) still uses KEY_POWER below.
        if (action is "power_on" or "wake" or "on")
        {
            var mac = d.Mac ?? GetMac(d.Id) ?? ArpLookup(d.Ip);   // saved MAC, else last-known ARP (only if TV replied recently)
            return WakeOnLan(mac, d.Ip)
                ? "Samsung: Wake-on-LAN sent — the TV should power on (needs the TV's network-standby setting ON to work)."
                : "Samsung: no MAC known for Wake-on-LAN. Re-add the TV while it's on so I can read its MAC.";
        }

        // App launch (Netflix, YouTube, …). The REST launch endpoint works on this TV (POST returns 200);
        // it just needs the CORRECT per-TV app IDs — several stock IDs 404. IDs below were verified live
        // against the Q60AA via GET /api/v2/applications/{id} (name match). Launch is unauthenticated REST.
        var apps = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["netflix"] = "3201907018807", ["youtube"] = "111299001912",
            ["prime"] = "3201910019365", ["primevideo"] = "3201910019365", ["amazon"] = "3201910019365",
            ["disney"] = "3201901017640", ["disneyplus"] = "3201901017640",
            ["spotify"] = "3201606009684", ["hbo"] = "3201601007230", ["hbomax"] = "3201601007230", ["max"] = "3201601007230",
            ["appletv"] = "3201807016597", ["apple"] = "3201807016597",
        };
        if (apps.TryGetValue(action, out var appId))
        {
            try
            {
                using var areq = new HttpRequestMessage(HttpMethod.Post, $"http://{d.Ip}:8001/api/v2/applications/{appId}");
                var ar = await Http.SendAsync(areq);
                return ar.IsSuccessStatusCode
                    ? $"Samsung: launched {action}."
                    : $"Samsung: launch {action} returned HTTP {(int)ar.StatusCode} — app may not be installed under id {appId}.";
            }
            catch (Exception ex) { return $"Samsung: launch {action} failed — {ex.Message}"; }
        }

        if (!keys.TryGetValue(action, out var key)) return $"Samsung: unknown action '{action}'.";
        // Remote keypress payload for the WebSocket control channel.
        string Payload() => JsonSerializer.Serialize(new { method = "ms.remote.control", @params = new { Cmd = "Click", DataOfCmd = key, Option = "false", TypeOfRemote = "SendRemoteKey" } });
        string label = key;
        // Use a stable per-device app name. If we've never paired, and one was tried before, the TV may
        // remember it as denied and refuse (ms.channel.timeOut) — so pick a fresh name until paired.
        // The controller name is the pairing identity: the TV stores an auth token AGAINST this name.
        // If a name was authorised before but its token got lost, reconnecting with the same name and no
        // token makes Tizen reject instantly (timeOut/unauthorized) without re-prompting. So when we have
        // NO saved token, present a fresh name to force a clean Allow prompt; once paired, the name is
        // pinned in _samsungNames so the saved token keeps matching it on every later connect.
        d.SamsungAppName ??= SamsungPairName(d.Id, !string.IsNullOrEmpty(d.ClientKey));
        var name = Convert.ToBase64String(Encoding.UTF8.GetBytes(d.SamsungAppName));

        // Modern Samsung (2016+) uses secure wss:8002 with a token issued on first authorisation.
        // We accept the TV's self-signed cert, capture the token from the connect frame, and persist it
        // so later commands are silent. Fall back to legacy ws:8001 for older sets.
        using var ws = new ClientWebSocket();
        ws.Options.RemoteCertificateValidationCallback = (_, _, _, _) => true;   // TV serves a self-signed cert
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));

        string tokenQs = string.IsNullOrEmpty(d.ClientKey) ? "" : $"&token={d.ClientKey}";
        bool secure = true;
        try { await ws.ConnectAsync(new Uri($"wss://{d.Ip}:8002/api/v2/channels/samsung.remote.control?name={name}{tokenQs}"), cts.Token); Ip2slClient.Log($"[samsung] wss:8002 connected state={ws.State}"); }
        catch (Exception cex)
        {
            Ip2slClient.Log("[samsung] wss:8002 connect failed: " + cex.Message + " -> trying legacy ws:8001");
            secure = false;
            using var ws2 = new ClientWebSocket();
            using var cts2 = new CancellationTokenSource(TimeSpan.FromSeconds(6));
            try { await ws2.ConnectAsync(new Uri($"ws://{d.Ip}:8001/api/v2/channels/samsung.remote.control?name={name}"), cts2.Token); }
            catch { return "Samsung: connect failed — accept the authorisation prompt on the TV, then retry."; }
            await ws2.SendAsync(Encoding.UTF8.GetBytes(Payload()), WebSocketMessageType.Text, true, cts2.Token);
            try { await ws2.CloseAsync(WebSocketCloseStatus.NormalClosure, "", cts2.Token); } catch { }
            return $"Samsung {label} sent (legacy).";
        }

        // On first pairing the TV shows an "Allow BoatDashboard" prompt and only issues the auth token
        // AFTER the user accepts. The prompt stays up only while a client is connected — so if we don't
        // already have a token, HOLD the connection open and keep reading until the token arrives (up to
        // 40 s, giving the user time to grab the remote and accept). With a token, just a quick read.
        bool hadToken = !string.IsNullOrEmpty(d.ClientKey);
        bool denied = false, timedOut = false;
        var buf = new byte[8192];
        var deadline = DateTime.UtcNow.AddSeconds(hadToken ? 4 : 40);
        using (var readCts = new CancellationTokenSource(TimeSpan.FromSeconds(hadToken ? 4 : 42)))
        {
            while (DateTime.UtcNow < deadline && string.IsNullOrEmpty(d.ClientKey))
            {
                WebSocketReceiveResult res;
                try { res = await ws.ReceiveAsync(buf, readCts.Token); }
                catch (Exception rex) { Ip2slClient.Log("[samsung] receive ended: " + rex.Message); break; }
                var frame = Encoding.UTF8.GetString(buf, 0, res.Count);
                Ip2slClient.Log("[samsung] frame: " + (frame.Length > 180 ? frame[..180] : frame));
                try
                {
                    using var doc = JsonDocument.Parse(frame);
                    var root = doc.RootElement;
                    var ev = root.TryGetProperty("event", out var e) ? e.GetString() : "";
                    if (root.TryGetProperty("data", out var data) && data.TryGetProperty("token", out var tok) && tok.GetString() is { Length: > 0 } t)
                    { d.ClientKey = t; SaveToken(d.Id, t); PinSamsungName(d.Id, d.SamsungAppName!); break; }    // accepted → token issued (persist token + the name it was issued against)
                    if (ev == "ms.channel.unauthorized") { denied = true; break; }  // user tapped Deny
                    if (ev == "ms.channel.timeOut") { timedOut = true; break; }     // TV refused the secure channel
                    if (hadToken && ev == "ms.channel.connect") break; // already authorised
                }
                catch { }
            }
        }

        // Authorised (token captured on 8002, or we already had one) → send the key and finish.
        if (!string.IsNullOrEmpty(d.ClientKey) || hadToken)
        {
            try { await ws.SendAsync(Encoding.UTF8.GetBytes(Payload()), WebSocketMessageType.Text, true, cts.Token); } catch { }
            try { await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "", cts.Token); } catch { }
            return $"Samsung {label} sent (paired).";
        }
        if (denied)
        {
            try { await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "", cts.Token); } catch { }
            return "Samsung: you tapped Deny on the TV - re-run and choose Allow to pair.";
        }

        // Secure channel refused (instant timeOut, no prompt) → fall back to the LEGACY ws:8001 channel.
        // Many sets accept unpaired clients there, or at least surface the Allow prompt that 8002 won't.
        if (timedOut && string.IsNullOrEmpty(d.ClientKey))
        {
            try { await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None); } catch { }
            Ip2slClient.Log("[samsung] 8002 refused (timeOut) -> trying legacy ws:8001");
            using var lg = new ClientWebSocket();
            using var lcts = new CancellationTokenSource(TimeSpan.FromSeconds(45));
            try { await lg.ConnectAsync(new Uri($"ws://{d.Ip}:8001/api/v2/channels/samsung.remote.control?name={name}"), lcts.Token); }
            catch (Exception lex) { Ip2slClient.Log("[samsung] 8001 connect failed: " + lex.Message); return "Samsung: TV refused both control channels — enable Settings > General > External Device Manager > Device Connect Manager > Access Notification on the TV."; }

            bool legacyReady = false;
            var ldeadline = DateTime.UtcNow.AddSeconds(40);
            while (DateTime.UtcNow < ldeadline)
            {
                WebSocketReceiveResult lres;
                try { lres = await lg.ReceiveAsync(buf, lcts.Token); }
                catch (Exception rex) { Ip2slClient.Log("[samsung] 8001 receive ended: " + rex.Message); break; }
                var lframe = Encoding.UTF8.GetString(buf, 0, lres.Count);
                Ip2slClient.Log("[samsung] 8001 frame: " + (lframe.Length > 180 ? lframe[..180] : lframe));
                if (lframe.Contains("ms.channel.connect")) { legacyReady = true; break; }
                // 'unauthorized' = the Allow prompt is showing / not yet accepted. KEEP WAITING for the
                // user to tap Allow (which then sends ms.channel.connect). Don't treat it as a denial.
                if (lframe.Contains("ms.channel.timeOut")) break;
            }
            if (legacyReady)
            {
                try { await lg.SendAsync(Encoding.UTF8.GetBytes(Payload()), WebSocketMessageType.Text, true, lcts.Token); } catch { }
                await Task.Delay(400);
                try { await lg.CloseAsync(WebSocketCloseStatus.NormalClosure, "", lcts.Token); } catch { }
                d.ClientKey ??= "legacy";   // mark paired via legacy channel
                return $"Samsung {label} sent (legacy 8001).";
            }
            try { await lg.CloseAsync(WebSocketCloseStatus.NormalClosure, "", lcts.Token); } catch { }
        }

        if (denied) return "Samsung: you tapped Deny on the TV - re-run and choose Allow to pair.";
        return "Samsung: no Allow prompt appeared and the TV rejected both channels. Presenting a fresh "
             + $"controller name ('{d.SamsungAppName}') next run should force a new prompt — accept it on the TV to pair.";
    }

    // ---- LG webOS (SSAP WebSocket) ----
    // webOS pairs once: register with a permission manifest, the TV prompts, and returns a "client-key".
    // We persist that key on the device so subsequent connects register silently. Some actions carry a payload.
    private static readonly string[] LgPermissions =
    {
        "LAUNCH", "CONTROL_AUDIO", "CONTROL_POWER", "CONTROL_INPUT_MEDIA_PLAYBACK",
        "CONTROL_INPUT_TV", "READ_INSTALLED_APPS", "CONTROL_DISPLAY", "CONTROL_INPUT_JOYSTICK",
        "CONTROL_INPUT_MEDIA_RECORDING", "CONTROL_INPUT_TEXT", "CONTROL_MOUSE_AND_KEYBOARD",
    };

    private static async Task<string> LgAsync(AvDevice d, string action)
    {
        // (uri, payload) per action — setMute needs {mute:true}.
        (string uri, object? payload) = action switch
        {
            "vol_up" => ("ssap://audio/volumeUp", (object?)null),
            "vol_down" => ("ssap://audio/volumeDown", null),
            "mute" => ("ssap://audio/setMute", new { mute = true }),
            "unmute" => ("ssap://audio/setMute", new { mute = false }),
            "power" or "power_off" => ("ssap://system/turnOff", null),
            "play" => ("ssap://media.controls/play", null),
            "pause" => ("ssap://media.controls/pause", null),
            "stop" => ("ssap://media.controls/stop", null),
            "home" => ("ssap://system.launcher/close", null),
            _ => ("", null),
        };
        if (uri.Length == 0) return $"LG: unknown action '{action}'.";

        using var ws = new ClientWebSocket();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try { await ws.ConnectAsync(new Uri($"ws://{d.Ip}:3000"), cts.Token); }
        catch { return "LG: connect failed — is the TV on and on the network?"; }

        // 1) Register (include a stored client-key if we have one; else the TV prompts to pair).
        var manifest = new
        {
            manifestVersion = 1,
            permissions = LgPermissions,
        };
        var reg = new Dictionary<string, object?>
        {
            ["forcePairing"] = false,
            ["pairingType"] = "PROMPT",
            ["manifest"] = manifest,
        };
        if (!string.IsNullOrEmpty(d.ClientKey)) reg["client-key"] = d.ClientKey;
        await SendJsonAsync(ws, new { type = "register", id = "register_0", payload = reg }, cts.Token);

        // 2) Read frames until we're registered (capturing the client-key) or time out.
        bool registered = !string.IsNullOrEmpty(d.ClientKey);
        var buf = new byte[16384];
        var sb = new StringBuilder();
        var deadline = DateTime.UtcNow.AddSeconds(6);
        while (!registered && DateTime.UtcNow < deadline)
        {
            WebSocketReceiveResult res;
            try { res = await ws.ReceiveAsync(buf, cts.Token); }
            catch { break; }
            sb.Append(Encoding.UTF8.GetString(buf, 0, res.Count));
            if (!res.EndOfMessage) continue;
            var frame = sb.ToString(); sb.Clear();
            try
            {
                using var doc = JsonDocument.Parse(frame);
                var root = doc.RootElement;
                var t = root.TryGetProperty("type", out var tt) ? tt.GetString() : "";
                if (t == "registered")
                {
                    if (root.TryGetProperty("payload", out var pl) && pl.TryGetProperty("client-key", out var ck))
                        d.ClientKey = ck.GetString();   // persist for silent re-pair next time
                    registered = true;
                }
                else if (t == "error") { break; }
                // "response" to the prompt (PROMPT pairing) arrives before "registered" — keep reading.
            }
            catch { }
        }
        if (!registered)
        {
            try { await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "", cts.Token); } catch { }
            return "LG: waiting for the pairing prompt — accept 'BoatDashboard' on the TV, then retry.";
        }

        // 3) Send the command with any payload.
        object cmd = payload is null
            ? new { type = "request", id = "cmd_0", uri }
            : new { type = "request", id = "cmd_0", uri, payload };
        await SendJsonAsync(ws, cmd, cts.Token);
        try { await ws.ReceiveAsync(buf, cts.Token); } catch { }   // let the command land before closing
        try { await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "", cts.Token); } catch { }
        return $"LG {action} sent.";
    }

    private static Task SendJsonAsync(ClientWebSocket ws, object o, CancellationToken ct)
        => ws.SendAsync(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(o)), WebSocketMessageType.Text, true, ct);

    // ---- Amazon Fire TV / Firestick (ADB over network) ----
    private static async Task<string> FireTvAsync(AvDevice d, string action)
    {
        var codes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["power"] = "26", ["vol_up"] = "24", ["vol_down"] = "25", ["home"] = "3", ["up"] = "19", ["down"] = "20",
            ["left"] = "21", ["right"] = "22", ["select"] = "23", ["play"] = "85", ["pause"] = "85", ["back"] = "4",
        };
        if (!codes.TryGetValue(action, out var kc)) return $"Fire TV: unknown action '{action}'.";
        try
        {
            await RunAdbAsync($"connect {d.Ip}:5555");
            await RunAdbAsync($"-s {d.Ip}:5555 shell input keyevent {kc}");
            return $"Fire TV {action} sent.";
        }
        catch (Exception ex) { return "Fire TV: ADB failed — enable ADB debugging on the device and install platform-tools. " + ex.Message; }
    }

    private static async Task<string> RunAdbAsync(string args)
    {
        var psi = new ProcessStartInfo { FileName = "adb", Arguments = args, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        using var p = Process.Start(psi);
        if (p is null) return "";
        var o = await p.StandardOutput.ReadToEndAsync();
        await p.WaitForExitAsync();
        return o;
    }

    // ---- ESPHome web_server (HTTP) ----
    // action is a web_server path like "switch/relay1/toggle", "light/lamp/turn_on",
    // "cover/blind/open"; 'value' is optional query (e.g. "brightness=200").
    private static async Task<string> EspHomeAsync(AvDevice d, string action, string? value)
    {
        var url = $"http://{d.Ip}/{action.TrimStart('/')}";
        if (!string.IsNullOrEmpty(value)) url += (url.Contains('?') ? "&" : "?") + value;
        var resp = await Http.PostAsync(url, null);
        return resp.IsSuccessStatusCode ? $"ESPHome {action} sent." : $"ESPHome {action} failed ({(int)resp.StatusCode}).";
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
