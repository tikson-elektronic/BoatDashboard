using System.IO;
using System.Text.Json;

namespace BoatDashboard;

public sealed class MqttSettings
{
    public bool Enabled { get; set; } = false;
    public string Host { get; set; } = "localhost";
    public int Port { get; set; } = 1883;
    public string Username { get; set; } = "";
    public string Password { get; set; } = "";
    public string BaseTopic { get; set; } = "boat";
}

public sealed class AppSettings
{
    public string ClaudeApiKey { get; set; } = "";
    public MqttSettings Mqtt { get; set; } = new();

    /// <summary>Fullscreen, cursor-hidden, always-on-top, close-locked kiosk display.</summary>
    public bool Kiosk { get; set; } = false;

    /// <summary>Register the app to start automatically at Windows sign-in.</summary>
    public bool LaunchAtBoot { get; set; } = false;

    /// <summary>Whether alarm indicators pulse/blink. When false they show steady.</summary>
    public bool BlinkAlarms { get; set; } = true;

    /// <summary>HTTP Basic Auth for the LAN dashboard (empty user = no login, per user preference).</summary>
    public string LanUser { get; set; } = "";
    public string LanPass { get; set; } = "";

    /// <summary>
    /// LAN dashboard access allowlist: IP addresses and/or MAC addresses, one entry each.
    /// Empty list = every device on the LAN is allowed. Loopback is always allowed.
    /// </summary>
    public List<string> LanAllowList { get; set; } = new();
}

public static class SettingsStore
{
    private static readonly string Dir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "BoatDashboard");
    private static readonly string FilePath = Path.Combine(Dir, "settings.json");
    private static readonly JsonSerializerOptions Opts = new() { WriteIndented = true };

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new AppSettings();
        }
        catch { }
        return new AppSettings();
    }

    public static void Save(AppSettings s)
    {
        try
        {
            Directory.CreateDirectory(Dir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(s, Opts));
        }
        catch { }
    }
}
