using System.Text.Json;

namespace BoatDashboard.Onboard;

/// <summary>
/// One normalized reading the generic core knows how to publish (ONBOARD_AGENT_SPEC §3/§4).
/// The adapter has already done the hex parsing / unit conversion; the core never sees raw frames.
/// </summary>
public readonly record struct SensorReading(
    string Category,   // "tanks" | "electrical" | "engine" | "safety" | "bilge"
    string Name,       // slug, e.g. "fuel-fwd-port"
    double Value,      // engineering units
    string Unit);      // "%", "V", "A", "Hz", ...

/// <summary>One normalized command the core routes from MQTT (ONBOARD_AGENT_SPEC §6).</summary>
public readonly record struct DeviceCommand(
    string Subsystem,  // "lights", "shades", ... (the cmd topic segment)
    string Target,     // normalized key, e.g. "salon"
    JsonElement? Args);// optional extra params

public readonly record struct CommandResult(bool Ok, string? Error = null);

/// <summary>
/// What an adapter can do — folded into the capability manifest (ONBOARD_AGENT_SPEC §7).
/// Adding a sensor category or light target here requires no core change.
/// </summary>
public sealed class AdapterCapabilities
{
    public bool Sensors { get; init; } = true;
    public bool LightingControl { get; init; }
    public bool Alarms { get; init; }
    public bool Rules { get; init; }
    public bool Fcm { get; init; }
    public bool Jobs { get; init; }

    public IReadOnlyList<string> SensorCategories { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> LightTargets { get; init; } = Array.Empty<string>();
}

/// <summary>
/// The hardware seam that keeps the agent generic. The core talks to hardware ONLY through this
/// interface. Each vessel ships one implementation; the core is unchanged (ONBOARD_AGENT_SPEC §3).
/// </summary>
public interface IVesselAdapter : IAsyncDisposable
{
    /// <summary>Stable id for the manifest "source" field, e.g. "itach_ip2sl".</summary>
    string SourceId { get; }

    /// <summary>What this adapter can do — folded into the manifest.</summary>
    AdapterCapabilities Capabilities { get; }

    /// <summary>Connect to the hardware and START streaming readings.</summary>
    Task StartAsync(CancellationToken ct);

    /// <summary>Fired whenever a fresh reading is available. The core coalesces + publishes on a timer.</summary>
    event Action<SensorReading>? OnReading;

    /// <summary>Adapter connection state for the UI / status.</summary>
    bool HardwareConnected { get; }
    event Action<bool>? OnConnectionChanged;

    /// <summary>Execute a command on the hardware. Core publishes the ACK from the result.</summary>
    Task<CommandResult> ExecuteAsync(DeviceCommand cmd, CancellationToken ct);
}
