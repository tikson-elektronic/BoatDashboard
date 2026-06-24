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
        "You are the onboard AI assistant for a boat. You have live access to all systems via tools: " +
        "get_status returns tank levels, battery voltages, and AC (shore/generator) readings; " +
        "control_lights switches lights. Use get_status before answering questions about current state. " +
        "When the user asks to turn lights on/off, call control_lights. Be concise and direct — answer in a " +
        "sentence or two, no preamble. Use plain text, no markdown headers. Flag anything that looks unsafe " +
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
}
