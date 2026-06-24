using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;

namespace BoatDashboard;

public partial class MainWindow : Window
{
    private readonly DashboardViewModel _vm = new();
    private readonly Ip2slClient _client = new();
    private readonly MqttPublisher _mqtt = new();
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(800) };

    private AppSettings _settings = SettingsStore.Load();
    private ClaudeAssistant? _assistant;
    private DateTime _lastPublish = DateTime.MinValue;
    private bool _busy;

    public MainWindow()
    {
        InitializeComponent();
        DataContext = _vm;

        _client.ConnectionChanged += OnConnectionChanged;
        _client.Start();

        _timer.Tick += OnTick;
        _timer.Start();

        ApplySettings();

        Closed += (_, _) => { _client.Dispose(); _mqtt.Dispose(); };
    }

    private void OnTick(object? sender, EventArgs e)
    {
        _vm.Refresh(_client, $"updated {DateTime.Now:HH:mm:ss}");

        if (_settings.Mqtt.Enabled && _mqtt.Connected && (DateTime.Now - _lastPublish).TotalSeconds >= 5)
        {
            _lastPublish = DateTime.Now;
            foreach (var (topic, value) in _vm.MqttPoints())
                _ = _mqtt.PublishAsync(topic, value);
        }
    }

    private void OnConnectionChanged(bool up) => Dispatcher.Invoke(() =>
    {
        _vm.ConnectionText = up ? "Connected" : "Reconnecting…";
        _vm.ConnectionBrush = up ? Palette.Good : Palette.Bad;
    });

    // ---- Lighting ----
    private async void Light_Click(object sender, RoutedEventArgs e)
    {
        Ip2slClient.Log($"Light_Click fired; dc={(sender as Button)?.DataContext?.GetType().Name ?? "null"}");
        if (sender is Button { DataContext: LightVm light })
        {
            await _client.SendCommandAsync(light.Code);
            light.IsOn = !light.IsOn;          // toggle commanded state
        }
    }

    private async void AllOn_Click(object sender, RoutedEventArgs e)
    {
        await _client.SendCommandAsync(0x0600u);
        foreach (var l in _vm.Lights) l.IsOn = true;
    }

    private async void AllOff_Click(object sender, RoutedEventArgs e)
    {
        await _client.SendCommandAsync(0x0700u);
        foreach (var l in _vm.Lights) l.IsOn = false;
    }

    // ---- Settings ----
    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new SettingsWindow(_settings) { Owner = this };
        if (dlg.ShowDialog() == true)
        {
            _settings = dlg.Settings;
            ApplySettings();
        }
    }

    private void ApplySettings()
    {
        // Claude assistant
        if (!string.IsNullOrWhiteSpace(_settings.ClaudeApiKey))
        {
            _assistant = new ClaudeAssistant(_settings.ClaudeApiKey, _client, _vm);
            _vm.AssistantHint = "ready";
        }
        else
        {
            _assistant = null;
            _vm.AssistantHint = "add API key in settings";
        }

        // MQTT
        _mqtt.Disconnect();
        if (_settings.Mqtt.Enabled)
        {
            _vm.MqttText = "MQTT connecting…";
            _vm.MqttBrush = Palette.Warn;
            _ = ConnectMqttAsync();
        }
        else
        {
            _vm.MqttText = "MQTT off";
            _vm.MqttBrush = Palette.Muted;
        }
    }

    private async Task ConnectMqttAsync()
    {
        try
        {
            await _mqtt.ConnectAsync(_settings.Mqtt);
            Dispatcher.Invoke(() => { _vm.MqttText = "MQTT live"; _vm.MqttBrush = Palette.Good; });
        }
        catch (Exception ex)
        {
            Dispatcher.Invoke(() => { _vm.MqttText = "MQTT error"; _vm.MqttBrush = Palette.Bad; });
            _ = ex;
        }
    }

    // ---- Assistant chat ----
    private void ChatInput_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { e.Handled = true; _ = SendAsync(); }
    }

    private void Send_Click(object sender, RoutedEventArgs e) => _ = SendAsync();

    private async Task SendAsync()
    {
        var text = ChatInput.Text.Trim();
        if (text.Length == 0 || _busy) return;

        if (_assistant is null)
        {
            _vm.Chat.Add(new ChatMessage { Text = "Add your Claude API key in ⚙ Settings to enable the assistant.", FromUser = false });
            ScrollChatToEnd();
            return;
        }

        _busy = true;
        ChatInput.Clear();
        _vm.Chat.Add(new ChatMessage { Text = text, FromUser = true });
        var thinking = new ChatMessage { Text = "…", FromUser = false };
        _vm.Chat.Add(thinking);
        ScrollChatToEnd();

        string reply;
        try { reply = await _assistant.AskAsync(text); }
        catch (Exception ex) { reply = $"Error: {ex.Message}"; }

        _vm.Chat.Remove(thinking);
        _vm.Chat.Add(new ChatMessage { Text = string.IsNullOrWhiteSpace(reply) ? "(no response)" : reply, FromUser = false });
        ScrollChatToEnd();
        _busy = false;
    }

    private void ScrollChatToEnd() => Dispatcher.BeginInvoke(() => ChatScroll.ScrollToEnd(), DispatcherPriority.Background);
}
