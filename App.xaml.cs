using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;

namespace BoatDashboard;

public partial class App : Application
{
    /// <summary>
    /// Set true only by the passcode-protected shutdown in Settings. In kiosk mode
    /// ShellWindow refuses to close unless this is set, so the dashboard cannot be
    /// exited by Alt+F4 or the window chrome — only via Settings + passcode 5577.
    /// </summary>
    public static bool AllowExit { get; set; }

    // Ask Windows to relaunch us automatically if we crash or hang (Restart Manager).
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int RegisterApplicationRestart(string? pwzCommandline, int dwFlags);

    protected override void OnStartup(StartupEventArgs e)
    {
        // ---- Self-healing ----
        // 1) Windows auto-restarts the process on crash/hang (flags 0 = restart in all those cases).
        try { RegisterApplicationRestart(null, 0); } catch { }

        // 2) Survive transient UI-thread exceptions instead of dying — log and keep running.
        DispatcherUnhandledException += (_, args) =>
        {
            Ip2slClient.Log("[selfheal] dispatcher exception: " + args.Exception);
            args.Handled = true;   // don't tear the app down over a recoverable UI glitch
        };

        // 3) Log background-thread / task faults (last-chance; process may still exit → Restart Manager relaunches).
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            Ip2slClient.Log("[selfheal] domain exception: " + (args.ExceptionObject as Exception)?.ToString());
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            Ip2slClient.Log("[selfheal] unobserved task exception: " + args.Exception);
            args.SetObserved();
        };

        base.OnStartup(e);
    }
}
