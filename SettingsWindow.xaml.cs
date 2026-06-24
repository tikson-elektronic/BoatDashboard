using System.Windows;

namespace BoatDashboard;

public partial class SettingsWindow : Window
{
    public AppSettings Settings { get; private set; }
    public bool Saved { get; private set; }

    public SettingsWindow(AppSettings settings)
    {
        InitializeComponent();
        Settings = settings;

        ApiKeyBox.Password = settings.ClaudeApiKey;
        MqttEnabled.IsChecked = settings.Mqtt.Enabled;
        MqttHost.Text = settings.Mqtt.Host;
        MqttPort.Text = settings.Mqtt.Port.ToString();
        MqttUser.Text = settings.Mqtt.Username;
        MqttPass.Password = settings.Mqtt.Password;
        MqttTopic.Text = settings.Mqtt.BaseTopic;
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        Settings.ClaudeApiKey = ApiKeyBox.Password.Trim();
        Settings.Mqtt.Enabled = MqttEnabled.IsChecked == true;
        Settings.Mqtt.Host = MqttHost.Text.Trim();
        Settings.Mqtt.Port = int.TryParse(MqttPort.Text.Trim(), out var p) ? p : 1883;
        Settings.Mqtt.Username = MqttUser.Text.Trim();
        Settings.Mqtt.Password = MqttPass.Password;
        Settings.Mqtt.BaseTopic = string.IsNullOrWhiteSpace(MqttTopic.Text) ? "boat" : MqttTopic.Text.Trim();

        SettingsStore.Save(Settings);
        Saved = true;
        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
