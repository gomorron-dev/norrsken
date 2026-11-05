using System.Runtime.InteropServices;
using System.Windows.Threading;

namespace Norrsken.Shell.Core;

/// <summary>
/// Routes the Windows shortcuts for apps Norrsken replaces to Norrsken's own apps:
/// PrtScn and Win+Shift+S → Screenshot, Win+E → Files, Win+I → Settings.
/// A low-level keyboard hook on its own thread (so a busy UI can never delay typing); every
/// other key passes straight through. Only these exact combinations are taken.
/// </summary>
public static class KeyboardHook
{
    const int WH_KEYBOARD_LL = 13, WM_KEYDOWN = 0x100, WM_KEYUP = 0x101, WM_SYSKEYDOWN = 0x104, WM_SYSKEYUP = 0x105;
    const uint LLKHF_INJECTED = 0x10;
    const int VK_LWIN = 0x5B, VK_RWIN = 0x5C, VK_SNAPSHOT = 0x2C, VK_SHIFT = 0x10, VK_CONTROL = 0x11, VK_MENU = 0x12;
    const ushort VK_MASK = 0xE8; // unassigned: tells Windows "another key was pressed" so Start doesn't open on Win release

    [StructLayout(LayoutKind.Sequential)]
    struct KBDLLHOOKSTRUCT { public uint vkCode, scanCode, flags, time; public IntPtr extra; }

    [StructLayout(LayoutKind.Sequential)]
    struct KEYBDINPUT { public ushort wVk, wScan; public uint dwFlags, time; public IntPtr extra; }

    [StructLayout(LayoutKind.Explicit, Size = 40)]
    struct INPUT { [FieldOffset(0)] public uint type; [FieldOffset(8)] public KEYBDINPUT ki; }

