using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media;

namespace BoatDashboard;

public abstract class Observable : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    protected void Raise([CallerMemberName] string? n = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? n = null)
    {
        if (Equals(field, value)) return false;
        field = value; Raise(n); return true;
    }
}

public static class Palette
{
    public static readonly Brush Good = Frozen("#35D08A");
    public static readonly Brush Warn = Frozen("#F5B14C");
    public static readonly Brush Bad  = Frozen("#EF5D6B");
    public static readonly Brush Muted= Frozen("#5A6A85");
    public static readonly Brush Accent= Frozen("#3DA9FC");
    public static Brush Frozen(string hex)
    {
        var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)!);
        b.Freeze(); return b;
    }
}

public sealed class TankVm : Observable
{
    public string Name { get; init; } = "";
    public string Kind { get; init; } = "";   // "WATER" / "FUEL"
    private double _pct;
    public double Percent { get => _pct; set { if (Set(ref _pct, value)) { Raise(nameof(Display)); Raise(nameof(Fill)); } } }
    public string Display => $"{_pct:0}%";
    public Brush Fill => _pct >= 50 ? Palette.Good : _pct >= 20 ? Palette.Warn : Palette.Bad;
}

public sealed class BatteryVm : Observable
{
    public string Name { get; init; } = "";
    private double _v;
    public double Volts { get => _v; set { if (Set(ref _v, value)) { Raise(nameof(Display)); Raise(nameof(Brush)); Raise(nameof(State)); } } }
    public string Display => $"{_v:0.0} V";
    public string State => _v <= 0.5 ? "OFF" : _v >= 12.4 ? "CHARGED" : "LOW";
    public Brush Brush => _v <= 0.5 ? Palette.Muted : _v >= 12.4 ? Palette.Good : _v >= 11.8 ? Palette.Warn : Palette.Bad;
}

public sealed class AcVm : Observable
{
    public string Name { get; init; } = "";
    private double _v, _a, _hz;
    public double Volts { get => _v; set { if (Set(ref _v, value)) { Raise(nameof(VoltsText)); Raise(nameof(Dot)); Raise(nameof(StateText)); } } }
    public double Amps  { get => _a; set { if (Set(ref _a, value)) Raise(nameof(AmpsText)); } }
    public double Hz    { get => _hz; set { if (Set(ref _hz, value)) { Raise(nameof(HzText)); Raise(nameof(Dot)); Raise(nameof(StateText)); } } }
    public bool On => _v > 1 || _hz > 1;
    public string VoltsText => $"{_v:0} V";
    public string AmpsText  => $"{_a:0} A";
    public string HzText    => $"{_hz:0.0} Hz";
    public string StateText => On ? "ONLINE" : "OFFLINE";
    public Brush Dot => On ? Palette.Good : Palette.Muted;
}

public sealed class LightVm : Observable
{
    public string Name { get; init; } = "";
    public uint Code { get; init; }
    private bool _on;
    public bool IsOn { get => _on; set { if (Set(ref _on, value)) { Raise(nameof(StateText)); Raise(nameof(Dot)); } } }
    public string StateText => _on ? "ON" : "OFF";
    public Brush Dot => _on ? Palette.Good : Palette.Muted;
}

public sealed class ChatMessage
{
    public string Text { get; init; } = "";
    public bool FromUser { get; init; }
    public string Align => FromUser ? "Right" : "Left";
    public Brush Bubble => FromUser ? Palette.Accent : Palette.Frozen("#243047");
}

public sealed class DashboardViewModel : Observable
{
    public ObservableCollection<ChatMessage> Chat { get; } = new();

    private string _hint = "not configured";
    public string AssistantHint { get => _hint; set => Set(ref _hint, value); }

    public ObservableCollection<AcVm> Ac { get; } = new()
    {
        new AcVm { Name = "Shore 1" },
        new AcVm { Name = "Shore 2" },
        new AcVm { Name = "Generator" },
    };

    public ObservableCollection<BatteryVm> Batteries { get; } = new()
    {
        new BatteryVm { Name = "Genset" },
        new BatteryVm { Name = "Port engine" },
        new BatteryVm { Name = "Stbd engine" },
        new BatteryVm { Name = "Service" },
    };

    public ObservableCollection<TankVm> Tanks { get; } = new()
    {
        new TankVm { Name = "Fresh water · port", Kind = "WATER" },
        new TankVm { Name = "Fresh water · stbd", Kind = "WATER" },
        new TankVm { Name = "Fuel · fwd port", Kind = "FUEL" },
        new TankVm { Name = "Fuel · fwd stbd", Kind = "FUEL" },
        new TankVm { Name = "Fuel · aft port", Kind = "FUEL" },
        new TankVm { Name = "Fuel · aft stbd", Kind = "FUEL" },
    };

