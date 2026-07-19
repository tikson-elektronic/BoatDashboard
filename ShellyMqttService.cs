using System.Collections.Concurrent;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using MQTTnet;
using MQTTnet.Server;

namespace BoatDashboard;

/// <summary>
/// Hosts an embedded MQTT broker (the dashboard IS the broker — no external Mosquitto) that the Shelly
/// relay devices connect to, and drives up/down motors (TV lift, shades) over it.
///
/// Shelly Gen2+ MQTT: status on <c>{topic}/status/switch:{id}</c>, liveness on <c>{topic}/online</c>
/// (last-will), commands on <c>{topic}/command/switch:{id}</c> (payload on/off). We intercept the first two
/// to know each motor's state, and inject the third to control it.
///
/// SELF-HEALING: the broker runs for the app's lifetime; if it ever faults it is restarted. MQTT keep-alive +
/// the last-will topic flip a device to offline the instant it drops and back to online when it reconnects —
/// no polling. A motor is never left latched: every move auto-stops after its travel time.
/// </summary>
public sealed class ShellyMqttService : IDisposable
{
    private readonly CancellationTokenSource _cts = new();
    private MqttServer? _server;
    private int _port = 1883;
    private string _user = "boat", _pass = "";

    private readonly object _lock = new();
    private List<ShellyMotor> _motors = new();
    // live state per motor key: is the device connected, and are its two outputs on
    private readonly ConcurrentDictionary<string, bool> _online = new();       // topic -> online
    private readonly ConcurrentDictionary<string, bool> _outState = new();      // "topic/id" -> on
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _autoStop = new();  // key -> long travel-time backstop
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _holdWatch = new(); // key -> short dead-man watchdog (hold mode)
    private readonly ConcurrentDictionary<string, string> _driving = new();                    // key -> "up"/"down"/"" (avoid MQTT spam on heartbeats)
    private const int HoldWatchdogMs = 1500;  // hold mode: if no heartbeat within this, stop (covers lost release / dead tablet).
                                              // Normal release stops instantly via the explicit 'stop' the UI sends; this is only the
                                              // backstop for a LOST release, so a wider window absorbs WiFi/tablet heartbeat jitter that
                                              // was otherwise cutting the motor mid-hold (the "lift only runs 2s" bug). Firmware auto_off (10s) caps it.

    // Offline-alarm tracking: a shade Shelly that drops off the broker for too long raises an alarm.
    private readonly ConcurrentDictionary<string, DateTime> _lastOnline = new();  // topic -> last time seen online (UTC)
    private readonly ConcurrentDictionary<string, bool> _alarmed = new();          // topic -> currently in offline-alarm
    private DateTime _startedUtc = DateTime.UtcNow;
    /// <summary>Seconds a shade controller may be offline before it alarms.</summary>
    public int OfflineAlarmSec { get; set; } = 60;
    /// <summary>Raised on the edge: (message, isAlarm) — isAlarm=true when it goes offline-too-long, false on recovery.</summary>
    public event Action<string, bool>? OnShadeAlarm;

    private void SetOnline(string topic, bool on)
    {
        _online[topic] = on;
        if (on) _lastOnline[topic] = DateTime.UtcNow;
        Changed?.Invoke();
    }

    // Time-based position tracking (0.0 = fully up/open, 1.0 = fully down/closed). Lets a mid-travel reverse run
    // only as far as it actually travelled — e.g. down 3s then up runs ~3s back, not a fresh full cycle.
    private readonly ConcurrentDictionary<string, double> _pos = new();          // key -> last-known position 0..1
    private readonly ConcurrentDictionary<string, DateTime> _moveStartT = new(); // key -> when the current move started (UTC)
    private readonly ConcurrentDictionary<string, double> _moveStartPos = new(); // key -> position when the current move started

