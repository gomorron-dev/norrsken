using System.Windows.Threading;
using Norrsken.Shell.Interop;
using static Norrsken.Shell.Interop.Native;

namespace Norrsken.Shell.Core;

/// <summary>
/// Decides when something is full screen on the primary monitor (YouTube/browser full screen,
/// borderless or exclusive games, video players, presentations) so the dock and its popups get
/// out of the way. Three signals, any of which counts:
///   1. Explorer's ABN_FULLSCREENAPP notification (passed in via <see cref="ShellSignal"/>),
///   2. Windows' notification state (busy / Direct3D full screen / presentation mode),
///   3. the foreground window itself: caption-less and covering the whole monitor.
/// Event-driven: re-evaluated when the foreground window changes or moves/resizes; no polling.
/// </summary>
public sealed class FullscreenWatcher : IDisposable
{
    const int QUNS_BUSY = 2, QUNS_RUNNING_D3D_FULL_SCREEN = 3, QUNS_PRESENTATION_MODE = 4;

    readonly WinEventProc _foregroundProc, _locationProc;
    readonly DispatcherTimer _debounce = new() { Interval = TimeSpan.FromMilliseconds(90) };
    readonly uint _ownPid = (uint)Environment.ProcessId;
    IntPtr _foregroundHook, _locationHook;
    uint _locationPid;
    bool _shellSignal;

    public bool IsFullscreen { get; private set; }

    /// <summary>Raised on the UI thread when full-screen state flips.</summary>
    public event Action<bool>? Changed;

    public FullscreenWatcher()
    {
        _foregroundProc = (_, _, _, _, _, _, _) => OnForeground();
        // Only the foreground window's own moves matter (entering full screen resizes it).
        _locationProc = (_, _, hwnd, idObject, _, _, _) =>
        {
            if (idObject == 0 && hwnd == GetForegroundWindow()) Schedule();
        };
        _debounce.Tick += (_, _) => { _debounce.Stop(); Evaluate(); };
    }

    public void Start()
    {
        _foregroundHook = SetWinEventHook(EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_FOREGROUND, IntPtr.Zero,
            _foregroundProc, 0, 0, WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS);
        OnForeground();
    }

    void OnForeground()
    {
        // Listen for location changes from the foreground app's process only, so ordinary
        // window moves elsewhere (and the system cursor) never wake us.
        IntPtr fg = GetForegroundWindow();
        GetWindowThreadProcessId(fg, out uint pid);
        if (pid != _locationPid)
        {
            if (_locationHook != IntPtr.Zero) UnhookWinEvent(_locationHook);
            _locationHook = pid == 0 || pid == _ownPid ? IntPtr.Zero
                : SetWinEventHook(EVENT_OBJECT_LOCATIONCHANGE, EVENT_OBJECT_LOCATIONCHANGE, IntPtr.Zero,
                    _locationProc, pid, 0, WINEVENT_OUTOFCONTEXT);
            _locationPid = pid;
        }
        Schedule();
    }

    void Schedule() { _debounce.Stop(); _debounce.Start(); }

    /// <summary>Explorer's ABN_FULLSCREENAPP, forwarded by the dock's AppBar callback.</summary>
    public void ShellSignal(bool fullscreen)
    {
        _shellSignal = fullscreen;
        if (OnlyProcesses == null) { Evaluate(); return; }
        // Test path: drive the state directly (Explorer's signal doesn't exist on a test desktop).
        if (fullscreen == IsFullscreen) return;
        IsFullscreen = fullscreen;
        Changed?.Invoke(fullscreen);
    }

    /// <summary>
    /// UI-test isolation: when set, only these processes' windows count, and the session-wide
    /// notification state is ignored, so the user's real games/videos can't leak into a test.
    /// </summary>
    public HashSet<uint>? OnlyProcesses { get; set; }

    public void Evaluate()
    {
        IntPtr fg = GetForegroundWindow();
        GetWindowThreadProcessId(fg, out uint fgPid);
        bool now = OnlyProcesses is { } only
            ? only.Contains(fgPid) && IsFullscreenWindow(fg)
            : _shellSignal || NotificationStateIsFullscreen() || IsFullscreenWindow(fg, _ownPid);
        if (now == IsFullscreen) return;
        IsFullscreen = now;
        Log.Write(now ? "Full screen detected: dock stepping aside" : "Full screen ended: dock back");
        Changed?.Invoke(now);
    }

    static bool NotificationStateIsFullscreen() =>
        SHQueryUserNotificationState(out int state) == 0
        && state is QUNS_BUSY or QUNS_RUNNING_D3D_FULL_SCREEN or QUNS_PRESENTATION_MODE;

    /// <summary>
    /// A window is full screen when it covers its whole monitor (taskbar area included) without
    /// a caption. Maximised windows have a caption and stop at the work area, so they don't count.
    /// Only the primary monitor matters: that's where the dock lives.
    /// </summary>
    public static bool IsFullscreenWindow(IntPtr hwnd, uint ignorePid = 0)
    {
        if (hwnd == IntPtr.Zero || !IsWindowVisible(hwnd) || IsIconic(hwnd)) return false;
        GetWindowThreadProcessId(hwnd, out uint pid);
        if (ignorePid != 0 && pid == ignorePid) return false;
        string cls = Native.GetClassName(hwnd);
        if (cls is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd") return false;
        if ((GetWindowLongPtr(hwnd, GWL_STYLE).ToInt64() & WS_CAPTION) == WS_CAPTION) return false;
        if (!GetWindowRect(hwnd, out var r)) return false;
        var info = new MONITORINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<MONITORINFO>() };
        if (!GetMonitorInfoW(MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST), ref info)) return false;
        if ((info.dwFlags & MONITORINFOF_PRIMARY) == 0) return false;
        var m = info.rcMonitor;
        return r.Left <= m.Left && r.Top <= m.Top && r.Right >= m.Right && r.Bottom >= m.Bottom;
    }

    public void Dispose()
    {
        if (_foregroundHook != IntPtr.Zero) UnhookWinEvent(_foregroundHook);
        if (_locationHook != IntPtr.Zero) UnhookWinEvent(_locationHook);
        _debounce.Stop();
    }
}
