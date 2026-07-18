using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace BoatDashboard;

/// <summary>
/// Full Spotify control from the dashboard via the Spotify Web API (Authorization Code + PKCE, so only a
/// Client ID is needed — no secret). Searches the catalogue, lists the Sonos amps as Spotify Connect targets,
/// and plays / transports on whichever amp (zone) the user picks. The one-time login is done at the helm PC
/// (loopback redirect); after that the stored refresh token keeps it working, including from the iPad.
///
/// Setup (once): create an app at developer.spotify.com, add redirect URI <c>http://127.0.0.1:8080/spotify/callback</c>,
/// paste the Client ID into Settings, tap Connect. Requires Spotify Premium (streaming scope) and Spotify
/// linked in the Sonos system so the amps appear as Connect devices.
/// </summary>
public sealed class SpotifyService
{
    public const string RedirectUri = "http://127.0.0.1:8080/spotify/callback";
    private const string Scopes = "user-read-playback-state user-modify-playback-state user-read-currently-playing streaming user-read-private";

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(10) };

    private readonly object _lock = new();
    private string _clientId = "";
    private string _refreshToken = "";
    private string _accessToken = "";
    private DateTime _accessExp = DateTime.MinValue;
    private string _pkceVerifier = "";
    private string _state = "";

    /// <summary>Raised when a new refresh token is obtained, so the host can persist it.</summary>
    public event Action<string>? OnRefreshToken;

    public void SetConfig(string clientId, string refreshToken)
    {
        lock (_lock) { _clientId = clientId ?? ""; _refreshToken = refreshToken ?? ""; }
    }

    /// <summary>Update just the Client ID (keeps any existing refresh token / access token).</summary>
    public void SetClientId(string clientId)
    {
        lock (_lock) _clientId = clientId ?? "";
    }

    public bool HasClientId { get { lock (_lock) return !string.IsNullOrWhiteSpace(_clientId); } }
    public bool Connected { get { lock (_lock) return !string.IsNullOrWhiteSpace(_refreshToken); } }

    // ---- OAuth (PKCE) ----------------------------------------------------

    /// <summary>Build the Spotify authorize URL and remember the PKCE verifier + state for the callback.</summary>
    public string BuildAuthUrl()
    {
        string cid; lock (_lock) cid = _clientId;
        var verifier = B64Url(RandomNumberGenerator.GetBytes(64));
        var challenge = B64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        var state = B64Url(RandomNumberGenerator.GetBytes(12));
        lock (_lock) { _pkceVerifier = verifier; _state = state; }
        var q = new Dictionary<string, string>
        {
            ["client_id"] = cid,
            ["response_type"] = "code",
            ["redirect_uri"] = RedirectUri,
            ["scope"] = Scopes,
            ["code_challenge_method"] = "S256",
            ["code_challenge"] = challenge,
            ["state"] = state,
        };
        return "https://accounts.spotify.com/authorize?" + string.Join("&", q.Select(kv => $"{Uri.EscapeDataString(kv.Key)}={Uri.EscapeDataString(kv.Value)}"));
    }

    /// <summary>Exchange the auth code for tokens, store + persist the refresh token. Returns true on success.</summary>
    public async Task<bool> HandleCallbackAsync(string code, string? state)
    {
        string cid, verifier, expect;
        lock (_lock) { cid = _clientId; verifier = _pkceVerifier; expect = _state; }
        if (!string.IsNullOrEmpty(expect) && state != expect) return false;
        var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["redirect_uri"] = RedirectUri,
            ["client_id"] = cid,
            ["code_verifier"] = verifier,
        });
        try
        {
            using var resp = await Http.PostAsync("https://accounts.spotify.com/api/token", form);
            var body = await resp.Content.ReadAsStringAsync();
            if (!resp.IsSuccessStatusCode) return false;
            using var d = JsonDocument.Parse(body);
            var root = d.RootElement;
            var access = root.GetProperty("access_token").GetString() ?? "";
            var refresh = root.TryGetProperty("refresh_token", out var rt) ? rt.GetString() ?? "" : "";
            var expin = root.TryGetProperty("expires_in", out var ei) ? ei.GetInt32() : 3600;
            lock (_lock) { _accessToken = access; _accessExp = DateTime.UtcNow.AddSeconds(expin - 30); if (!string.IsNullOrEmpty(refresh)) _refreshToken = refresh; }
            if (!string.IsNullOrEmpty(refresh)) OnRefreshToken?.Invoke(refresh);
            return true;
        }
        catch { return false; }
    }

    private async Task<string?> EnsureTokenAsync()
    {
        string cid, refresh, access; DateTime exp;
        lock (_lock) { cid = _clientId; refresh = _refreshToken; access = _accessToken; exp = _accessExp; }
        if (!string.IsNullOrEmpty(access) && DateTime.UtcNow < exp) return access;
        if (string.IsNullOrEmpty(refresh) || string.IsNullOrEmpty(cid)) return null;
        var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = refresh,
            ["client_id"] = cid,
        });
        try
        {
            using var resp = await Http.PostAsync("https://accounts.spotify.com/api/token", form);
            if (!resp.IsSuccessStatusCode) return null;
            using var d = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            var root = d.RootElement;
            var newAccess = root.GetProperty("access_token").GetString() ?? "";
            var expin = root.TryGetProperty("expires_in", out var ei) ? ei.GetInt32() : 3600;
            var newRefresh = root.TryGetProperty("refresh_token", out var rt) ? rt.GetString() ?? "" : "";
            lock (_lock) { _accessToken = newAccess; _accessExp = DateTime.UtcNow.AddSeconds(expin - 30); if (!string.IsNullOrEmpty(newRefresh)) _refreshToken = newRefresh; }
            if (!string.IsNullOrEmpty(newRefresh)) OnRefreshToken?.Invoke(newRefresh);
            return newAccess;
        }
        catch { return null; }
    }

    // ---- Web API calls ---------------------------------------------------

    private async Task<(bool ok, string body)> ApiAsync(HttpMethod method, string url, string? jsonBody = null)
    {
        var token = await EnsureTokenAsync();
        if (token is null) return (false, "");
        using var req = new HttpRequestMessage(method, url);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (jsonBody is not null) req.Content = new StringContent(jsonBody, Encoding.UTF8, "application/json");
        try
        {
            using var resp = await Http.SendAsync(req);
            var body = resp.Content is null ? "" : await resp.Content.ReadAsStringAsync();
            return (resp.IsSuccessStatusCode, body);
        }
        catch { return (false, ""); }
    }

    /// <summary>Search tracks/playlists/albums/artists; returns a compact JSON list for the picker.</summary>
    public async Task<string> SearchJsonAsync(string query, int limit = 8)
    {
        if (string.IsNullOrWhiteSpace(query)) return "[]";
        var url = $"https://api.spotify.com/v1/search?q={Uri.EscapeDataString(query)}&type=track,playlist,album,artist&limit={limit}";
        var (ok, body) = await ApiAsync(HttpMethod.Get, url);
        if (!ok) return "[]";
        var items = new List<object>();
        try
        {
            using var d = JsonDocument.Parse(body);
            var root = d.RootElement;
            void AddArr(string prop, string kind, bool ctx)
            {
                if (!root.TryGetProperty(prop, out var pe) || !pe.TryGetProperty("items", out var arr)) return;
                foreach (var it in arr.EnumerateArray())
                {
                    if (it.ValueKind != JsonValueKind.Object) continue;
                    var uri = it.TryGetProperty("uri", out var u) ? u.GetString() : null;
                    var name = it.TryGetProperty("name", out var n) ? n.GetString() : null;
                    if (uri is null || name is null) continue;
                    string sub = kind;
                    if (kind == "track" && it.TryGetProperty("artists", out var ta) && ta.GetArrayLength() > 0)
                        sub = ta[0].GetProperty("name").GetString() ?? "track";
                    else if (kind == "album" && it.TryGetProperty("artists", out var aa) && aa.GetArrayLength() > 0)
                        sub = "Album · " + (aa[0].GetProperty("name").GetString() ?? "");
                    else if (kind == "playlist") sub = "Playlist";
                    else if (kind == "artist") sub = "Artist";
                    string? img = null;
                    var imgProp = kind == "track" ? (it.TryGetProperty("album", out var alb) && alb.TryGetProperty("images", out var ti) ? ti : default) : (it.TryGetProperty("images", out var ii) ? ii : default);
                    if (imgProp.ValueKind == JsonValueKind.Array && imgProp.GetArrayLength() > 0)
                        img = imgProp[imgProp.GetArrayLength() - 1].GetProperty("url").GetString();
                    items.Add(new { uri, name, sub, kind, ctx, img });
                }
            }
            AddArr("tracks", "track", false);
            AddArr("playlists", "playlist", true);
            AddArr("albums", "album", true);
            AddArr("artists", "artist", true);
        }
        catch { }
        return JsonSerializer.Serialize(items);
    }

    /// <summary>List Spotify Connect devices (the Sonos amps show here when linked). [{id,name,active,vol}].</summary>
    public async Task<string> DevicesJsonAsync()
    {
        var (ok, body) = await ApiAsync(HttpMethod.Get, "https://api.spotify.com/v1/me/player/devices");
        if (!ok) return "[]";
        var list = new List<object>();
        try
        {
            using var d = JsonDocument.Parse(body);
            if (d.RootElement.TryGetProperty("devices", out var arr))
                foreach (var dev in arr.EnumerateArray())
                    list.Add(new
                    {
                        id = dev.TryGetProperty("id", out var i) ? i.GetString() : null,
                        name = dev.TryGetProperty("name", out var n) ? n.GetString() : null,
                        active = dev.TryGetProperty("is_active", out var a) && a.GetBoolean(),
                        vol = dev.TryGetProperty("volume_percent", out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : -1,
                    });
        }
        catch { }
        return JsonSerializer.Serialize(list);
    }

    private async Task<string?> DeviceIdForZoneAsync(string zone)
    {
        var json = await DevicesJsonAsync();
        try
        {
            using var d = JsonDocument.Parse(json);
            foreach (var dev in d.RootElement.EnumerateArray())
            {
                var name = dev.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                if (name.Equals(zone, StringComparison.OrdinalIgnoreCase) ||
                    name.Replace(" ", "").Equals(zone.Replace(" ", ""), StringComparison.OrdinalIgnoreCase))
                    return dev.TryGetProperty("id", out var i) ? i.GetString() : null;
            }
        }
        catch { }
        return null;
    }

    /// <summary>Play a track/playlist/album/artist on the given zone's amp. ctx=true => context_uri, else uris[].</summary>
    public async Task<bool> PlayAsync(string zone, string uri, bool ctx)
    {
        var devId = await DeviceIdForZoneAsync(zone);
        if (devId is null) return false;
        // transfer to the target amp first (so playback lands there even if it was idle), then play
        await ApiAsync(HttpMethod.Put, "https://api.spotify.com/v1/me/player",
            JsonSerializer.Serialize(new { device_ids = new[] { devId }, play = false }));
        var body = ctx
            ? JsonSerializer.Serialize(new { context_uri = uri })
            : JsonSerializer.Serialize(new { uris = new[] { uri } });
        var (ok, _) = await ApiAsync(HttpMethod.Put, $"https://api.spotify.com/v1/me/player/play?device_id={Uri.EscapeDataString(devId)}", body);
        return ok;
    }

    /// <summary>Transport on a zone: play | pause | next | previous.</summary>
    public async Task<bool> ControlAsync(string zone, string action)
    {
        var devId = await DeviceIdForZoneAsync(zone);
        var qs = devId is null ? "" : $"?device_id={Uri.EscapeDataString(devId)}";
        var (ok, _) = action switch
        {
            "pause" => await ApiAsync(HttpMethod.Put, $"https://api.spotify.com/v1/me/player/pause{qs}"),
            "play" => await ApiAsync(HttpMethod.Put, $"https://api.spotify.com/v1/me/player/play{qs}"),
            "next" => await ApiAsync(HttpMethod.Post, $"https://api.spotify.com/v1/me/player/next{qs}"),
            "previous" or "prev" => await ApiAsync(HttpMethod.Post, $"https://api.spotify.com/v1/me/player/previous{qs}"),
            _ => (false, ""),
        };
        return ok;
    }

    /// <summary>Now-playing for a zone (or the active device): {playing,title,artist,img,device}.</summary>
    public async Task<string> NowPlayingJsonAsync()
    {
        var (ok, body) = await ApiAsync(HttpMethod.Get, "https://api.spotify.com/v1/me/player");
        if (!ok || string.IsNullOrWhiteSpace(body)) return "{}";
        try
        {
            using var d = JsonDocument.Parse(body);
            var root = d.RootElement;
            string? title = null, artist = null, img = null, device = null; bool playing = false;
            if (root.TryGetProperty("is_playing", out var ip)) playing = ip.GetBoolean();
            if (root.TryGetProperty("device", out var dev) && dev.TryGetProperty("name", out var dn)) device = dn.GetString();
            if (root.TryGetProperty("item", out var item) && item.ValueKind == JsonValueKind.Object)
            {
                title = item.TryGetProperty("name", out var nm) ? nm.GetString() : null;
                if (item.TryGetProperty("artists", out var ar) && ar.GetArrayLength() > 0)
                    artist = ar[0].GetProperty("name").GetString();
                if (item.TryGetProperty("album", out var al) && al.TryGetProperty("images", out var ims) && ims.GetArrayLength() > 0)
                    img = ims[ims.GetArrayLength() - 1].GetProperty("url").GetString();
            }
            return JsonSerializer.Serialize(new { playing, title, artist, img, device });
        }
        catch { return "{}"; }
    }

    private static string B64Url(byte[] b) =>
        Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
