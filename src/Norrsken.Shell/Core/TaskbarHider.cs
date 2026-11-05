using System.Diagnostics;
using System.Runtime.InteropServices;
using Norrsken.Shell.Interop;
using static Norrsken.Shell.Interop.Native;

namespace Norrsken.Shell.Core;

/// <summary>
/// Optionally hides the Windows taskbar while the dock runs. Nothing persistent is changed:
/// the taskbar windows are only hidden, a marker file records that, and a tiny guard process
/// (`--guard pid`) shows them again if Norrsken exits for any reason, including a crash.
/// </summary>
public static class TaskbarHider
{
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern IntPtr FindWindowExW(IntPtr parent, IntPtr after, string? cls, string? title);

    const uint EVENT_OBJECT_SHOW = 0x8002;

    static string Marker => Path.Combine(ShellSettings.Directory, "taskbar-hidden");
    static WinEventProc? _proc;
    static IntPtr _hook;
    static bool _guardStarted;
    static HashSet<IntPtr> _bars = new();

    public static bool IsHidden { get; private set; }

    static List<IntPtr> TaskbarWindows()
    {
        var list = new List<IntPtr>();
        IntPtr main = FindWindowExW(IntPtr.Zero, IntPtr.Zero, "Shell_TrayWnd", null);
        if (main != IntPtr.Zero) list.Add(main);
        IntPtr h = IntPtr.Zero;
        while ((h = FindWindowExW(IntPtr.Zero, h, "Shell_SecondaryTrayWnd", null)) != IntPtr.Zero) list.Add(h);
        return list;
    }

    public static void Apply(bool hide)
    {
        if (hide) Hide();
        else if (IsHidden) Restore();
    }

    const uint ABM_GETSTATE = 4, ABM_SETSTATE = 10, ABS_AUTOHIDE = 1;

    // A hidden taskbar that isn't auto-hide still reserves its strip of screen; switching it to
    // auto-hide frees that space. The previous state is kept in the marker and put back on restore.
    static uint TaskbarState()
    {
        var abd = new APPBARDATA { cbSize = Marshal.SizeOf<APPBARDATA>() };
        return SHAppBarMessage(ABM_GETSTATE, ref abd);
    }

    static void SetTaskbarState(uint state)
    {
        var abd = new APPBARDATA
        {
            cbSize = Marshal.SizeOf<APPBARDATA>(),
            hWnd = FindWindowExW(IntPtr.Zero, IntPtr.Zero, "Shell_TrayWnd", null),
            lParam = (IntPtr)state,
        };
        SHAppBarMessage(ABM_SETSTATE, ref abd);
    }

    static void Hide()
    {
        Directory.CreateDirectory(ShellSettings.Directory);
        uint prior = File.Exists(Marker) && ReadPriorState() is uint saved ? saved : TaskbarState();
        File.WriteAllText(Marker, $"{Environment.ProcessId};{prior}");
        if ((prior & ABS_AUTOHIDE) == 0) SetTaskbarState(prior | ABS_AUTOHIDE);
        StartGuard();
        _bars = TaskbarWindows().ToHashSet();
        foreach (var h in _bars) ShowWindow(h, 0 /* SW_HIDE */);
        IsHidden = true;
        // Explorer re-shows its taskbar on some actions (e.g. opening Start); quietly hide it again.
        if (_hook == IntPtr.Zero)
        {
            _proc = (_, _, hwnd, idObject, _, _, _) =>
            {
                if (IsHidden && idObject == 0 && _bars.Contains(hwnd)) ShowWindow(hwnd, 0);
            };
            _hook = SetWinEventHook(EVENT_OBJECT_SHOW, EVENT_OBJECT_SHOW, IntPtr.Zero, _proc, 0, 0, WINEVENT_OUTOFCONTEXT);
        }
        Log.Write("Windows taskbar hidden");
    }

    public static void Restore()
    {
        if (_hook != IntPtr.Zero) { UnhookWinEvent(_hook); _hook = IntPtr.Zero; }
        if (ReadPriorState() is uint prior && prior != TaskbarState()) SetTaskbarState(prior);
        foreach (var h in TaskbarWindows()) ShowWindow(h, 5 /* SW_SHOW */);
        IsHidden = false;
        try { File.Delete(Marker); } catch (IOException) { }
        Log.Write("Windows taskbar restored");
    }

    static uint? ReadPriorState()
    {
        try
        {
            var parts = File.ReadAllText(Marker).Split(';');
            return parts.Length > 1 && uint.TryParse(parts[1], out uint s) ? s : null;
        }
        catch (IOException) { return null; }
    }

    /// <summary>If a previous session died with the taskbar hidden and no guard ran, show it now.</summary>
    public static void RecoverFromCrash()
    {
        if (File.Exists(Marker)) Restore();
    }

    static void StartGuard()
    {
        if (_guardStarted) return;
        _guardStarted = true;
        try
        {
            Process.Start(new ProcessStartInfo(Environment.ProcessPath!, $"--guard {Environment.ProcessId}")
            {
                UseShellExecute = false, CreateNoWindow = true,
            });
        }
        catch (Exception ex) { Log.Write("Taskbar guard failed to start: " + ex.Message); }
    }

    /// <summary>Guard process body: wait for the dock's process to end, then make sure the taskbar is back.</summary>
    public static int RunGuard(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            p.WaitForExit();
        }
        catch (ArgumentException) { } // already gone
        if (File.Exists(Marker)) Restore();
        return 0;
    }
}

public static class Autostart
{
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string ValueName = "Norrsken";

    public static bool IsEnabled
    {
        get
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RunKey);
            return SystemIntegration.TaskExists() || key?.GetValue(ValueName) is string v && v.Contains(Environment.ProcessPath!, StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>Only ever called from the user's own toggle in Settings.</summary>
    public static void Set(bool enabled)
    {
        // Elevated: the instant-start task (for everyone on this PC). Otherwise: this account's Run key.
        if (Unelevated.IsElevated)
        {
            if (enabled) SystemIntegration.Install(Environment.ProcessPath!);
            else System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("schtasks.exe", $"/Delete /TN \"{SystemIntegration.TaskName}\" /F") { UseShellExecute = false, CreateNoWindow = true })?.WaitForExit(5000);
        }
        using var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(RunKey);
        if (enabled) key.SetValue(ValueName, $"\"{Environment.ProcessPath}\"");
        else key.DeleteValue(ValueName, throwOnMissingValue: false);
    }
}
