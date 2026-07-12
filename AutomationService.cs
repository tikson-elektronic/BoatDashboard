using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace BoatDashboard;

/// <summary>A structured action an automation performs (kept structured so it runs without re-invoking Claude).</summary>
public sealed class AutoAction
{
    public string Type { get; set; } = "";     // light | all_lights | av | notify
    public long Code { get; set; }              // light: iTach code
    public string? Target { get; set; }         // av: device id/name
    public string? Value { get; set; }          // all_lights: on/off ; av: action ; notify: text ; av volume: number
}

/// <summary>One automation rule.</summary>
public sealed class Automation
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];
    public string Name { get; set; } = "";
    public string TriggerType { get; set; } = "time";   // time | daylight | sensor
    public string TriggerParam { get; set; } = "";      // time:"22:00" ; daylight:"dark"|"light" ; sensor:"battery-service < 22"
    public List<AutoAction> Actions { get; set; } = new();
    public bool Enabled { get; set; } = true;
    [JsonIgnore] public bool LastConditionMet { get; set; }
    public string? LastFiredUtc { get; set; }
}

/// <summary>
/// The vessel automation engine. Evaluates time/daylight/sensor triggers on a background loop and
/// runs their (structured) actions. Rules are created by Claude from natural language
/// ("turn on the flybridge lights if it's not daylight") and persisted to C:\voms\automations.json.
/// </summary>
public sealed class AutomationService
{
    private static readonly string Path_ = @"C:\voms\automations.json";
    private readonly object _lock = new();
    private List<Automation> _rules = new();
    private CancellationTokenSource? _cts;

    private readonly Func<AutoAction, Task> _run;   // executes a structured action
    private readonly Func<string, double?> _sensor; // current sensor value by name (or null)
    public double Lat { get; set; } = 43.55;        // vessel position for daylight (default Antibes)
    public double Lon { get; set; } = 7.02;

    public AutomationService(Func<AutoAction, Task> run, Func<string, double?> sensor)
    {
        _run = run;
        _sensor = sensor;
        Load();
    }

    public void Start()
    {
        if (_cts is not null) return;
        _cts = new CancellationTokenSource();
        _ = LoopAsync(_cts.Token);
    }

    public void Stop() { try { _cts?.Cancel(); } catch { } _cts = null; }

    private async Task LoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { await EvaluateAsync(); }
            catch (Exception ex) { Ip2slClient.Log("[auto] eval error: " + ex.Message); }
            try { await Task.Delay(TimeSpan.FromSeconds(20), ct); } catch { break; }
        }
    }

    private async Task EvaluateAsync()
    {
        List<Automation> snapshot;
        lock (_lock) snapshot = _rules.ToList();

        foreach (var r in snapshot)
        {
            if (!r.Enabled) continue;
            bool met = Condition(r);
            // Edge-trigger: fire when the condition transitions from not-met to met.
            if (met && !r.LastConditionMet)
            {
                Ip2slClient.Log($"[auto] firing '{r.Name}' ({r.TriggerType} {r.TriggerParam})");
                foreach (var a in r.Actions)
                    try { await _run(a); } catch (Exception ex) { Ip2slClient.Log("[auto] action error: " + ex.Message); }
                r.LastFiredUtc = DateTime.UtcNow.ToString("o");
                Save();
            }
            r.LastConditionMet = met;
        }
    }

    private bool Condition(Automation r)
    {
        switch (r.TriggerType)
        {
            case "time":
                if (TimeSpan.TryParse(r.TriggerParam, out var at))
                {
                    var now = DateTime.Now.TimeOfDay;
                    return now.Hours == at.Hours && now.Minutes == at.Minutes;   // fires in the matching minute
                }
                return false;

            case "daylight":
                bool day = IsDaylight();
                return r.TriggerParam.StartsWith("dark", StringComparison.OrdinalIgnoreCase) ? !day : day;

            case "sensor":
                var parts = r.TriggerParam.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length == 3 && _sensor(parts[0]) is double v && double.TryParse(parts[2], out var thr))
                    return parts[1] switch
                    {
                        "<" => v < thr, "<=" => v <= thr, ">" => v > thr, ">=" => v >= thr,
                        "==" or "=" => Math.Abs(v - thr) < 0.01, _ => false,
                    };
                return false;

            default: return false;
        }
    }

    public bool IsDaylight() => SolarAltitudeDeg(DateTime.UtcNow, Lat, Lon) > 0;

    /// <summary>NOAA solar-position approximation → sun altitude in degrees (&gt;0 = above horizon).</summary>
    public static double SolarAltitudeDeg(DateTime utc, double lat, double lon)
    {
        const double rad = Math.PI / 180.0;
        double frac = 2 * Math.PI / 365.0 * (utc.DayOfYear - 1 + (utc.Hour - 12) / 24.0);
        double eqtime = 229.18 * (0.000075 + 0.001868 * Math.Cos(frac) - 0.032077 * Math.Sin(frac)
                        - 0.014615 * Math.Cos(2 * frac) - 0.040849 * Math.Sin(2 * frac));
        double decl = 0.006918 - 0.399912 * Math.Cos(frac) + 0.070257 * Math.Sin(frac)
                      - 0.006758 * Math.Cos(2 * frac) + 0.000907 * Math.Sin(2 * frac)
                      - 0.002697 * Math.Cos(3 * frac) + 0.00148 * Math.Sin(3 * frac);
        double tst = utc.Hour * 60 + utc.Minute + utc.Second / 60.0 + eqtime + 4 * lon;
        double ha = tst / 4.0 - 180.0;
        double zenith = Math.Acos(Math.Sin(lat * rad) * Math.Sin(decl)
                        + Math.Cos(lat * rad) * Math.Cos(decl) * Math.Cos(ha * rad));
        return 90 - zenith / rad;
    }

    // ---- CRUD (used by Claude tools) ----
    public Automation Add(Automation r) { lock (_lock) { _rules.Add(r); Save(); } return r; }
    public bool Delete(string id) { lock (_lock) { var n = _rules.RemoveAll(x => x.Id == id); if (n > 0) Save(); return n > 0; } }
    public bool Enable(string id, bool on) { lock (_lock) { var r = _rules.FirstOrDefault(x => x.Id == id); if (r is null) return false; r.Enabled = on; Save(); return true; } }
    public string ListJson() { lock (_lock) return JsonSerializer.Serialize(_rules); }

    private void Load()
    {
        try { if (File.Exists(Path_)) _rules = JsonSerializer.Deserialize<List<Automation>>(File.ReadAllText(Path_)) ?? new(); }
        catch { _rules = new(); }
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(@"C:\voms");
            File.WriteAllText(Path_, JsonSerializer.Serialize(_rules, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }
}
