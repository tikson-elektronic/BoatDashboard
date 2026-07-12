using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;

namespace BoatDashboard;

/// <summary>
/// Talks to the Global Cache iTach IP2SL on TCP 4999.
/// Reads the controller's "&lt;XX:f0,f1,...&gt;" telemetry frames and sends 4-byte
/// little-endian command codes (lights / scenes). Auto-reconnects.
/// </summary>
public sealed class Ip2slClient : IDisposable
{
    public const string Host = "192.168.0.100";
    public const int Port = 4999;

    private static readonly Regex FrameRx =
        new(@"<([0-9A-Fa-f]{2}):([^<>]*)>", RegexOptions.Compiled);

    // latest field array per channel ("00".."03", "FE","FF")
    private readonly ConcurrentDictionary<string, int[]> _channels = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private TcpClient? _tcp;
    private NetworkStream? _stream;
    private volatile bool _connected;

    public bool Connected => _connected;
    public event Action<bool>? ConnectionChanged;

    public void Start()
    {
        _ = Task.Run(ReadLoopAsync);
        _ = Task.Run(HeartbeatLoopAsync);
    }

    /// <summary>Get a telemetry field (hex value as int). Returns null if not seen yet.</summary>
    public int? Field(string channel, int index)
    {
        if (_channels.TryGetValue(channel, out var f) && index >= 0 && index < f.Length)
            return f[index];
        return null;
    }

    /// <summary>
    /// Snapshot of every channel currently captured from the iTach (channel → field array).
    /// Reads the already-open connection's cached frames — issues NO new traffic to the
    /// Global Cache. Used by the /api/raw debug dump to reveal undecoded data.
    /// </summary>
    public IReadOnlyDictionary<string, int[]> SnapshotChannels()
        => _channels.ToDictionary(kv => kv.Key, kv => (int[])kv.Value.Clone());

    /// <summary>Send a 4-byte little-endian command code (sent 3x, like the app).</summary>
    public async Task SendCommandAsync(uint code)
    {
        Log($"SendCommand 0x{code:X8} (connected={_connected}, stream={(_stream != null)})");
        var b = new byte[]
        {
            (byte)(code & 0xFF),
            (byte)((code >> 8) & 0xFF),
            (byte)((code >> 16) & 0xFF),
            (byte)((code >> 24) & 0xFF),
        };
        for (int i = 0; i < 3; i++)
        {
            await WriteAsync(b);
            await Task.Delay(250);
        }
    }

    private static readonly string LogPath =
        System.IO.Path.Combine(AppContext.BaseDirectory, "debug.log");
    public static void Log(string msg)
    {
        try { System.IO.File.AppendAllText(LogPath, $"{DateTime.Now:HH:mm:ss.fff} {msg}\n"); } catch { }
    }

    private async Task WriteAsync(byte[] data)
    {
        var s = _stream;
        if (s == null) { Log("  WriteAsync: stream null, skipped"); return; }
        await _writeLock.WaitAsync();
        try { await s.WriteAsync(data); await s.FlushAsync(); Log($"  wrote {BitConverter.ToString(data)}"); }
        catch (Exception ex) { Log($"  write FAILED: {ex.Message}"); }
        finally { _writeLock.Release(); }
    }

    private async Task ReadLoopAsync()
    {
        var buf = new StringBuilder();
        var bytes = new byte[8192];
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                _tcp = new TcpClient { NoDelay = true };
                await _tcp.ConnectAsync(Host, Port, _cts.Token);
                _stream = _tcp.GetStream();
                SetConnected(true);

                while (!_cts.IsCancellationRequested)
                {
                    int n = await _stream.ReadAsync(bytes, _cts.Token);
                    if (n <= 0) break;
                    buf.Append(Encoding.Latin1.GetString(bytes, 0, n));
                    ParseFrames(buf);
                    if (buf.Length > 4000) buf.Remove(0, buf.Length - 2000);
                }
            }
            catch (OperationCanceledException) { break; }
            catch { /* fallthrough to reconnect */ }

            SetConnected(false);
            try { _stream?.Dispose(); _tcp?.Dispose(); } catch { }
            try { await Task.Delay(1500, _cts.Token); } catch { break; }
        }
    }

    private void ParseFrames(StringBuilder buf)
    {
        var s = buf.ToString();
        int lastEnd = 0;
        foreach (Match m in FrameRx.Matches(s))
        {
            var ch = m.Groups[1].Value.ToUpperInvariant();
            var parts = m.Groups[2].Value.Split(',');
            var vals = new int[parts.Length];
            bool ok = true;
            for (int i = 0; i < parts.Length; i++)
                ok &= int.TryParse(parts[i], System.Globalization.NumberStyles.HexNumber, null, out vals[i]);
            if (ok) _channels[ch] = vals;
            lastEnd = m.Index + m.Length;
        }
        if (lastEnd > 0) buf.Remove(0, Math.Min(lastEnd, buf.Length));
    }

    // The iPad app streams 0xFF every ~6s; mimic it to keep the stream healthy.
    private async Task HeartbeatLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            try { await Task.Delay(6000, _cts.Token); } catch { break; }
            await WriteAsync(new byte[] { 0xFF, 0, 0, 0 });
        }
    }

    private void SetConnected(bool v)
    {
        if (_connected == v) return;
        _connected = v;
        ConnectionChanged?.Invoke(v);
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _stream?.Dispose(); _tcp?.Dispose(); } catch { }
        _cts.Dispose();
    }
}
