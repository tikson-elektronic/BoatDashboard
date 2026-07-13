using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;

namespace BoatDashboard;

public partial class SettingsWindow : Window
{
    public AppSettings Settings { get; private set; }
    public bool Saved { get; private set; }

    private readonly DispatcherTimer _pcTimer = new() { Interval = TimeSpan.FromMilliseconds(1500) };
    private readonly LocalServer? _server;

    public SettingsWindow(AppSettings settings, LocalServer? server = null)
    {
        InitializeComponent();
        Settings = settings;
        _server = server;

        ApiKeyBox.Password = settings.ClaudeApiKey;
        KioskCheck.IsChecked = settings.Kiosk;
        CursorCheck.IsChecked = settings.ShowCursor;
        BootCheck.IsChecked = settings.LaunchAtBoot;
        BlinkCheck.IsChecked = settings.BlinkAlarms;

        HardwareIdText.Text = "Hardware ID: " + PairingService.HardwareId;
        NetUrlText.Text = $"http://{LocalServer.LocalIPv4()}:{ShellWindow.HttpPort}";
        NetDiscoveryText.Text = "Discovery (mDNS / Bonjour): BoatDashboard._http._tcp.local";
        AllowListBox.Text = string.Join(Environment.NewLine, settings.LanAllowList);
        CamerasBox.Text = string.Join(Environment.NewLine, settings.Cameras.Select(c => $"{c.Name} | {c.Url}"));
        RefreshPairingUi();
        LoadLogs();

        _pcTimer.Tick += (_, _) => { UpdatePcStats(); RefreshRequests(); };
        _pcTimer.Start();
        UpdatePcStats();
        RefreshRequests();

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

    // ---- LAN connection requests (accept adds the device's MAC to the allowlist) ----

    private string _lastReqKeys = "";

    private void RefreshRequests()
    {
        if (_server is null) return;
        var pending = _server.PendingRequests;
        var keys = string.Join("|", pending.Select(r => r.Key + r.Name));
        if (keys == _lastReqKeys) return;   // avoid rebuilding (and killing button clicks) every tick
        _lastReqKeys = keys;

        ReqPanel.Children.Clear();
        if (pending.Count == 0)
        {
            ReqPanel.Children.Add(new TextBlock
            {
                Text = "No pending requests.",
                FontSize = 11,
                Foreground = System.Windows.Media.Brushes.Gray,
                Margin = new Thickness(2, 0, 0, 0),
            });
            return;
        }

        foreach (var r in pending)
        {
            var row = new Grid { Margin = new Thickness(0, 3, 0, 3) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var info = new TextBlock
            {
                Text = $"{(r.Name.Length > 0 ? r.Name : "Unnamed device")}  ·  {r.Ip}" +
                       (r.Mac is null ? "" : $"  ·  {string.Join(":", Enumerable.Range(0, 6).Select(i => r.Mac.Substring(i * 2, 2)))}"),
                FontSize = 12,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = (System.Windows.Media.Brush)FindResource("TextBrush"),
                TextTrimming = TextTrimming.CharacterEllipsis,
            };
            Grid.SetColumn(info, 0);

            var accept = new Button { Content = "Accept", Height = 28, Padding = new Thickness(12, 0, 12, 0), Margin = new Thickness(8, 0, 0, 0) };
            accept.Style = (Style)FindResource("SceneOn");
            var key = r.Key;
            var entry = r.Mac ?? r.Ip;   // prefer MAC (survives DHCP renewals)
            accept.Click += (_, _) =>
            {
                if (!Settings.LanAllowList.Contains(entry, StringComparer.OrdinalIgnoreCase))
                    Settings.LanAllowList.Add(entry);
                SettingsStore.Save(Settings);
                _server.SetAllowList(Settings.LanAllowList);
                _server.ResolveRequest(key, approve: true);
                AllowListBox.Text = string.Join(Environment.NewLine, Settings.LanAllowList);
                _lastReqKeys = ""; RefreshRequests();
            };
            Grid.SetColumn(accept, 1);

            var reject = new Button { Content = "Reject", Height = 28, Padding = new Thickness(12, 0, 12, 0), Margin = new Thickness(6, 0, 0, 0) };
            reject.Style = (Style)FindResource("LightButton");
            reject.Click += (_, _) =>
            {
                _server.ResolveRequest(key, approve: false);
                _lastReqKeys = ""; RefreshRequests();
            };
            Grid.SetColumn(reject, 2);

            row.Children.Add(info);
            row.Children.Add(accept);
            row.Children.Add(reject);
            ReqPanel.Children.Add(row);
        }
    }

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
        Settings.ShowCursor = CursorCheck.IsChecked == true;
        Settings.LaunchAtBoot = BootCheck.IsChecked == true;
        Settings.BlinkAlarms = BlinkCheck.IsChecked == true;
        Settings.LanAllowList = AllowListBox.Text
            .Split('\n')
            .Select(l => l.Trim().TrimEnd('\r'))
            .Where(l => l.Length > 0)
            .ToList();
        Settings.Cameras = CamerasBox.Text
            .Split('\n')
            .Select(l => l.Trim().TrimEnd('\r'))
            .Where(l => l.Length > 0)
            .Select(l =>
            {
                var i = l.IndexOf('|');
                return i > 0
                    ? new CameraDef { Name = l[..i].Trim(), Url = l[(i + 1)..].Trim() }
                    : new CameraDef { Name = "Camera", Url = l };
            })
            .Where(c => c.Url.Length > 0)
            .ToList();

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
