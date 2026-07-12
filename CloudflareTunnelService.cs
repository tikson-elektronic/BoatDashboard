using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;

namespace BoatDashboard;

/// <summary>
/// Manages a Cloudflare "quick tunnel" that exposes the local dashboard (http://localhost:8080)
/// to the internet over HTTPS, so the boat can be reached from anywhere without port-forwarding.
///
/// This runs the bundled <c>cloudflared.exe</c> (<c>tunnel --url http://localhost:PORT</c>),
/// parses the assigned <c>https://&lt;random&gt;.trycloudflare.com</c> URL from its output, and
/// exposes it via <see cref="PublicUrl"/> / <see cref="UrlChanged"/>. Quick-tunnel URLs are
/// EPHEMERAL — they change every restart. A stable hostname needs a *named* tunnel, which
/// requires the owner's Cloudflare account login + a domain (see <see cref="NamedTunnelHint"/>).
///
/// Internet exposure is gated by LocalServer's loopback Basic Auth (the tunnel reaches the
/// server as a loopback origin), so the public URL always requires the dashboard login.
/// Off by default — enable via <c>AppSettings.EnableCloudflareTunnel</c>.
/// </summary>
public sealed class CloudflareTunnelService : IDisposable
{
    private static readonly string[] ExeCandidates =
    {
        @"C:\Program Files (x86)\cloudflared\cloudflared.exe",
        @"C:\Program Files\cloudflared\cloudflared.exe",
        "cloudflared.exe",
        "cloudflared",
    };

    private static readonly Regex UrlRx =
        new(@"https://[a-z0-9-]+\.trycloudflare\.com", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public const string NamedTunnelHint =
        "For a STABLE URL: run `cloudflared tunnel login` (opens your Cloudflare account), then " +
        "`cloudflared tunnel create voms` and route a hostname to http://localhost:8080.";

    private readonly int _port;
    private Process? _proc;
    private volatile string? _publicUrl;

    /// <summary>The current public HTTPS URL, or null while starting / if unavailable.</summary>
    public string? PublicUrl => _publicUrl;
    public bool Running => _proc is { HasExited: false };

    /// <summary>Raised whenever the public URL is assigned or changes (empty string when the tunnel stops).</summary>
    public event Action<string>? UrlChanged;

    public CloudflareTunnelService(int localPort = 8080) => _port = localPort;

    public static string? FindExe()
    {
        foreach (var c in ExeCandidates)
        {
            try { if (Path.IsPathRooted(c) && File.Exists(c)) return c; }
            catch { }
        }
        // Fall back to PATH resolution — Process.Start will find it if it's there.
        return ExeCandidates[^1];
    }

    public void Start()
    {
        if (Running) return;
        var exe = FindExe();
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = exe,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add("tunnel");
            psi.ArgumentList.Add("--no-autoupdate");
            psi.ArgumentList.Add("--url");
            psi.ArgumentList.Add($"http://localhost:{_port}");

            _proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
            _proc.OutputDataReceived += (_, e) => Scan(e.Data);
            _proc.ErrorDataReceived += (_, e) => Scan(e.Data);   // cloudflared prints the URL to stderr
            _proc.Exited += (_, _) => { _publicUrl = null; UrlChanged?.Invoke(""); };

            _proc.Start();
            _proc.BeginOutputReadLine();
            _proc.BeginErrorReadLine();
            Ip2slClient.Log("[cloudflare] quick tunnel starting via " + exe);
        }
        catch (Exception ex)
        {
            Ip2slClient.Log("[cloudflare] failed to start: " + ex.Message);
            _proc = null;
        }
    }

    private void Scan(string? line)
    {
        if (string.IsNullOrEmpty(line)) return;
        var m = UrlRx.Match(line);
        if (m.Success && m.Value != _publicUrl)
        {
            _publicUrl = m.Value;
            Ip2slClient.Log("[cloudflare] public URL: " + m.Value);
            UrlChanged?.Invoke(m.Value);
        }
    }

    public void Stop()
    {
        var p = _proc;
        _proc = null;
        _publicUrl = null;
        if (p is null) return;
        try { if (!p.HasExited) p.Kill(entireProcessTree: true); } catch { }
        try { p.Dispose(); } catch { }
    }

    public void Dispose() => Stop();
}
