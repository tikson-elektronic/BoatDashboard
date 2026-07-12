using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Anthropic;
using Anthropic.Models.Messages;

namespace BoatDashboard;

/// <summary>
/// Claude-powered assistant with full access to the boat: it can read every sensor
/// (get_status) and operate every light (control_lights). Uses a manual tool-use loop
/// on claude-opus-4-8 via the official Anthropic C# SDK.
/// </summary>
public sealed class ClaudeAssistant
{
    private readonly AnthropicClient _api;
    private readonly Ip2slClient _client;
    private readonly DashboardViewModel _vm;
    private readonly List<MessageParam> _history = new();

    private const string SystemPrompt =
        "You are the onboard AI assistant for a Lagoon 630 motor yacht. You have live access via tools: " +
        "get_status returns tank levels, battery voltages, and AC (shore/generator) readings; " +
        "get_pc_status returns the onboard PC's CPU load/temperature, LAN dashboard URL, and hardware-link state; " +
        "control_lights switches lights; run_powershell runs any PowerShell command on the onboard PC with the " +
        "dashboard's Administrator privileges (use it for diagnostics, files, networking, and maintenance the " +
        "captain asks for — be careful, and never reboot/shut down or delete data unless explicitly told). " +
        "Use get_status before answering questions about current state. " +
        "When the user asks to turn lights on/off, call control_lights. Be concise and direct — answer in a " +
        "sentence or two, no preamble. Use plain text, no markdown headers. Replies may be read aloud by " +
        "text-to-speech, so keep them natural to hear. Flag anything that looks unsafe " +
        "(e.g. a battery reading 0 V, or a tank nearly empty).";

    private static readonly List<ToolUnion> Tools =
    [
        new Tool
        {
            Name = "get_status",
            Description = "Get current live readings for all boat systems: tank levels (%), " +
                          "battery voltages (V), and AC monitor (Shore 1, Shore 2, Generator: volts, amps, Hz).",
            InputSchema = new()
            {
                Properties = new Dictionary<string, JsonElement>(),
                Required = [],
            },
        },
        new Tool
        {
            Name = "run_powershell",
            Description = "Run a PowerShell command on the onboard PC and return its output. " +
                          "Runs with the dashboard's privileges (Administrator). Use for diagnostics, " +
                          "file/network/system checks, and maintenance the captain asks for. " +
                          "Be careful with destructive commands; never reboot or shut down unless explicitly asked.",
            InputSchema = new()
            {
                Properties = new Dictionary<string, JsonElement>
                {
                    ["command"] = JsonSerializer.SerializeToElement(new
                    {
                        type = "string",
                        description = "The PowerShell command to execute.",
                    }),
                },
                Required = ["command"],
            },
        },
        new Tool
        {
            Name = "get_pc_status",
            Description = "Get the onboard PC's health: CPU load %, CPU temperature (if available), " +
                          "LAN IP / dashboard URL, and hardware-link state (iTach connected or not).",
            InputSchema = new()
            {
                Properties = new Dictionary<string, JsonElement>(),
                Required = [],
            },
        },
        new Tool
        {
            Name = "control_lights",
            Description = "Turn boat lights on or off.",
            InputSchema = new()
            {
                Properties = new Dictionary<string, JsonElement>
                {
                    ["action"] = JsonSerializer.SerializeToElement(new
                    {
                        type = "string",
                        @enum = new[]
                        {
                            "all_on", "all_off", "courtesy", "port_fwd_cabin", "port_fwd_gangway",
                            "port_mid_cabin", "galley", "salon", "stbd_fwd_cabin", "stbd_aft_cabin",
                        },
                        description = "Which light command to send.",
                    }),
                },
                Required = ["action"],
            },
        },
    ];

    public ClaudeAssistant(string apiKey, Ip2slClient client, DashboardViewModel vm)
    {
        _api = new AnthropicClient { ApiKey = apiKey };
        _client = client;
        _vm = vm;
    }

