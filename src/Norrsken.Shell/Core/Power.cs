using System.Diagnostics;
using Norrsken.Shell.Interop;

namespace Norrsken.Shell.Core;

public static class Power
{
    public static void Lock() => Native.LockWorkStation();
    public static void Sleep() => Native.SetSuspendState(false, false, false);
    public static void SignOut() => Run("/l");
    public static void Restart() => Run("/r /t 0");
    public static void ShutDown() => Run("/s /t 0");

    static void Run(string args) =>
        Process.Start(new ProcessStartInfo("shutdown.exe", args) { CreateNoWindow = true, UseShellExecute = false });

    /// <summary>Battery percentage, or null on machines without a battery (desktops, most VMs).</summary>
    public static (int Percent, bool Charging)? Battery()
    {
        if (!Native.GetSystemPowerStatus(out var s)) return null;
        if ((s.BatteryFlag & 128) != 0 || s.BatteryLifePercent == 255) return null;
        return (s.BatteryLifePercent, s.ACLineStatus == 1);
    }
}
