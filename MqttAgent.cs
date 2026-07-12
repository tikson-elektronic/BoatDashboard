using System.Text;
using System.Text.Json;
using MQTTnet;
using MQTTnet.Client;
using MQTTnet.Protocol;

namespace BoatDashboard;

/// <summary>
/// The onboard VOMS agent (ONBOARD_AGENT_SPEC §4–§7): once paired, connects to the cloud
/// broker with the saved credentials and continuously publishes the full iTach sensor set
/// (§3.3) every few seconds, a 60 s heartbeat with Last-Will (§5), a capability manifest
/// (§7), and routes cmd-topic messages to the iTach with an ACK (§6). Auto-reconnects.
/// </summary>
public sealed class MqttAgent : IAsyncDisposable
{
    private readonly VomsCredentials _creds;
    private readonly Ip2slClient _itach;
    private readonly string _hardwareId;
    private readonly CancellationTokenSource _cts = new();
    private IMqttClient? _client;

    private const string VesselName = "TENDERLAND";

    // §3.4 light command table (target key → 4-byte LE code).
    private static readonly Dictionary<string, uint> Lights = new(StringComparer.OrdinalIgnoreCase)
    {
        ["all_on"] = 0x0600, ["all_off"] = 0x0700,
        ["interior_courtesy"] = 0x0009, ["courtesy"] = 0x0009,
        ["port_fwd_cabin"] = 0x0100, ["port_fwd_gangway"] = 0x0200, ["port_mid_cabin"] = 0x0300,
        ["galley"] = 0x0500, ["salon"] = 0x0800,
        ["stbd_fwd_cabin"] = 0x0900, ["stbd_aft_cabin"] = 0x0D00,
    };

    public bool Connected => _client?.IsConnected ?? false;
    public event Action<bool>? ConnectionChanged;

    public MqttAgent(VomsCredentials creds, Ip2slClient itach, string hardwareId)
    {
        _creds = creds;
        _itach = itach;
        _hardwareId = hardwareId;
    }

    public void Start() => _ = RunAsync();

    private static long NowMs() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    private string Base => $"vessel/{_creds.VesselId}";

    private async Task RunAsync()
    {
        var factory = new MqttFactory();
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                _client = factory.CreateMqttClient();
                _client.ApplicationMessageReceivedAsync += OnMessageAsync;

                var willPayload = Encoding.UTF8.GetBytes(
                    JsonSerializer.Serialize(new { online = false, status = "offline" }));
                var opts = new MqttClientOptionsBuilder()
                    .WithTcpServer(_creds.MqttHost, _creds.MqttPort)
                    .WithClientId(string.IsNullOrWhiteSpace(_creds.MqttClientId) ? "onboard_" + _creds.VesselId[..8] : _creds.MqttClientId)
                    .WithCredentials(_creds.MqttUser, _creds.MqttPass)
                    .WithCleanSession()
                    .WithWillTopic($"{Base}/status")
                    .WithWillPayload(willPayload)
                    .WithWillQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce)
                    .WithWillRetain(false)
                    .Build();

                await _client.ConnectAsync(opts, _cts.Token);
                ConnectionChanged?.Invoke(true);
                Ip2slClient.Log("[onboard] MQTT connected to " + _creds.MqttHost);

