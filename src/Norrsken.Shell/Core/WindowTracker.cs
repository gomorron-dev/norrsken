using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Windows.Media;
using System.Windows.Threading;
using Norrsken.Shell.Interop;
using static Norrsken.Shell.Interop.Native;

namespace Norrsken.Shell.Core;

public sealed class TrackedWindow(IntPtr handle) : INotifyPropertyChanged
{
    public IntPtr Handle { get; } = handle;

    string _title = "";
    public string Title { get => _title; set => Set(ref _title, value); }

    ImageSource? _icon;
    public ImageSource? Icon { get => _icon; set => Set(ref _icon, value); }

    bool _isActive;
    public bool IsActive { get => _isActive; set => Set(ref _isActive, value); }

    bool _isMinimized;
    public bool IsMinimized { get => _isMinimized; set => Set(ref _isMinimized, value); }

    bool _needsAttention;
    /// <summary>The app flashed its taskbar button (e.g. a new message); cleared once it's in front.</summary>
    public bool NeedsAttention { get => _needsAttention; set => Set(ref _needsAttention, value); }

    public string? ExePath { get; init; }
    public string? Aumid { get; init; }
    /// <summary>Human name of the owning program (file description), used when no app entry matches.</summary>
    public string AppName { get; init; } = "";
    public DateTime LastActivated { get; set; }

    /// <summary>Windows of one program share a dock icon; UWP frames without an identity stay separate.</summary>
    public string GroupKey =>
        Aumid != null ? "aumid:" + Aumid
        : ExePath == null || ExePath.EndsWith("ApplicationFrameHost.exe", StringComparison.OrdinalIgnoreCase) ? "hwnd:" + Handle
        : "exe:" + ExePath.ToLowerInvariant();

    public event PropertyChangedEventHandler? PropertyChanged;

    void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}

/// <summary>
/// Tracks top-level app windows the way a taskbar does, driven by WinEvent hooks
/// (no polling). Only events that can change the set of app windows trigger a rescan.
/// </summary>
public sealed class WindowTracker : IDisposable
{
    static readonly HashSet<string> IgnoredClasses = new(StringComparer.Ordinal)
    {
        "Progman", "WorkerW", "Shell_TrayWnd", "Shell_SecondaryTrayWnd", "Windows.UI.Core.CoreWindow",
    };

    static readonly Dictionary<string, string> Descriptions = new(StringComparer.OrdinalIgnoreCase);

    public ObservableCollection<TrackedWindow> Windows { get; } = new();

    readonly WinEventProc _proc;
    readonly List<IntPtr> _hooks = new();
    readonly DispatcherTimer _debounce;
    readonly uint _ownPid = (uint)Environment.ProcessId;

    public WindowTracker()
    {
        _proc = OnWinEvent; // keep the delegate rooted for the lifetime of the hooks
        _debounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        _debounce.Tick += (_, _) => { _debounce.Stop(); Refresh(); };
    }

    public void Start()
    {
        Hook(EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_FOREGROUND);
        Hook(EVENT_SYSTEM_MINIMIZESTART, EVENT_SYSTEM_MINIMIZEEND);
        Hook(EVENT_OBJECT_CREATE, EVENT_OBJECT_HIDE); // create, destroy, show, hide
        Hook(EVENT_OBJECT_NAMECHANGE, EVENT_OBJECT_NAMECHANGE);
        Hook(EVENT_OBJECT_CLOAKED, EVENT_OBJECT_UNCLOAKED);
        Refresh();
    }

    void Hook(uint min, uint max)
    {
        IntPtr h = SetWinEventHook(min, max, IntPtr.Zero, _proc, 0, 0, WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS);
        if (h != IntPtr.Zero) _hooks.Add(h);
        else Log.Write($"SetWinEventHook {min:X}-{max:X} failed");
    }