    delegate IntPtr HookProc(int code, IntPtr w, IntPtr l);
    [DllImport("user32.dll", SetLastError = true)] static extern IntPtr SetWindowsHookExW(int id, HookProc proc, IntPtr mod, uint thread);
    [DllImport("user32.dll")] static extern bool UnhookWindowsHookEx(IntPtr h);
    [DllImport("user32.dll")] static extern IntPtr CallNextHookEx(IntPtr h, int code, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] static extern short GetAsyncKeyState(int vk);
    [DllImport("user32.dll")] static extern uint SendInput(uint n, INPUT[] inputs, int size);
    [DllImport("user32.dll")] static extern int GetMessageW(out MSG m, IntPtr h, uint min, uint max);
    [DllImport("user32.dll")] static extern bool PostThreadMessageW(uint thread, uint msg, IntPtr w, IntPtr l);
    [DllImport("kernel32.dll")] static extern IntPtr GetModuleHandleW(string? name);
    [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();

    [StructLayout(LayoutKind.Sequential)]
    struct MSG { public IntPtr hwnd; public uint message; public IntPtr w, l; public uint time; public int x, y; }

    static HookProc? _proc;
    static IntPtr _hook;
    static uint _threadId;
    static Dispatcher? _ui;
    static bool _lwin, _rwin, _winAlone, _altTab;
    static readonly HashSet<uint> SwallowUp = new();

    /// <summary>"capture", "files" or "settings", raised on the UI thread.</summary>
    public static event Action<string>? Pressed;

    public static bool Running => _hook != IntPtr.Zero;

    /// <summary>Norrsken Capture on a normal Windows PC: only the screenshot keys are taken.</summary>
    public static bool CaptureOnly;

    public static void Start()
    {
        if (_threadId != 0 || App.TestMode) return;
        _ui = Dispatcher.CurrentDispatcher;
        var ready = new ManualResetEventSlim();
        var t = new Thread(() =>
        {
            _threadId = GetCurrentThreadId();
            _proc = Proc;
            _hook = SetWindowsHookExW(WH_KEYBOARD_LL, _proc, GetModuleHandleW(null), 0);
            if (_hook == IntPtr.Zero) Log.Write("Keyboard hook unavailable: " + Marshal.GetLastWin32Error());
            ready.Set();
            while (GetMessageW(out _, IntPtr.Zero, 0, 0) > 0) { }
            if (_hook != IntPtr.Zero) UnhookWindowsHookEx(_hook);
            _hook = IntPtr.Zero;
        }) { IsBackground = true, Name = "Norrsken keyboard", Priority = ThreadPriority.AboveNormal };
        t.Start();
        ready.Wait(2000);
        Log.Write(Running ? "Keyboard shortcuts: PrtScn, Win+Shift+S, Win+E, Win+I → Norrsken" : "Keyboard shortcuts left to Windows");
    }

    public static void Stop()
    {
        if (_threadId != 0) PostThreadMessageW(_threadId, 0x0012 /* WM_QUIT */, IntPtr.Zero, IntPtr.Zero);
        _threadId = 0;
    }

    static bool Down(int vk) => (GetAsyncKeyState(vk) & 0x8000) != 0;

    static IntPtr Proc(int code, IntPtr w, IntPtr l)
    {
        if (code >= 0)
        {
            var k = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(l);
            int msg = (int)w;
            bool down = msg is WM_KEYDOWN or WM_SYSKEYDOWN, up = msg is WM_KEYUP or WM_SYSKEYUP;
            if ((k.flags & LLKHF_INJECTED) == 0)
            {
                bool isWin = k.vkCode is VK_LWIN or VK_RWIN;
                if (isWin && down && !_lwin && !_rwin) _winAlone = true;
                else if (down && !isWin) _winAlone = false;
                if (k.vkCode == VK_LWIN) _lwin = down || (!up && _lwin);
                if (k.vkCode == VK_RWIN) _rwin = down || (!up && _rwin);
                // A tap of the Windows key on its own opens Norrsken's launcher instead of Start.
                if (isWin && up && _winAlone && !_lwin && !_rwin && !CaptureOnly && App.Settings.ReplaceSystemApps && App.Settings.WinKeyOpensLauncher)
                {
                    _winAlone = false;
                    SendMask(); // Windows now sees Win+(key) and leaves Start closed
                    _ui?.BeginInvoke(() => Pressed?.Invoke("launcher"));
                    return CallNextHookEx(_hook, code, w, l);
                }
                // Norrsken Alt+Tab: Tab while Alt is held drives the switcher; letting go of Alt switches.
                bool isAlt = k.vkCode is VK_MENU or 0xA4 or 0xA5;
                if (_altTab && up && isAlt)
                {
                    _altTab = false;
                    _ui?.BeginInvoke(() => Pressed?.Invoke("alttab-commit"));
                }
                if (down && !CaptureOnly && App.Settings.ReplaceSystemApps && App.Settings.NorrskenAltTab)
                {
                    bool altHeld = Down(VK_MENU), ctrlHeld = Down(VK_CONTROL), winHeld = _lwin || _rwin;
                    if (k.vkCode == 0x09 && altHeld && !ctrlHeld && !winHeld)
                    {
                        if (!_altTab) SendMask(); // releasing Alt later won't open the app's menu bar
                        _altTab = true;
                        SwallowUp.Add(k.vkCode);
                        bool back = Down(VK_SHIFT);
                        _ui?.BeginInvoke(() => Pressed?.Invoke(back ? "alttab-back" : "alttab"));
                        return 1;
                    }
                    if (_altTab && k.vkCode is 0x1B or 0x25 or 0x27 or 0x26 or 0x28 or 0x0D or 0x2E)
                    {
                        string a = k.vkCode switch { 0x2E => "alttab-close", 0x1B => "alttab-cancel", 0x25 or 0x26 => "alttab-left", 0x0D => "alttab-commit", _ => "alttab-right" };
                        if (k.vkCode is 0x1B or 0x0D) _altTab = false;
                        SwallowUp.Add(k.vkCode);
                        _ui?.BeginInvoke(() => Pressed?.Invoke(a));
                        return 1;
                    }
                }
                // While the window overview (Win+Tab) is open, arrows, Tab, Enter and Esc drive it.
                if (down && OverviewOpen && k.vkCode == 0x2E) { SwallowUp.Add(k.vkCode); _ui?.BeginInvoke(() => Pressed?.Invoke("alttab-close")); return 1; }
                if (down && OverviewOpen && k.vkCode is 0x1B or 0x25 or 0x26 or 0x27 or 0x28 or 0x0D or 0x09)
                {
                    if (k.vkCode == 0x09 && (_lwin || _rwin)) { SwallowUp.Add(k.vkCode); _ui?.BeginInvoke(() => Pressed?.Invoke("overview")); return 1; } // Win+Tab again closes it
                    string a = k.vkCode switch { 0x1B => "overview-cancel", 0x0D => "overview-commit", 0x25 or 0x26 => "overview-left", _ => "overview-right" };
                    SwallowUp.Add(k.vkCode);
                    _ui?.BeginInvoke(() => Pressed?.Invoke(a));
                    return 1;
                }
                if (up && SwallowUp.Remove(k.vkCode)) return 1;
                if (down)
                {
                    if (SwallowUp.Contains(k.vkCode)) return 1; // auto-repeat of a key we took
                    bool win = _lwin || _rwin, shift = Down(VK_SHIFT), ctrl = Down(VK_CONTROL), alt = Down(VK_MENU);
                    string? action = Map(k.vkCode, win, shift, ctrl, alt);
                    if (action != null && (CaptureOnly ? action.StartsWith("capture", StringComparison.Ordinal) : App.Settings.ReplaceSystemApps))
                    {
                        SwallowUp.Add(k.vkCode);
                        SendMask(); // also lets Norrsken bring its window to the front
                        _ui?.BeginInvoke(() => Pressed?.Invoke(action));
                        return 1;
                    }
                }
            }
        }
        return CallNextHookEx(_hook, code, w, l);
    }

    /// <summary>Set by the window overview while it's open.</summary>
    public static volatile bool OverviewOpen;

    /// <summary>
    /// Every Windows shortcut that opens a piece of Windows UI, and the Norrsken part that replaces it.
    /// Shortcuts that act on windows (Win+D, Win+M, snapping, virtual desktops) stay Windows' own.
    /// </summary>
    public static readonly (string Keys, string Opens)[] Shortcuts =
    [
        ("Win", "Launcher"), ("Win+S / Win+Q", "Launcher search"), ("Win+E", "Files"), ("Win+I", "Settings"),
        ("Win+N / Win+W", "Today: notifications and calendar"), ("Win+A / Win+B", "Quick settings"), ("Win+R", "Run"),
        ("Win+X", "Power menu"), ("Win+V", "Clipboard history"), ("Win+Tab", "Window overview"), ("Alt+Tab", "Switch windows"),
        ("Win+P", "Display mode"), ("Win+1…9", "Dock apps"), ("Win+T", "Show the dock"), ("Win+Pause", "About this PC"),
        ("Win+C", "Claude"), ("Ctrl+Shift+Esc", "Task Manager"), ("PrtScn / Win+Shift+S", "Screenshot"),
        ("Win+PrtScn", "Save the whole screen"), ("Alt+PrtScn", "Capture the active window"), ("Alt+F4 on the desktop", "Power options"),
    ];

    static string? Map(uint vk, bool win, bool shift, bool ctrl, bool alt)
    {
        bool only(bool w, bool s, bool c, bool a) => win == w && shift == s && ctrl == c && alt == a;
        if (vk == VK_SNAPSHOT) return only(false, false, false, false) ? "capture" : only(true, false, false, false) ? "capture-screen" : only(false, false, false, true) ? "capture-window" : null;
        if (vk == 0x1B && only(false, true, true, false)) return "taskmgr";
        if (vk == 0x73 && only(false, false, false, true) && DesktopIsForeground()) return "power";
        if (only(true, true, false, false) && vk == 'S') return "capture";
        if (!only(true, false, false, false)) return null;
        return vk switch
        {
            'E' => "files", 'I' => "settings", 'N' or 'W' => "notifications", 'R' => "run", 'X' => "powermenu",
            'A' or 'B' => "quick", 'S' or 'Q' => "search", 'V' => "clipboard", 0x09 => "overview", 'P' => "project",
            'T' => "dock", 0x13 => "about", 'C' => "claude",
            >= '1' and <= '9' => "dock:" + (char)vk,
            _ => null,
        };
    }

    static bool DesktopIsForeground()
    {
        var fg = Interop.Native.GetForegroundWindow();
        return fg == IntPtr.Zero || Interop.Native.GetClassName(fg) is "Progman" or "WorkerW" or "Shell_TrayWnd";
    }

    static void SendMask()
    {
        var inputs = new INPUT[2];
        inputs[0].type = inputs[1].type = 1;
        inputs[0].ki.wVk = inputs[1].ki.wVk = VK_MASK;
        inputs[1].ki.dwFlags = 2; // KEYEVENTF_KEYUP
        SendInput(2, inputs, Marshal.SizeOf<INPUT>());
    }
}