                await _client.SubscribeAsync(new MqttClientSubscribeOptionsBuilder()
                    .WithTopicFilter(f => f.WithTopic("vessel/+/cmd/+").WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce))
                    .Build(), _cts.Token);

                await PublishManifestAsync();

                var hb = HeartbeatLoopAsync(_cts.Token);
                var tele = TelemetryLoopAsync(_cts.Token);
                var man = ManifestLoopAsync(_cts.Token);

                // Wait until disconnected, then fall through to reconnect.
                while (!_cts.IsCancellationRequested && _client.IsConnected)
                    await Task.Delay(1000, _cts.Token);

                ConnectionChanged?.Invoke(false);
                await Task.WhenAny(hb, tele, man);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { Ip2slClient.Log("[onboard] MQTT error: " + ex.Message); }

            ConnectionChanged?.Invoke(false);
            try { _client?.Dispose(); } catch { }
            _client = null;
            try { await Task.Delay(5000, _cts.Token); } catch { break; }
        }
    }

    private async Task PublishAsync(string topic, string payload, bool retain)
    {
        if (_client is not { IsConnected: true }) return;
        try
        {
            await _client.PublishAsync(new MqttApplicationMessageBuilder()
                .WithTopic(topic).WithPayload(payload)
                .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce)
                .WithRetainFlag(retain).Build(), _cts.Token);
        }
        catch { }
    }

    private async Task HeartbeatLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && Connected)
        {
            var payload = JsonSerializer.Serialize(new
            {
                online = true,
                vessel = VesselName,
                hardware_id = _hardwareId,
                timestamp = NowMs(),
            });
            await PublishAsync($"{Base}/status", payload, retain: false);
            try { await Task.Delay(TimeSpan.FromSeconds(60), ct); } catch { break; }
        }
    }

    private async Task TelemetryLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && Connected)
        {
            foreach (var (cat, name, val, unit) in ReadSensors())
            {
                var payload = JsonSerializer.Serialize(new { value = val, unit, timestamp = NowMs() });
                await PublishAsync($"{Base}/{cat}/{name}", payload, retain: true);
            }
            try { await Task.Delay(TimeSpan.FromSeconds(4), ct); } catch { break; }
        }
    }

    private async Task ManifestLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && Connected)
        {
            try { await Task.Delay(TimeSpan.FromHours(1), ct); } catch { break; }
            await PublishManifestAsync();
        }
    }

    private Task PublishManifestAsync()
    {
        var manifest = JsonSerializer.Serialize(new
        {
            version = "2.0",
            vessel_id = _creds.VesselId,
            vessel = VesselName,
            source = "itach_ip2sl",
            capabilities = new { sensors = true, lighting_control = true, alarms = false, rules = false, fcm = false, jobs = false },
            sensor_categories = new[] { "tanks", "electrical" },
            light_targets = new[] { "all_on", "all_off", "interior_courtesy", "port_fwd_cabin", "port_fwd_gangway", "port_mid_cabin", "galley", "salon", "stbd_fwd_cabin", "stbd_aft_cabin" },
            published_at = DateTime.UtcNow.ToString("o"),
        });
        return PublishAsync($"{Base}/manifest", manifest, retain: true);
    }

    /// <summary>The §3.3 sensor set, skipping channels not yet seen from the iTach.</summary>
    private IEnumerable<(string cat, string name, double val, string unit)> ReadSensors()
    {
        int? F(string ch, int i) => _itach.Field(ch, i);

        // Tanks (percent, direct)
        if (F("00", 10) is int wp) yield return ("tanks", "fresh-water-port", wp, "%");
        if (F("00", 11) is int ws) yield return ("tanks", "fresh-water-stbd", ws, "%");
        if (F("03", 2) is int ffp) yield return ("tanks", "fuel-fwd-port", ffp, "%");
        if (F("03", 3) is int ffs) yield return ("tanks", "fuel-fwd-stbd", ffs, "%");
        if (F("03", 10) is int fap) yield return ("tanks", "fuel-aft-port", fap, "%");
        if (F("03", 11) is int fas) yield return ("tanks", "fuel-aft-stbd", fas, "%");

        // Batteries (÷10 → V)
        if (F("00", 2) is int bg) yield return ("electrical", "battery-genset", bg / 10.0, "V");
        if (F("00", 4) is int bpe) yield return ("electrical", "battery-port-engine", bpe / 10.0, "V");
        if (F("00", 6) is int bse) yield return ("electrical", "battery-stbd-engine", bse / 10.0, "V");
        if (F("00", 8) is int bsv) yield return ("electrical", "battery-service", bsv / 10.0, "V");

        // AC — channel 02: Shore 1 / Shore 2 / Generator (V, A, Hz÷10)
        if (F("02", 0) is int s1v) yield return ("electrical", "shore-1-volts", s1v, "V");
        if (F("02", 1) is int s1a) yield return ("electrical", "shore-1-amps", s1a, "A");
        if (F("02", 2) is int s1h) yield return ("electrical", "shore-1-hz", s1h / 10.0, "Hz");
        if (F("02", 3) is int s2v) yield return ("electrical", "shore-2-volts", s2v, "V");
        if (F("02", 4) is int s2a) yield return ("electrical", "shore-2-amps", s2a, "A");
        if (F("02", 5) is int s2h) yield return ("electrical", "shore-2-hz", s2h / 10.0, "Hz");
        if (F("02", 6) is int gv) yield return ("electrical", "generator-volts", gv, "V");
        if (F("02", 7) is int ga) yield return ("electrical", "generator-amps", ga, "A");
        if (F("02", 8) is int gh) yield return ("electrical", "generator-hz", gh / 10.0, "Hz");
    }

    private async Task OnMessageAsync(MqttApplicationMessageReceivedEventArgs e)
    {
        try
        {
            var parts = e.ApplicationMessage.Topic.Split('/');
            // vessel/<id>/cmd/<subsystem>
            if (parts.Length < 4 || parts[0] != "vessel" || parts[2] != "cmd") return;
            if (!string.Equals(parts[1], _creds.VesselId, StringComparison.OrdinalIgnoreCase)) return;
            var subsystem = parts[3];

            var payload = e.ApplicationMessage.ConvertPayloadToString() ?? "";
            var target = ParseTarget(payload);
            var norm = new string(target.ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : '_').ToArray());

            bool ok = false; string? err = null;
            if (subsystem.Equals("lights", StringComparison.OrdinalIgnoreCase) && Lights.TryGetValue(norm, out var code))
            {
                await _itach.SendCommandAsync(code);
                ok = true;
            }
            else err = Lights.ContainsKey(norm) ? "unsupported subsystem" : "unknown target";

            var ack = JsonSerializer.Serialize(ok
                ? new { status = "ok", target = norm, timestamp = NowMs() }
                : (object)new { status = "error", error = err, target = norm, timestamp = NowMs() });
            await PublishAsync($"{Base}/cmd/{subsystem}/ack", ack, retain: false);
        }
        catch (Exception ex) { Ip2slClient.Log("[onboard] cmd error: " + ex.Message); }
    }

    private static string ParseTarget(string payload)
    {
        payload = payload.Trim();
        if (payload.StartsWith('{'))
        {
            try
            {
                using var doc = JsonDocument.Parse(payload);
                var r = doc.RootElement;
                if (r.TryGetProperty("target", out var t) && t.ValueKind == JsonValueKind.String) return t.GetString() ?? "";
                if (r.TryGetProperty("command", out var c) && c.ValueKind == JsonValueKind.String) return c.GetString() ?? "";
            }
            catch { }
            return "";
        }
        return payload.Trim('"');
    }

    public ValueTask DisposeAsync()
    {
        _cts.Cancel();
        try { _client?.Dispose(); } catch { }
        _cts.Dispose();
        return ValueTask.CompletedTask;
    }
}
