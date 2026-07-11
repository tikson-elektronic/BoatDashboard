using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;

namespace BoatDashboard;

public partial class SettingsWindow : Window
{
    public AppSettings Settings { get; private set; }
    public bool Saved { get; private set; }

    private readonly DispatcherTimer _pcTimer = new() { Interval = TimeSpan.FromMilliseconds(1500) };

    public SettingsWindow(AppSettings settings)
    {
        InitializeComponent();
        Settings = settings;

        ApiKeyBox.Password = settings.ClaudeApiKey;
        KioskCheck.IsChecked = settings.Kiosk;
        BootCheck.IsChecked = settings.LaunchAtBoot;
        BlinkCheck.IsChecked = settings.BlinkAlarms;

        HardwareIdText.Text = "Hardware ID: " + PairingService.HardwareId;
        NetUrlText.Text = $"http://{LocalServer.LocalIPv4()}:{ShellWindow.HttpPort}";
        NetDiscoveryText.Text = "Discovery (mDNS / Bonjour): BoatDashboard._http._tcp.local";
        RefreshPairingUi();
        LoadLogs();

        _pcTimer.Tick += (_, _) => UpdatePcStats();
        _pcTimer.Start();
        UpdatePcStats();

        Closed += (_, _) => _pcTimer.Stop();
    }

    private void UpdatePcStats()
    {
        CpuLoadText.Text = PcStats.CpuLoadPercent().ToString("0") + " %";
        var t = PcStats.CpuTempC();
        CpuTempText.Text = t is { } c ? c.ToString("0.0") + " °C" : "n/a";
    }

    private void LoadLogs()
    {
        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, "debug.log");
            if (!File.Exists(path)) { LogsBox.Text = "(no log yet)"; return; }
            // Read the last ~200 lines, tolerating the file being open by the app.
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var sr = new StreamReader(fs);
            var all = sr.ReadToEnd().Split('\n');
            var tail = all.Length > 200 ? all[^200..] : all;
            LogsBox.Text = string.Join("\n", tail);
            LogsBox.ScrollToEnd();
        }
        catch (Exception ex) { LogsBox.Text = "Could not read log: " + ex.Message; }
    }

    private void RefreshLogs_Click(object sender, RoutedEventArgs e) => LoadLogs();

    private void RefreshPairingUi()
    {
        var creds = PairingService.LoadSaved();
        if (creds is { VesselId.Length: > 0 })
        {
            PairStatusText.Text = "Paired  •  vessel " + creds.VesselId;
            UnpairButton.Visibility = Visibility.Visible;
        }
        else
        {
            PairStatusText.Text = "Not paired. Enter the code generated in the Alive app.";
            UnpairButton.Visibility = Visibility.Collapsed;
        }
    }

    private void ShowPairMessage(string text, bool ok)
    {
        PairMsgText.Text = text;
        PairMsgText.Foreground = ok ? Brushes.MediumSeaGreen : Brushes.IndianRed;
        PairMsgText.Visibility = Visibility.Visible;
    }

    private async void Pair_Click(object sender, RoutedEventArgs e)
    {
        var code = PairCodeBox.Text.Trim();
        if (code.Length < 6)
        {
            ShowPairMessage("Enter the full pairing code (at least 6 characters).", false);
            return;
        }

        PairButton.IsEnabled = false;
        PairCodeBox.IsEnabled = false;
        ShowPairMessage("Pairing with the Alive cloud…", true);
        try
        {
            var result = await PairingService.ClaimAsync(code);
            if (result.Ok)
            {
                PairCodeBox.Clear();
                ShowPairMessage("Paired successfully. Credentials saved to C:\\voms\\mqtt.env.", true);
                RefreshPairingUi();
            }
            else
            {
                ShowPairMessage(result.Error ?? "Pairing failed.", false);
            }
        }
        finally
        {
            PairButton.IsEnabled = true;
            PairCodeBox.IsEnabled = true;
        }
    }

    private void Unpair_Click(object sender, RoutedEventArgs e)
    {
        var confirm = MessageBox.Show(this,
            "Unpair this PC from the vessel? MQTT credentials will be deleted.",
            "Unpair", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (confirm != MessageBoxResult.Yes) return;
        PairingService.Unpair();
        ShowPairMessage("This PC has been unpaired.", true);
        RefreshPairingUi();
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        Settings.ClaudeApiKey = ApiKeyBox.Password.Trim();
        Settings.Kiosk = KioskCheck.IsChecked == true;
        Settings.LaunchAtBoot = BootCheck.IsChecked == true;
        Settings.BlinkAlarms = BlinkCheck.IsChecked == true;

        SetLaunchAtBoot(Settings.LaunchAtBoot);
        SettingsStore.Save(Settings);
        Saved = true;
        DialogResult = true;
        Close();
    }

    /// <summary>Registers/unregisters the app under the current user's Run key.</summary>
    private static void SetLaunchAtBoot(bool on)
    {
        const string runKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        const string valueName = "BoatDashboard";
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(runKey, writable: true);
            if (key is null) return;
            if (on)
            {
                var exe = Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule?.FileName;
                if (!string.IsNullOrEmpty(exe))
                    key.SetValue(valueName, "\"" + exe + "\"");
            }
            else
            {
                key.DeleteValue(valueName, throwOnMissingValue: false);
            }
        }
        catch { /* non-fatal: startup registration is best-effort */ }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void Kill_Click(object sender, RoutedEventArgs e)
    {
        const string passcode = "5577";
        if (KillPass.Password != passcode)
        {
            MessageBox.Show(this, "Incorrect passcode.", "Shut Down",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            KillPass.Clear();
            KillPass.Focus();
            return;
        }

        var confirm = MessageBox.Show(this, "Shut down the dashboard now?", "Shut Down",
            MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (confirm == MessageBoxResult.Yes)
        {
            App.AllowExit = true;   // authorise the kiosk close-guard to let the app exit
            Application.Current.Shutdown();
        }
    }
}
