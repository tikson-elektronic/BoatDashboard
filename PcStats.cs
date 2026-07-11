using System.Management;
using System.Runtime.InteropServices;

namespace BoatDashboard;

/// <summary>
/// Onboard-PC host telemetry for the Settings page: CPU load (kernel/user vs idle deltas
/// via GetSystemTimes) and CPU temperature (ACPI thermal zone over WMI, when the board
/// exposes it — many desktops/VMs do not, in which case temperature is null).
/// </summary>
public static class PcStats
{
    [StructLayout(LayoutKind.Sequential)]
    private struct FileTimeRaw { public uint Low; public uint High; }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemTimes(out FileTimeRaw idle, out FileTimeRaw kernel, out FileTimeRaw user);

    private static ulong U(FileTimeRaw f) => ((ulong)f.High << 32) | f.Low;
    private static ulong _pIdle, _pKernel, _pUser;
    private static bool _have;

    /// <summary>System-wide CPU utilisation since the previous call (0 on the first call).</summary>
    public static double CpuLoadPercent()
    {
        if (!GetSystemTimes(out var i, out var k, out var u)) return 0;
        ulong idle = U(i), kern = U(k), user = U(u);   // kernel time includes idle time
        if (!_have) { _pIdle = idle; _pKernel = kern; _pUser = user; _have = true; return 0; }
        ulong dIdle = idle - _pIdle, dKern = kern - _pKernel, dUser = user - _pUser;
        _pIdle = idle; _pKernel = kern; _pUser = user;
        ulong total = dKern + dUser;
        if (total == 0) return 0;
        return Math.Clamp((double)(total - dIdle) / total * 100.0, 0, 100);
    }

    /// <summary>CPU temperature in °C, or null if the platform doesn't expose a thermal zone.</summary>
    public static double? CpuTempC()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(
                @"root\WMI", "SELECT CurrentTemperature FROM MSAcpi_ThermalZoneTemperature");
            foreach (ManagementBaseObject mo in searcher.Get())
            {
                var tenthKelvin = Convert.ToDouble(mo["CurrentTemperature"]);
                if (tenthKelvin > 0) return tenthKelvin / 10.0 - 273.15;
            }
        }
        catch { /* not available on this platform */ }
        return null;
    }
}
