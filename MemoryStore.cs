using System.IO;
using System.Text.Json;

namespace BoatDashboard;

/// <summary>
/// Persistent memory for the vessel AI: user preferences and remembered facts, stored as
/// JSON at C:\voms\memory.json. Claude reads/writes this so the system "remembers" (favourite
/// TV app, preferred amp volume, cabin names, routines, etc.) across restarts.
/// </summary>
public sealed class MemoryStore
{
    private static readonly string Path_ = @"C:\voms\memory.json";
    private readonly object _lock = new();
    private Dictionary<string, string> _mem = new(StringComparer.OrdinalIgnoreCase);

    public MemoryStore() => Load();

    private void Load()
    {
        try
        {
            if (File.Exists(Path_))
                _mem = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(Path_))
                       ?? new(StringComparer.OrdinalIgnoreCase);
        }
        catch { _mem = new(StringComparer.OrdinalIgnoreCase); }
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(@"C:\voms");
            File.WriteAllText(Path_, JsonSerializer.Serialize(_mem, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }

    public void Remember(string key, string value)
    {
        lock (_lock) { _mem[key] = value; Save(); }
    }

    public void Forget(string key)
    {
        lock (_lock) { if (_mem.Remove(key)) Save(); }
    }

    public string? Recall(string key)
    {
        lock (_lock) return _mem.TryGetValue(key, out var v) ? v : null;
    }

    public string AllJson()
    {
        lock (_lock) return JsonSerializer.Serialize(_mem);
    }
}