    public ObservableCollection<LightVm> Lights { get; } = new()
    {
        new LightVm { Name = "Interior Courtesy", Code = 0x0009 },
        new LightVm { Name = "Port Fwd Cabin", Code = 0x0100 },
        new LightVm { Name = "Port Fwd Gangway", Code = 0x0200 },
        new LightVm { Name = "Port Mid Cabin", Code = 0x0300 },
        new LightVm { Name = "Galley", Code = 0x0500 },
        new LightVm { Name = "Salon", Code = 0x0800 },
        new LightVm { Name = "Stbd Fwd Cabin", Code = 0x0900 },
        new LightVm { Name = "Stbd Aft Cabin", Code = 0x0D00 },
    };

    private string _conn = "Connecting…";
    public string ConnectionText { get => _conn; set => Set(ref _conn, value); }

    private Brush _connBrush = Palette.Warn;
    public Brush ConnectionBrush { get => _connBrush; set => Set(ref _connBrush, value); }

    private string _updated = "—";
    public string LastUpdated { get => _updated; set => Set(ref _updated, value); }

    private string _mqtt = "MQTT off";
    public string MqttText { get => _mqtt; set => Set(ref _mqtt, value); }

    private Brush _mqttBrush = Palette.Muted;
    public Brush MqttBrush { get => _mqttBrush; set => Set(ref _mqttBrush, value); }

    /// <summary>Pull latest telemetry from the client into the bound view models.</summary>
    public void Refresh(Ip2slClient c, string nowText)
    {
        int F(string ch, int i, int fallback = 0) => c.Field(ch, i) ?? fallback;

        // AC monitor — channel 02, (V, A, Hz*10) triplets
        Set(Ac[0], F("02", 0), F("02", 1), F("02", 2));
        Set(Ac[1], F("02", 3), F("02", 4), F("02", 5));
        Set(Ac[2], F("02", 6), F("02", 7), F("02", 8));

        // Batteries — channel 00, value/10 = volts
        Batteries[0].Volts = F("00", 2) / 10.0;
        Batteries[1].Volts = F("00", 4) / 10.0;
        Batteries[2].Volts = F("00", 6) / 10.0;
        Batteries[3].Volts = F("00", 8) / 10.0;

        // Tanks — percent directly
        Tanks[0].Percent = F("00", 10);
        Tanks[1].Percent = F("00", 11);
        Tanks[2].Percent = F("03", 2);
        Tanks[3].Percent = F("03", 3);
        Tanks[4].Percent = F("03", 10);
        Tanks[5].Percent = F("03", 11);

        if (c.Connected) LastUpdated = nowText;
    }

    private static void Set(AcVm vm, int v, int a, int hz10)
    {
        vm.Volts = v; vm.Amps = a; vm.Hz = hz10 / 10.0;
    }

    /// <summary>Current readings as a compact JSON string (for the Claude assistant).</summary>
    public string StatusJson()
    {
        var o = new
        {
            tanks = Tanks.Select(t => new { name = t.Name, percent = t.Percent }),
            batteries = Batteries.Select(b => new { name = b.Name, volts = b.Volts, state = b.State }),
            ac = Ac.Select(a => new { name = a.Name, volts = a.Volts, amps = a.Amps, hz = a.Hz, online = a.On }),
        };
        return System.Text.Json.JsonSerializer.Serialize(o);
    }

    /// <summary>All readings as (topic, value) pairs for MQTT publishing.</summary>
    public IEnumerable<(string Topic, string Value)> MqttPoints()
    {
        static string Slug(string s) => s.ToLowerInvariant()
            .Replace(" · ", "_").Replace(" ", "_").Replace("·", "_");
        foreach (var t in Tanks) yield return ($"tanks/{Slug(t.Name)}", ((int)t.Percent).ToString());
        foreach (var b in Batteries) yield return ($"batteries/{Slug(b.Name)}", b.Volts.ToString("0.0"));
        foreach (var a in Ac)
        {
            var s = Slug(a.Name);
            yield return ($"ac/{s}/volts", ((int)a.Volts).ToString());
            yield return ($"ac/{s}/amps", ((int)a.Amps).ToString());
            yield return ($"ac/{s}/hz", a.Hz.ToString("0.0"));
        }
        yield return ("status", StatusJson());
    }
}
