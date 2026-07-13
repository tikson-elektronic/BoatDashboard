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

    /// <summary>Show the mouse cursor even in kiosk mode (for mouse/trackpad use on the helm PC).</summary>
    public bool ShowCursor { get; set; } = true;

    /// <summary>Register the app to start automatically at Windows sign-in.</summary>
    public bool LaunchAtBoot { get; set; } = false;

    /// <summary>Whether alarm indicators pulse/blink. When false they show steady.</summary>
    public bool BlinkAlarms { get; set; } = true;

    /// <summary>HTTP Basic Auth for the LAN dashboard (empty user = no login, per user preference).</summary>
    public string LanUser { get; set; } = "";
    public string LanPass { get; set; } = "";

    /// <summary>Auto-start a Cloudflare quick tunnel so the dashboard is reachable from the internet.
    /// Off by default — a tunnel exposes the boat publicly (login-gated). URL is ephemeral.</summary>
    public bool EnableCloudflareTunnel { get; set; } = false;

    /// <summary>NMEA 2000 gateway (YachtDevices/Actisense) TCP address for navigation + engine data.
    /// Empty = no gateway; the navigation/engine pages keep demo/last values.</summary>
    public string NmeaHost { get; set; } = "";
    public int NmeaPort { get; set; } = 2000;

    /// <summary>Announce the dashboard to Navico MFDs (Simrad/B&amp;G/Lowrance) via the HTML5 Integration
    /// Protocol, so it appears as an app icon on the chartplotter and can raise alarms there.</summary>
    public bool EnableNavicoMfd { get; set; } = true;

    /// <summary>
    /// LAN dashboard access allowlist: IP addresses and/or MAC addresses, one entry each.
    /// Empty list = every device on the LAN is allowed. Loopback is always allowed.
    /// </summary>
    public List<string> LanAllowList { get; set; } = new();

    /// <summary>Vessel position for daylight-based automations (default Antibes).</summary>
    public double VesselLat { get; set; } = 43.55;
    public double VesselLon { get; set; } = 7.02;

    /// <summary>Cameras / NVR streams (Blue Iris, RTSP, MJPEG) shown on the dashboard.</summary>
    public List<CameraDef> Cameras { get; set; } = new();
}

public sealed class CameraDef
{
    public string Name { get; set; } = "";
    public string Url { get; set; } = "";   // rtsp://… , http://…/mjpg/… , or an MJPEG/HTTP snapshot URL
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
