using MQTTnet;
using MQTTnet.Client;

namespace BoatDashboard;

/// <summary>Publishes telemetry to an MQTT broker (retained, one topic per metric + a JSON snapshot).</summary>
public sealed class MqttPublisher : IDisposable
{
    private IMqttClient? _client;
    private MqttSettings _s = new();

    public bool Connected => _client?.IsConnected ?? false;
    public event Action<string>? StatusChanged;

    public async Task ConnectAsync(MqttSettings s)
    {
        _s = s;
        Disconnect();
        var factory = new MqttFactory();
        _client = factory.CreateMqttClient();
        _client.DisconnectedAsync += _ => { StatusChanged?.Invoke("MQTT disconnected"); return Task.CompletedTask; };
        var b = new MqttClientOptionsBuilder()
            .WithTcpServer(s.Host, s.Port)
            .WithClientId("boat-dashboard")
            .WithCleanSession();
        if (!string.IsNullOrWhiteSpace(s.Username))
            b = b.WithCredentials(s.Username, s.Password);
        await _client.ConnectAsync(b.Build());
        StatusChanged?.Invoke($"MQTT connected → {s.Host}:{s.Port}");
    }

    public async Task PublishAsync(string subtopic, string payload)
    {
        if (_client is null || !_client.IsConnected) return;
        var topic = $"{_s.BaseTopic.TrimEnd('/')}/{subtopic}";
        try { await _client.PublishStringAsync(topic, payload, retain: true); }
        catch { }
    }

    public void Disconnect()
    {
        try { _client?.Dispose(); } catch { }
        _client = null;
    }

    public void Dispose() => Disconnect();
}