    public async Task<string> AskAsync(string userText)
    {
        _history.Add(new() { Role = Role.User, Content = userText });
        var sb = new StringBuilder();

        for (int turn = 0; turn < 6; turn++)
        {
            Message resp = await _api.Messages.Create(new MessageCreateParams
            {
                Model = "claude-opus-4-8",
                MaxTokens = 4000,
                System = SystemPrompt,
                Tools = Tools,
                Messages = _history.ToList(),
            });

            List<ContentBlockParam> assistant = [];
            List<ContentBlockParam> toolResults = [];

            foreach (ContentBlock block in resp.Content)
            {
                if (block.TryPickText(out TextBlock? text))
                {
                    assistant.Add(new TextBlockParam { Text = text!.Text });
                    sb.AppendLine(text.Text);
                }
                else if (block.TryPickToolUse(out ToolUseBlock? tu))
                {
                    assistant.Add(new ToolUseBlockParam { ID = tu!.ID, Name = tu.Name, Input = tu.Input });
                    string result = await ExecuteToolAsync(tu.Name, tu.Input);
                    toolResults.Add(new ToolResultBlockParam { ToolUseID = tu.ID, Content = result });
                }
            }

            _history.Add(new() { Role = Role.Assistant, Content = assistant });
            if (toolResults.Count == 0) break;
            _history.Add(new() { Role = Role.User, Content = toolResults });
        }

        return sb.ToString().Trim();
    }

    private async Task<string> ExecuteToolAsync(string name, object input)
    {
        switch (name)
        {
            case "get_status":
                return _vm.StatusJson();

            case "run_powershell":
                string psCmd = "";
                try { psCmd = JsonSerializer.SerializeToElement(input).GetProperty("command").GetString() ?? ""; }
                catch { }
                if (string.IsNullOrWhiteSpace(psCmd)) return "No command provided.";
                return await RunPowerShellAsync(psCmd);

            case "get_pc_status":
                return JsonSerializer.Serialize(new
                {
                    cpu_load_percent = Math.Round(PcStats.CpuLoadPercent()),
                    cpu_temp_c = PcStats.CpuTempC(),
                    lan_dashboard_url = $"http://{LocalServer.LocalIPv4()}:8080",
                    itach_connected = _client.Connected,
                });

            case "control_lights":
                string action = "";
                try { action = JsonSerializer.SerializeToElement(input).GetProperty("action").GetString() ?? ""; }
                catch { }
                uint code = action switch
                {
                    "all_on" => 0x0600u,
                    "all_off" => 0x0700u,
                    "courtesy" => 0x0009u,
                    "port_fwd_cabin" => 0x0100u,
                    "port_fwd_gangway" => 0x0200u,
                    "port_mid_cabin" => 0x0300u,
                    "galley" => 0x0500u,
                    "salon" => 0x0800u,
                    "stbd_fwd_cabin" => 0x0900u,
                    "stbd_aft_cabin" => 0x0D00u,
                    _ => 0u,
                };
                if (code == 0) return $"Unknown action '{action}'.";
                await _client.SendCommandAsync(code);
                return $"Command '{action}' sent to the lighting controller.";

            default:
                return $"Unknown tool '{name}'.";
        }
    }

    /// <summary>Runs a PowerShell command on the onboard PC (with the dashboard's privileges).</summary>
    private static async Task<string> RunPowerShellAsync(string command)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add("-NoProfile");
            psi.ArgumentList.Add("-NonInteractive");
            psi.ArgumentList.Add("-ExecutionPolicy");
            psi.ArgumentList.Add("Bypass");
            psi.ArgumentList.Add("-Command");
            psi.ArgumentList.Add(command);

            using var proc = Process.Start(psi);
            if (proc is null) return "Failed to start PowerShell.";

            var outTask = proc.StandardOutput.ReadToEndAsync();
            var errTask = proc.StandardError.ReadToEndAsync();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(90));
            try { await proc.WaitForExitAsync(cts.Token); }
            catch (OperationCanceledException) { try { proc.Kill(true); } catch { } return "Command timed out (90 s)."; }

            var stdout = await outTask;
            var stderr = await errTask;
            var result = (stdout + (stderr.Length > 0 ? "\n[stderr]\n" + stderr : "")).Trim();
            if (result.Length == 0) result = $"(no output; exit code {proc.ExitCode})";
            return result.Length > 6000 ? result[..6000] + "\n…(truncated)" : result;
        }
        catch (Exception ex) { return "PowerShell error: " + ex.Message; }
    }
}
