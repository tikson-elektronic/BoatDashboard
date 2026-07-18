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

    /// <summary>SmartThings Personal Access Token (account.smartthings.com/tokens) for cloud TV control —
    /// the reliable primary path; local WebSocket control is the automatic fallback when offline.</summary>
    public string SmartThingsToken { get; set; } = "";

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

    /// <summary>Shelly relays that drive up/down motors (TV lift, shades), controlled over MQTT. Each motor
    /// maps a dashboard control to a Shelly's MQTT topic + which output is up and which is down.</summary>
    public List<ShellyMotor> ShellyMotors { get; set; } = new();

    /// <summary>Embedded MQTT broker (the dashboard hosts it; Shelly devices connect to this PC). Credentials
    /// the Shellys authenticate with — set them on each Shelly's MQTT config too. Empty = allow anonymous.</summary>
    public int MqttBrokerPort { get; set; } = 1883;
    public string MqttBrokerUser { get; set; } = "boat";
    public string MqttBrokerPass { get; set; } = "";

    /// <summary>Seconds a shade/motor Shelly may be offline (off the broker) before the dashboard alarms.</summary>
    public int ShadeOfflineAlarmSec { get; set; } = 60;

    /// <summary>Spotify Web API (Authorization Code + PKCE) for full Spotify control of the Sonos amps.
    /// ClientId from developer.spotify.com (one-time); RefreshToken is filled by the in-app Connect flow.</summary>
    public string SpotifyClientId { get; set; } = "";
    public string SpotifyRefreshToken { get; set; } = "";

    /// <summary>Scenes (named presets) as an opaque JSON array maintained by the dashboard UI:
    /// [{"id","name","actions":[{"t":"amp|tv|shade|light|lift",...}]}]. Executed in the helm WebView so all
    /// the existing control functions (and live light state) are reused. C# just stores and serves this blob;
    /// automations run a scene by id via the existing AutomationService firing a "scene" action.</summary>
    public string ScenesJson { get; set; } = "[]";
}

/// <summary>A Shelly-controlled up/down motor (TV lift or a shade), driven over MQTT. Two relay outputs:
/// one raises the motor, one lowers it.</summary>
public sealed class ShellyMotor
{
    public string Key { get; set; } = "";      // stable slot id: "tvlift","shadePort","shadeStbd","shadeFront","shadeAft"
    public string Name { get; set; } = "";      // display label
    public string Topic { get; set; } = "";     // the Shelly's MQTT topic prefix (its device id, e.g. shelly2pmg4-abc123)
    public int UpOut { get; set; } = 0;          // output that raises: 0 = O1 (switch:0), 1 = O2 (switch:1)
    public int DownOut { get; set; } = 1;        // output that lowers
    public int TravelSec { get; set; } = 20;     // full-travel time (auto-stop after this, so a relay is never left latched)
    public double StartDelaySec { get; set; } = 0.5;  // dead-time from relay-on until the shade actually starts moving
    public string Ip { get; set; } = "";         // device IP (for pushing firmware config like the auto-off backstop)
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