    // Most WinEvents are noise for a dock; only rescan when an app window can have
    // appeared or disappeared. Everything else is a cheap in-place update.
    void OnWinEvent(IntPtr hook, uint evt, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
    {
        if (idObject != 0 || idChild != 0 || hwnd == IntPtr.Zero) return; // OBJID_WINDOW only
        TrackedWindow? tracked = Windows.FirstOrDefault(w => w.Handle == hwnd);
        switch (evt)
        {
            case EVENT_SYSTEM_FOREGROUND:
            case EVENT_SYSTEM_MINIMIZESTART:
            case EVENT_SYSTEM_MINIMIZEEND:
                UpdateStates();
                if (tracked == null && IsTaskbarWindow(hwnd, _ownPid)) ScheduleRefresh();
                break;
            case EVENT_OBJECT_NAMECHANGE:
                if (tracked != null) tracked.Title = Native.GetWindowText(hwnd);
                else if (IsTaskbarWindow(hwnd, _ownPid)) ScheduleRefresh();
                break;
            case EVENT_OBJECT_CREATE:
                break; // windows are shown (EVENT_OBJECT_SHOW) before they matter
            default: // destroy, show, hide, cloak, uncloak
                if (tracked != null || (GetAncestor(hwnd, GA_ROOT) == hwnd && IsTaskbarWindow(hwnd, _ownPid)))
                    ScheduleRefresh();
                break;
        }
    }

    void ScheduleRefresh() { if (!_debounce.IsEnabled) _debounce.Start(); }

    void UpdateStates()
    {
        IntPtr fg = GetForegroundWindow();
        foreach (var w in Windows)
        {
            w.IsMinimized = IsIconic(w.Handle);
            bool active = w.Handle == fg && !w.IsMinimized;
            if (active && !w.IsActive) w.LastActivated = DateTime.UtcNow;
            w.IsActive = active;
            if (active) w.NeedsAttention = false;
        }
    }

    /// <summary>A window flashed (shell hook HSHELL_FLASH): mark it, unless it's already in front.</summary>
    public void SetAttention(IntPtr hwnd)
    {
        var w = Windows.FirstOrDefault(x => x.Handle == hwnd);
        if (w != null && !w.IsActive) w.NeedsAttention = true;
    }

    public static bool IsTaskbarWindow(IntPtr h, uint ownPid)
    {
        if (!IsWindowVisible(h)) return false;
        long ex = GetWindowLongPtr(h, GWL_EXSTYLE).ToInt64();
        bool appWindow = (ex & WS_EX_APPWINDOW) != 0;
        if (!appWindow)
        {
            if (GetWindow(h, GW_OWNER) != IntPtr.Zero) return false;
            if ((ex & WS_EX_TOOLWINDOW) != 0 || (ex & WS_EX_NOACTIVATE) != 0) return false;
        }
        if (DwmGetWindowAttribute(h, DWMWA_CLOAKED, out int cloaked, sizeof(int)) == 0 && cloaked != 0) return false;
        if (GetWindowTextLengthW(h) == 0) return false;
        GetWindowThreadProcessId(h, out uint pid);
        if (pid == ownPid) return false;
        return !IgnoredClasses.Contains(Native.GetClassName(h));
    }

    public void Refresh()
    {
        var current = new List<IntPtr>();
        EnumWindows((h, _) => { if (IsTaskbarWindow(h, _ownPid)) current.Add(h); return true; }, IntPtr.Zero);
        var alive = new HashSet<IntPtr>(current);

        for (int i = Windows.Count - 1; i >= 0; i--)
            if (!alive.Contains(Windows[i].Handle)) Windows.RemoveAt(i);

        var known = Windows.Select(w => w.Handle).ToHashSet();
        // EnumWindows is top-down z-order; reverse so the oldest windows land first.
        for (int i = current.Count - 1; i >= 0; i--)
        {
            IntPtr h = current[i];
            if (known.Contains(h)) continue;
            var id = WindowIdentity.Resolve(h);
            string? exe = GetProcessPath(id.ProcessId);
            var w = new TrackedWindow(h) { ExePath = exe, Aumid = id.Aumid, AppName = Describe(exe) };
            w.Icon = (id.Packaged ? ShellIcons.Get(@"shell:AppsFolder\" + id.Aumid) : null)
                     ?? (exe != null ? ShellIcons.Get(exe) : null);
            Windows.Add(w);
        }

        foreach (var w in Windows) w.Title = Native.GetWindowText(w.Handle);
        UpdateStates();
    }

    static string Describe(string? exe)
    {
        if (exe == null) return "";
        if (Descriptions.TryGetValue(exe, out var d)) return d;
        try { d = FileVersionInfo.GetVersionInfo(exe).FileDescription ?? ""; } catch (Exception) { d = ""; }
        if (string.IsNullOrWhiteSpace(d)) d = Path.GetFileNameWithoutExtension(exe);
        return Descriptions[exe] = d.Trim();
    }

    /// <summary>Dock click semantics: activate, restore if minimised, minimise if already in front.</summary>
    public void Toggle(TrackedWindow w)
    {
        if (!IsWindow(w.Handle)) { Refresh(); return; }
        if (GetForegroundWindow() == w.Handle && !IsIconic(w.Handle)) ShowWindow(w.Handle, SW_MINIMIZE);
        else Activate(w);
    }

    /// <summary>Bring to front without changing maximised/normal state.</summary>
    public static void Activate(TrackedWindow w)
    {
        if (IsIconic(w.Handle)) ShowWindow(w.Handle, SW_RESTORE);
        SetForegroundWindow(w.Handle);
    }

    public static void Close(TrackedWindow w) => PostMessageW(w.Handle, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);

    public void Dispose()
    {
        foreach (var h in _hooks) UnhookWinEvent(h);
        _hooks.Clear();
        _debounce.Stop();
    }
}
