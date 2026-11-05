using System.Runtime.InteropServices;
using System.Windows.Threading;
using static Norrsken.Shell.Interop.Native;

namespace Norrsken.Shell.Core;

/// <summary>
/// Right-clicks on the desktop open Norrsken's menu instead of Explorer's. A low-level mouse hook on
/// its own thread; every other click (and every click anywhere else) passes straight through.
/// </summary>
public static class MouseHook
{
    const int WH_MOUSE_LL = 14, WM_RBUTTONDOWN = 0x204, WM_RBUTTONUP = 0x205;
    [StructLayout(LayoutKind.Sequential)] struct MSLLHOOKSTRUCT { public int X, Y; public uint mouseData, flags, time; public IntPtr extra; }
    [StructLayout(LayoutKind.Sequential)] struct POINT { public int X, Y; }
    delegate IntPtr HookProc(int code, IntPtr w, IntPtr l);
    [DllImport("user32.dll", SetLastError = true)] static extern IntPtr SetWindowsHookExW(int id, HookProc proc, IntPtr mod, uint thread);
    [DllImport("user32.dll")] static extern IntPtr CallNextHookEx(IntPtr h, int code, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] static extern int GetMessageW(out MSG m, IntPtr h, uint min, uint max);
    [DllImport("user32.dll")] static extern IntPtr WindowFromPoint(POINT p);
    [DllImport("kernel32.dll")] static extern IntPtr GetModuleHandleW(string? name);
    [StructLayout(LayoutKind.Sequential)] struct MSG { public IntPtr hwnd; public uint message; public IntPtr w, l; public uint time; public int x, y; }

    static HookProc? _proc;
    static IntPtr _hook;
    static Dispatcher? _ui;
    static bool _swallowingRight;

    /// <summary>Raised on the UI thread with the screen point (physical pixels) of a desktop right-click.</summary>
    public static event Action<int, int>? DesktopRightClick;

    public static void Start()
    {
        if (_hook != IntPtr.Zero || App.TestMode) return;
        _ui = Dispatcher.CurrentDispatcher;
        new Thread(() =>
        {
            _proc = Proc;
            _hook = SetWindowsHookExW(WH_MOUSE_LL, _proc, GetModuleHandleW(null), 0);
            while (GetMessageW(out _, IntPtr.Zero, 0, 0) > 0) { }
        }) { IsBackground = true, Name = "Norrsken mouse", Priority = ThreadPriority.AboveNormal }.Start();
    }

    /// <summary>The desktop's icon view (Explorer draws it inside Progman or WorkerW).</summary>
    public static bool IsDesktop(IntPtr h)
    {
        for (int i = 0; i < 4 && h != IntPtr.Zero; i++, h = GetAncestor(h, 1 /* GA_PARENT */))
        {
            string cls = GetClassName(h);
            if (cls is "Progman" or "WorkerW") return true;
            if (cls is not ("SysListView32" or "SHELLDLL_DefView")) return false;
        }
        return false;
    }

    static IntPtr Proc(int code, IntPtr w, IntPtr l)
    {
        if (code >= 0 && App.Settings.ReplaceSystemApps && App.Settings.DesktopMenu)
        {
            int msg = (int)w;
            if (msg is WM_RBUTTONDOWN or WM_RBUTTONUP)
            {
                var m = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(l);
                if ((m.flags & 1) == 0) // not injected
                {
                    if (msg == WM_RBUTTONDOWN && IsDesktop(WindowFromPoint(new POINT { X = m.X, Y = m.Y }))) { _swallowingRight = true; return 1; }
                    if (msg == WM_RBUTTONUP && _swallowingRight)
                    {
                        _swallowingRight = false;
                        int x = m.X, y = m.Y;
                        _ui?.BeginInvoke(() => DesktopRightClick?.Invoke(x, y));
                        return 1;
                    }
                }
            }
        }
        return CallNextHookEx(_hook, code, w, l);
    }
}