    /// <summary>Live position estimate: last-known position adjusted by how long the current move has actually
    /// been travelling — the first <paramref name="delay"/> seconds after relay-on are dead-time (motor spins
    /// up before the shade moves), so position only advances after it.</summary>
    // Persist last-known positions across restarts so the dashboard boots knowing roughly where each shade was.
    private static readonly string PosFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "BoatDashboard", "shade-positions.json");
    private readonly object _posFileLock = new();

    private void SavePositions()
    {
        try
        {
            lock (_posFileLock)
            {
                var dir = Path.GetDirectoryName(PosFile);
                if (dir is not null) Directory.CreateDirectory(dir);
                File.WriteAllText(PosFile, JsonSerializer.Serialize(_pos));
            }
        }
        catch { }
    }

    private void LoadPositions()
    {
        try
        {
            if (!File.Exists(PosFile)) return;
            var d = JsonSerializer.Deserialize<Dictionary<string, double>>(File.ReadAllText(PosFile));
            if (d is not null) foreach (var kv in d) _pos[kv.Key] = Math.Clamp(kv.Value, 0.0, 1.0);
        }
        catch { }
    }

    private double CurrentPos(string key, double travelSec, double delay)
    {
        double basePos = _pos.TryGetValue(key, out var p) ? p : 0.0;
        if (_driving.TryGetValue(key, out var dir) && !string.IsNullOrEmpty(dir)
            && _moveStartT.TryGetValue(key, out var st) && _moveStartPos.TryGetValue(key, out var sp) && travelSec > 0)
        {
            double moveTime = Math.Max(0.1, travelSec - delay);
            double moved = Math.Max(0, (DateTime.UtcNow - st).TotalSeconds - delay);
            double frac = moved / moveTime;
            double pos = dir == "down" ? sp + frac : sp - frac;
            return Math.Clamp(pos, 0.0, 1.0);
        }
        return basePos;
    }

    public event Action? Changed;

    public void Start(int port, string user, string pass)
    {
        _port = port <= 0 ? 1883 : port; _user = user ?? ""; _pass = pass ?? "";
        _startedUtc = DateTime.UtcNow;
        LoadPositions();   // restore last-known shade positions from the previous session
        _ = RunAsync();
        _ = MonitorLoopAsync();   // watch for shade controllers dropping off the broker
        _ = WarmLoopAsync();      // keep each device's HTTP connection warm so the FIRST tap isn't slow
    }

    public void SetConfig(IEnumerable<ShellyMotor> motors)
    {
        lock (_lock) _motors = motors?.ToList() ?? new();
    }

    public string ConfigJson() { lock (_lock) return JsonSerializer.Serialize(_motors); }

    /// <summary>Change a motor's travel timer (the dashboard auto-stop) and, if we know the device IP, push a
    /// matching firmware auto-off backstop to both outputs. Returns the updated motor list for persistence.</summary>
    public List<ShellyMotor> SetTimer(string key, int secs)
    {
        secs = Math.Clamp(secs, 1, 120);
        List<ShellyMotor> matches;
        lock (_lock) { matches = _motors.Where(x => x.Key == key).ToList(); foreach (var m in matches) m.TravelSec = secs; }
        foreach (var m in matches) if (!string.IsNullOrEmpty(m.Ip)) _ = PushAutoOffAsync(m.Ip, m.UpOut, m.DownOut, secs);
        List<ShellyMotor> copy; lock (_lock) copy = _motors.ToList();
        return copy;
    }

    /// <summary>Push the firmware auto-off backstop (seconds) to a Shelly's two outputs over HTTP RPC.</summary>
    public async Task PushAutoOffAsync(string ip, int up, int down, int secs)
    {
        foreach (var id in new[] { up, down })
        {
            try
            {
                var body = JsonSerializer.Serialize(new { id = 1, method = "Switch.SetConfig", @params = new { id, config = new { auto_off = true, auto_off_delay = (double)secs } } });
                await _http.PostAsync($"http://{ip}/rpc", new StringContent(body, Encoding.UTF8, "application/json"));
            }
            catch { }
        }
    }

    private static readonly HttpClient _http = new() { Timeout = TimeSpan.FromMilliseconds(1300) };

    /// <summary>Scan the local /24 subnet(s) for Shelly devices (GET /shelly returns id/model/gen).
    /// Returns JSON [{ip,id,model,gen,name,assignedTo}] so the config page can list what's online.</summary>
    public async Task<string> DiscoverJsonAsync()
    {
        var found = new ConcurrentBag<Dictionary<string, object?>>();
        var sem = new SemaphoreSlim(48);
        var tasks = new List<Task>();
        foreach (var base24 in LocalSubnet24s())
            for (int i = 1; i <= 254; i++)
            {
                var ip = base24 + i;
                tasks.Add(Task.Run(async () =>
                {
                    await sem.WaitAsync();
                    try
                    {
                        var r = await _http.GetStringAsync($"http://{ip}/shelly");
                        using var d = JsonDocument.Parse(r);
                        var root = d.RootElement;
                        if (root.TryGetProperty("id", out var id) && (id.GetString() ?? "").StartsWith("shelly", StringComparison.OrdinalIgnoreCase))
                        {
                            string idv = id.GetString() ?? "";
                            string? assigned; lock (_lock) assigned = _motors.FirstOrDefault(m => m.Topic == idv)?.Name;
                            found.Add(new()
                            {
                                ["ip"] = ip,
                                ["id"] = idv,
                                ["model"] = root.TryGetProperty("model", out var mo) ? mo.GetString() : null,
                                ["gen"] = root.TryGetProperty("gen", out var g) ? g.GetInt32() : 0,
                                ["name"] = root.TryGetProperty("name", out var nm) && nm.ValueKind == JsonValueKind.String ? nm.GetString() : idv,
                                ["assignedTo"] = assigned,
                            });
                        }
                    }
                    catch { }
                    finally { sem.Release(); }
                }));
            }
        await Task.WhenAll(tasks);
        return JsonSerializer.Serialize(found.OrderBy(f => (string?)f["id"]));
    }

    /// <summary>Push MQTT config to a Shelly over its HTTP RPC so it connects to THIS dashboard's broker with
    /// the given topic prefix — then reboot it to apply. No touching the Shelly app.</summary>
    public async Task<bool> ProvisionAsync(string ip, string topic)
    {
        var host = BrokerHostFor(ip);
        if (host is null) return false;
        var cfg = new { config = new { enable = true, server = $"{host}:{_port}", user = string.IsNullOrEmpty(_pass) ? (string?)null : _user, pass = string.IsNullOrEmpty(_pass) ? (string?)null : _pass, topic_prefix = topic, rpc_ntf = true, status_ntf = true } };
        try
        {
            var body = JsonSerializer.Serialize(new { id = 1, method = "MQTT.SetConfig", @params = cfg });
            var resp = await _http.PostAsync($"http://{ip}/rpc", new StringContent(body, Encoding.UTF8, "application/json"));
            if (!resp.IsSuccessStatusCode) return false;
            // reboot so the new MQTT config takes effect and the device connects to our broker
            try { await _http.PostAsync($"http://{ip}/rpc", new StringContent(JsonSerializer.Serialize(new { id = 2, method = "Shelly.Reboot" }), Encoding.UTF8, "application/json")); } catch { }
            return true;
        }
        catch { return false; }
    }

    private static IEnumerable<string> LocalSubnet24s()
    {
        var seen = new HashSet<string>();
        foreach (var ni in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
        {
            if (ni.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up) continue;
            if (ni.NetworkInterfaceType == System.Net.NetworkInformation.NetworkInterfaceType.Loopback) continue;
            foreach (var ua in ni.GetIPProperties().UnicastAddresses)
            {
                if (ua.Address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork) continue;
                var b = ua.Address.GetAddressBytes();
                var pre = $"{b[0]}.{b[1]}.{b[2]}.";
                if (seen.Add(pre)) yield return pre;
            }
        }
    }

    /// <summary>The PC IP the Shelly should reach the broker on: our address on the same /24 as the device.</summary>
    private static string? BrokerHostFor(string ip)
    {
        var parts = ip.Split('.'); if (parts.Length != 4) return null;
        var want = $"{parts[0]}.{parts[1]}.{parts[2]}.";
        foreach (var ni in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
        {
            if (ni.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up) continue;
            foreach (var ua in ni.GetIPProperties().UnicastAddresses)
                if (ua.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork
                    && ua.Address.ToString().StartsWith(want, StringComparison.Ordinal))
                    return ua.Address.ToString();
        }
        return null;
    }

    private async Task RunAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                var options = new MqttServerOptionsBuilder()
                    .WithDefaultEndpoint()
                    .WithDefaultEndpointPort(_port)
                    .Build();
                var server = new MqttFactory().CreateMqttServer(options);

                server.ValidatingConnectionAsync += e =>
                {
                    // If broker credentials are configured, require them (secure); else allow anonymous.
                    if (!string.IsNullOrEmpty(_pass) && (e.UserName != _user || e.Password != _pass))
                        e.ReasonCode = MQTTnet.Protocol.MqttConnectReasonCode.BadUserNameOrPassword;
                    return Task.CompletedTask;
                };
                server.InterceptingPublishAsync += OnPublish;
                server.ClientConnectedAsync += e => { Ip2slClient.Log($"[shelly] client connected: {e.ClientId}"); SetOnline(e.ClientId ?? "", true); return Task.CompletedTask; };
                server.ClientDisconnectedAsync += e => { Ip2slClient.Log($"[shelly] client disconnected: {e.ClientId}"); SetOnline(e.ClientId ?? "", false); return Task.CompletedTask; };

                await server.StartAsync();
                _server = server;
                Ip2slClient.Log($"[shelly] embedded MQTT broker up on :{_port}");

                // Block until shutdown; if the server object dies we loop and rebuild it.
                while (!_cts.IsCancellationRequested && server.IsStarted)
                    await Task.Delay(1000, _cts.Token);

                try { await server.StopAsync(); } catch { }
            }
            catch (OperationCanceledException) when (_cts.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                Ip2slClient.Log("[shelly] broker error, restarting in 3s: " + ex.Message);
                try { await Task.Delay(3000, _cts.Token); } catch { break; }
            }
        }
    }

    /// <summary>Every ~10s, flag any assigned shade controller that's been off the broker past the threshold.</summary>
    private async Task MonitorLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            try { EvaluateAlarms(); } catch { }
            try { await Task.Delay(10000, _cts.Token); } catch { break; }
        }
    }

    // These Shelly HTTP servers drop idle keep-alive connections, so the FIRST command after a pause pays a
    // slow reconnect (~2.5s) while later ones are ~0.2s. A light periodic GET per device keeps the pooled
    // connection warm so every tap is instant. One request per device every few seconds — very gentle.
    private async Task WarmLoopAsync()
    {
        try { await Task.Delay(4000, _cts.Token); } catch { return; }
        while (!_cts.IsCancellationRequested)
        {
            List<ShellyMotor> ms; lock (_lock) ms = _motors.ToList();
            foreach (var ip in ms.Where(x => !string.IsNullOrWhiteSpace(x.Ip)).Select(x => x.Ip).Distinct())
            {
                var u = ip;
                _ = Task.Run(async () => { try { await _http.GetAsync($"http://{u}/rpc/Sys.GetStatus", _cts.Token); } catch { } });
            }
            try { await Task.Delay(5000, _cts.Token); } catch { break; }
        }
    }

    private void EvaluateAlarms()
    {
        List<ShellyMotor> ms; lock (_lock) ms = _motors.ToList();
        var now = DateTime.UtcNow;
        foreach (var m in ms.Where(x => !string.IsNullOrWhiteSpace(x.Topic)).GroupBy(x => x.Topic).Select(g => g.First()))
        {
            bool online = _online.TryGetValue(m.Topic, out var o) && o;
            DateTime last = _lastOnline.TryGetValue(m.Topic, out var t) ? t : _startedUtc;
            bool alarm = !online && (now - last).TotalSeconds >= OfflineAlarmSec;
            bool prev = _alarmed.TryGetValue(m.Topic, out var pa) && pa;
            if (alarm && !prev)
            {
                _alarmed[m.Topic] = true;
                Ip2slClient.Log($"[shelly] ALARM {m.Name} ({m.Topic}) offline > {OfflineAlarmSec}s");
                OnShadeAlarm?.Invoke($"{m.Name} controller offline for over {OfflineAlarmSec} seconds", true);
            }
            else if (!alarm && prev)
            {
                _alarmed[m.Topic] = false;
                if (online) OnShadeAlarm?.Invoke($"{m.Name} controller back online", false);
            }
        }
    }

    private Task OnPublish(InterceptingPublishEventArgs e)
    {
        try
        {
            var topic = e.ApplicationMessage.Topic ?? "";
            var payload = e.ApplicationMessage.PayloadSegment.Count > 0
                ? Encoding.UTF8.GetString(e.ApplicationMessage.PayloadSegment) : "";

            // {prefix}/online  -> "true"/"false"
            if (topic.EndsWith("/online"))
            {
                var prefix = topic[..^"/online".Length];
                SetOnline(prefix, payload.Trim().Equals("true", StringComparison.OrdinalIgnoreCase));
            }
            // {prefix}/status/switch:{id}  -> JSON { "id":0, "output":true, ... }
            else if (topic.Contains("/status/switch:"))
            {
                var i = topic.IndexOf("/status/switch:", StringComparison.Ordinal);
                var prefix = topic[..i];
                var id = topic[(i + "/status/switch:".Length)..];
                bool on = false;
                try { using var d = JsonDocument.Parse(payload); if (d.RootElement.TryGetProperty("output", out var o)) on = o.GetBoolean(); } catch { }
                _outState[$"{prefix}/{id}"] = on;
                SetOnline(prefix, true);   // a status publish proves it's alive
            }
        }
        catch { }
        return Task.CompletedTask;
    }

    private async Task SetOutput(ShellyMotor m, int id, bool on)
    {
        // The embedded MQTT broker can take 2-4s to deliver a command to a device (esp. the first after idle),
        // which made the lift/shades feel "super slow". ONE fire-and-forget direct HTTP RPC lands in ~0.1s so
        // the relay responds instantly. Single request (no retry burst) keeps it gentle on the small Shellys;
        // the MQTT publish below stays for reliability + keeps status/position tracking flowing.
        if (!string.IsNullOrWhiteSpace(m.Ip))
        {
            string ip = m.Ip;
            var rpc = JsonSerializer.Serialize(new { id = 1, method = "Switch.Set", @params = new { id, on } });
            _ = Task.Run(async () => { try { await _http.PostAsync($"http://{ip}/rpc", new StringContent(rpc, Encoding.UTF8, "application/json")); } catch { } });
        }
        if (_server is null) return;
        var msg = new MqttApplicationMessageBuilder()
            .WithTopic($"{m.Topic}/command/switch:{id}")
            .WithPayload(on ? "on" : "off")
            .Build();
        try { await _server.InjectApplicationMessage(new InjectedMqttApplicationMessage(msg) { SenderClientId = "dashboard" }); } catch { }
    }

    /// <summary>Drive a motor. action = "up" | "down" | "stop".
    /// <para>Two safety nets so a motor is never left running: (1) a long travel-time backstop, and — when
    /// <paramref name="hold"/> is true (dead-man / press-and-hold) — (2) a short watchdog that stops the motor
    /// unless the browser keeps sending heartbeats. On button release the UI both sends "stop" immediately AND
    /// stops heartbeating, so the motor halts at once on the happy path and within ~700ms even if the release
    /// packet is lost or the tablet dies mid-press.</para>
    /// Repeated same-direction hold heartbeats just re-arm the watchdog — no MQTT re-publish, no relay chatter.</summary>
    public async Task CommandAsync(string key, string action, bool hold = false)
    {
        // A slot can map to SEVERAL Shellys (e.g. front shades = multiple panels) — drive them all together.
        List<ShellyMotor> ms; lock (_lock) ms = _motors.Where(x => x.Key == key && !string.IsNullOrWhiteSpace(x.Topic)).ToList();
        if (ms.Count == 0) return;
        int travel = ms.Max(m => Math.Clamp(m.TravelSec, 1, 120));
        double delay = ms.Max(m => Math.Clamp(m.StartDelaySec, 0.0, 5.0));   // startup dead-time before it moves
        double moveTime = Math.Max(0.1, travel - delay);

        async Task AllOff() { foreach (var m in ms) { await SetOutput(m, m.UpOut, false); await SetOutput(m, m.DownOut, false); } }
        // Freeze the live position estimate into _pos (call before changing/stopping a move) + persist it.
        void FreezePos() { _pos[key] = CurrentPos(key, travel, delay); _moveStartT.TryRemove(key, out _); SavePositions(); }

        if (action == "stop")
        {
            FreezePos();
            if (_autoStop.TryRemove(key, out var a)) { try { a.Cancel(); } catch { } }
            if (_holdWatch.TryRemove(key, out var w)) { try { w.Cancel(); } catch { } }
            _driving[key] = "";
            await AllOff();
            return;
        }

        bool up = action == "up";
        bool alreadyDriving = _driving.TryGetValue(key, out var cur) && cur == action;

        if (!alreadyDriving)
        {
            // capture position from any in-progress (possibly opposite) move, then cancel its backstop
            FreezePos();
            if (_autoStop.TryRemove(key, out var old)) { try { old.Cancel(); } catch { } }

            // unknown position bootstraps to the FAR limit so the first move runs full travel and calibrates
            double pos = _pos.TryGetValue(key, out var pp) ? pp : (up ? 1.0 : 0.0);
            double distance = up ? pos : (1.0 - pos);                       // fraction still to travel this way
            // time to reach the limit = the startup dead-time + the actual movement time for that distance
            double remaining = Math.Clamp(delay + distance * moveTime, 0.0, travel);
            // A HOLD (dead-man) press must ALWAYS energize the requested direction. These are switch-mode motors
            // with no encoder, so the tracked position is approximate and can read "at the limit" when it isn't
            // (e.g. pos=0 right after commissioning). The distance guard therefore only applies to AUTO taps;
            // for a hold the safety is the dead-man watchdog + physical limit switch + travel timer.
            if (!hold && distance < 0.02)   // AUTO tap already at that end — don't run into the stop
            {
                _driving[key] = "";
                await AllOff();
                return;
            }
            else
            {
                // Opposite output off, then target on (never both on at once). No delay between them:
                // a delay opens a re-entrancy window where rapid hold-heartbeats race and corrupt the state.
                foreach (var m in ms)
                {
                    await SetOutput(m, up ? m.DownOut : m.UpOut, false);
                    await SetOutput(m, up ? m.UpOut : m.DownOut, true);
                }
                _driving[key] = action;
                _moveStartT[key] = DateTime.UtcNow;
                _moveStartPos[key] = pos;

                // auto-stop backstop. AUTO: position-aware (stop when it should reach the limit). HOLD: full
                // travel time — the dead-man watchdog does the real stopping; this only backstops a stuck ping
                // stream, and must not cut a hold early when the tracked position is stale.
                var cts = new CancellationTokenSource();
                _autoStop[key] = cts;
                var runFor = hold ? travel : remaining;
                _ = Task.Run(async () =>
                {
                    try { await Task.Delay(TimeSpan.FromSeconds(runFor), cts.Token); }
                    catch { return; }
                    _pos[key] = up ? 0.0 : 1.0;   // reached the limit — position now known exactly
                    _moveStartT.TryRemove(key, out _);
                    _driving[key] = "";
                    await AllOff();
                    SavePositions();
                    _autoStop.TryRemove(key, out _);
                });
            }
        }

        if (hold)
        {
            // (re)arm the short dead-man watchdog; each heartbeat resets it
            if (_holdWatch.TryRemove(key, out var pw)) { try { pw.Cancel(); } catch { } }
            var wcts = new CancellationTokenSource();
            _holdWatch[key] = wcts;
            _ = Task.Run(async () =>
            {
                try { await Task.Delay(HoldWatchdogMs, wcts.Token); }
                catch { return; }
                // heartbeats stopped (finger up / tablet gone / network dropped) -> freeze position + halt
                _pos[key] = CurrentPos(key, travel, delay);
                _moveStartT.TryRemove(key, out _);
                if (_autoStop.TryRemove(key, out var ac)) { try { ac.Cancel(); } catch { } }
                _driving[key] = "";
                await AllOff();
                SavePositions();
                _holdWatch.TryRemove(key, out _);
            });
        }
    }

    /// <summary>Move a slot to a specific position (0.0 = open, 1.0 = closed) — this is how HALF / any %
    /// target works: it runs the motor only long enough to reach that fraction, then stops. Targets at the
    /// ends run into the limit (with a little overrun) so they self-calibrate.</summary>
    public async Task GotoAsync(string key, double target)
    {
        target = Math.Clamp(target, 0.0, 1.0);
        List<ShellyMotor> ms; lock (_lock) ms = _motors.Where(x => x.Key == key && !string.IsNullOrWhiteSpace(x.Topic)).ToList();
        if (ms.Count == 0) return;
        int travel = ms.Max(m => Math.Clamp(m.TravelSec, 1, 120));
        double delay = ms.Max(m => Math.Clamp(m.StartDelaySec, 0.0, 5.0));
        double moveTime = Math.Max(0.1, travel - delay);

        async Task AllOff() { foreach (var m in ms) { await SetOutput(m, m.UpOut, false); await SetOutput(m, m.DownOut, false); } }

        // freeze current position from any in-progress move, and cancel any pending timers
        _pos[key] = CurrentPos(key, travel, delay);
        _moveStartT.TryRemove(key, out _);
        if (_autoStop.TryRemove(key, out var old)) { try { old.Cancel(); } catch { } }
        if (_holdWatch.TryRemove(key, out var w)) { try { w.Cancel(); } catch { } }

        double cur = _pos.TryGetValue(key, out var pp) ? pp : 0.0;
        bool toLimit = target <= 0.02 || target >= 0.98;
        double distance = Math.Abs(target - cur);
        if (distance < 0.02 && !toLimit) { _driving[key] = ""; await AllOff(); return; }   // already there

        // OPEN (target≈0) and CLOSE (target≈1) drive the ABSOLUTE direction and run the FULL travel time, so
        // they always reach the limit and re-zero the position — even when the tracked estimate has drifted
        // (that drift is what made OPEN sometimes fire the close relay). Only HALF uses position-relative logic.
        bool up = toLimit ? (target <= 0.02) : (target < cur);
        double runFor = toLimit ? (travel + 0.6) : (delay + distance * moveTime);
        runFor = Math.Clamp(runFor, 0.05, travel + 1);

        foreach (var m in ms)
        {
            await SetOutput(m, up ? m.DownOut : m.UpOut, false);
            await SetOutput(m, up ? m.UpOut : m.DownOut, true);
        }
        _driving[key] = up ? "up" : "down";
        _moveStartT[key] = DateTime.UtcNow;
        _moveStartPos[key] = cur;
        SavePositions();

        double finalPos = toLimit ? (up ? 0.0 : 1.0) : target;
        var cts = new CancellationTokenSource();
        _autoStop[key] = cts;
        _ = Task.Run(async () =>
        {
            try { await Task.Delay(TimeSpan.FromSeconds(runFor), cts.Token); }
            catch { return; }
            _pos[key] = finalPos;
            _moveStartT.TryRemove(key, out _);
            _driving[key] = "";
            await AllOff();
            SavePositions();
            _autoStop.TryRemove(key, out _);
        });
    }

    /// <summary>Per-slot status for the UI (one row per slot, aggregated over all its Shellys):
    /// online if any device is up, moving if any device is driving.</summary>
    public string StatusJson()
    {
        List<ShellyMotor> ms; lock (_lock) ms = _motors.ToList();
        var arr = ms.GroupBy(m => m.Key).Select(g =>
        {
            int travel = g.Max(m => Math.Clamp(m.TravelSec, 1, 120));
            double delay = g.Max(m => Math.Clamp(m.StartDelaySec, 0.0, 5.0));
            var now = DateTime.UtcNow;
            return new
            {
                key = g.Key,
                online = g.Any(m => _online.TryGetValue(m.Topic, out var on) && on),
                // alarm if ANY device in the slot has been offline past the threshold (partial failure counts)
                alarm = g.Any(m => !(_online.TryGetValue(m.Topic, out var on) && on)
                                   && (now - (_lastOnline.TryGetValue(m.Topic, out var t) ? t : _startedUtc)).TotalSeconds >= OfflineAlarmSec),
                moving = g.Any(m => _outState.TryGetValue($"{m.Topic}/{m.UpOut}", out var u) && u) ? "up"
                       : g.Any(m => _outState.TryGetValue($"{m.Topic}/{m.DownOut}", out var d) && d) ? "down" : "",
                pos = (int)Math.Round(CurrentPos(g.Key, travel, delay) * 100),   // 0 = open, 100 = closed (time-tracked)
            };
        });
        return JsonSerializer.Serialize(arr);
    }

    public bool BrokerUp => _server?.IsStarted ?? false;

    public void Dispose()
    {
        // freeze any in-progress move so a close mid-travel still remembers roughly where the shade is
        try
        {
            List<ShellyMotor> ms; lock (_lock) ms = _motors.ToList();
            foreach (var g in ms.GroupBy(m => m.Key))
            {
                int travel = g.Max(m => Math.Clamp(m.TravelSec, 1, 120));
                double delay = g.Max(m => Math.Clamp(m.StartDelaySec, 0.0, 5.0));
                _pos[g.Key] = CurrentPos(g.Key, travel, delay);
            }
            SavePositions();
        }
        catch { }
        _cts.Cancel();
        try { _server?.StopAsync().Wait(1000); } catch { }
        _cts.Dispose();
    }
}
