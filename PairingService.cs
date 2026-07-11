using System.IO;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace BoatDashboard;

/// <summary>
/// Credentials returned by a successful cloud pairing claim and persisted to
/// <c>C:\voms\mqtt.env</c>. See ONBOARD_AGENT_SPEC.md §2.
/// </summary>
public sealed class VomsCredentials
{
    public string VesselId { get; init; } = "";
    public string MqttHost { get; init; } = "159.203.128.160";
    public int MqttPort { get; init; } = 1883;
    public string MqttClientId { get; init; } = "";
    public string MqttUser { get; init; } = "";
    public string MqttPass { get; init; } = "";
    public string TopicPrefix { get; init; } = "";
}

public sealed record ClaimResult(bool Ok, string? Error, VomsCredentials? Creds);

/// <summary>
/// Onboard-PC pairing with the Alive (VOMS) cloud — the "reverse pairing" flow:
/// the operator enters a code generated in the Alive app, this PC submits it
/// with its hardware fingerprint to claim the vessel. Implements ONBOARD_AGENT_SPEC.md §2.
/// </summary>
public static class PairingService
{
    private const string ClaimUrl = "http://159.203.128.160/api/pc/claim";
    private static readonly string ConfigDir = @"C:\voms";
    private static readonly string ConfigFile = Path.Combine(ConfigDir, "mqtt.env");
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };

    /// <summary>Stable per-PC id: SHA256(hostname|primaryMAC) truncated to 32 hex chars.</summary>
    public static string HardwareId
    {
        get
        {
            var host = Environment.MachineName;
            var mac = PrimaryMac();
            var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"{host}|{mac}"));
            return Convert.ToHexString(hash).ToLowerInvariant()[..32];
        }
    }

    public const string HardwareIdSource = "hostname_mac";

    /// <summary>MAC of the first non-internal interface with a non-zero MAC.</summary>
    private static string PrimaryMac()
    {
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)
                continue;
            var bytes = nic.GetPhysicalAddress().GetAddressBytes();
            if (bytes.Length == 0 || bytes.All(b => b == 0))
                continue;
            return string.Join(":", bytes.Select(b => b.ToString("x2")));
        }
        return "00:00:00:00:00:00";
    }

    /// <summary>Loads saved credentials from mqtt.env, or null if unpaired.</summary>
    public static VomsCredentials? LoadSaved()
    {
        try
        {
            if (!File.Exists(ConfigFile)) return null;
            var kv = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var line in File.ReadAllLines(ConfigFile))
            {
                var t = line.Trim();
                if (t.Length == 0 || t.StartsWith('#')) continue;
                var eq = t.IndexOf('=');
                if (eq <= 0) continue;
                kv[t[..eq].Trim()] = t[(eq + 1)..].Trim();
            }
            if (!kv.TryGetValue("VOMS_VESSEL_ID", out var vid) || string.IsNullOrWhiteSpace(vid))
                return null;
            return new VomsCredentials
            {
                VesselId = vid,
                MqttHost = kv.GetValueOrDefault("VOMS_MQTT_HOST", "159.203.128.160"),
                MqttPort = int.TryParse(kv.GetValueOrDefault("VOMS_MQTT_PORT"), out var p) ? p : 1883,
                MqttClientId = kv.GetValueOrDefault("VOMS_MQTT_CLIENT_ID", ""),
                MqttUser = kv.GetValueOrDefault("VOMS_MQTT_USER", ""),
                MqttPass = kv.GetValueOrDefault("VOMS_MQTT_PASS", ""),
                TopicPrefix = kv.GetValueOrDefault("VOMS_TOPIC_PREFIX", ""),
            };
        }
        catch { return null; }
    }

    public static bool IsPaired => LoadSaved() is { VesselId.Length: > 0 };

    /// <summary>Submits a pairing code to the cloud and, on success, persists credentials.</summary>
    public static async Task<ClaimResult> ClaimAsync(string rawCode, CancellationToken ct = default)
    {
        var code = new string(rawCode.Where(char.IsLetterOrDigit).ToArray()).ToUpperInvariant();
        if (code.Length < 6)
            return new ClaimResult(false, "Enter the full pairing code (at least 6 characters).", null);

        try
        {
            var body = JsonSerializer.Serialize(new
            {
                code,
                hardware_id = HardwareId,
                hardware_id_source = HardwareIdSource,
            });
            using var content = new StringContent(body, Encoding.UTF8, "application/json");
            using var resp = await Http.PostAsync(ClaimUrl, content, ct);
            var text = await resp.Content.ReadAsStringAsync(ct);

            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(text) ? "{}" : text);
            var root = doc.RootElement;

            if (!resp.IsSuccessStatusCode)
            {
                var err = root.TryGetProperty("error", out var e) ? e.GetString() : null;
                return new ClaimResult(false, err ?? $"Pairing failed (HTTP {(int)resp.StatusCode}).", null);
            }

            var vesselId = Str(root, "vessel_uuid");
            var user = Str(root, "mqtt_username");
            if (string.IsNullOrWhiteSpace(vesselId) || string.IsNullOrWhiteSpace(user))
                return new ClaimResult(false, "Invalid response from cloud (missing vessel or credentials).", null);

            var creds = new VomsCredentials
            {
                VesselId = vesselId,
                MqttHost = Str(root, "broker_url") is { Length: > 0 } h ? h : "159.203.128.160",
                MqttPort = root.TryGetProperty("broker_port", out var bp) && bp.TryGetInt32(out var pn) ? pn : 1883,
                MqttClientId = "onboard_" + vesselId[..Math.Min(8, vesselId.Length)],
                MqttUser = user,
                MqttPass = Str(root, "mqtt_password"),
                // §2.5/§4.1: persist the broadly-supported legacy topic prefix.
                TopicPrefix = "vessel/" + vesselId,
            };
            Save(creds);
            return new ClaimResult(true, null, creds);
        }
        catch (Exception ex)
        {
            return new ClaimResult(false, "Could not reach the Alive cloud: " + ex.Message, null);
        }
    }

    public static void Unpair()
    {
        try { if (File.Exists(ConfigFile)) File.Delete(ConfigFile); } catch { }
    }

    private static void Save(VomsCredentials c)
    {
        Directory.CreateDirectory(ConfigDir);
        var sb = new StringBuilder();
        sb.AppendLine("# VOMS MQTT credentials - auto-generated by BoatDashboard");
        sb.AppendLine($"VOMS_VESSEL_ID={c.VesselId}");
        sb.AppendLine($"VOMS_MQTT_HOST={c.MqttHost}");
        sb.AppendLine($"VOMS_MQTT_PORT={c.MqttPort}");
        sb.AppendLine($"VOMS_MQTT_CLIENT_ID={c.MqttClientId}");
        sb.AppendLine($"VOMS_MQTT_USER={c.MqttUser}");
        sb.AppendLine($"VOMS_MQTT_PASS={c.MqttPass}");
        sb.AppendLine($"VOMS_TOPIC_PREFIX={c.TopicPrefix}");
        File.WriteAllText(ConfigFile, sb.ToString());
    }

    private static string Str(JsonElement el, string name) =>
        el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
}
