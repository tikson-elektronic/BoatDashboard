using System.Windows;

namespace BoatDashboard;

public partial class App : Application
{
    /// <summary>
    /// Set true only by the passcode-protected shutdown in Settings. In kiosk mode
    /// ShellWindow refuses to close unless this is set, so the dashboard cannot be
    /// exited by Alt+F4 or the window chrome — only via Settings + passcode 5577.
    /// </summary>
    public static bool AllowExit { get; set; }
}
