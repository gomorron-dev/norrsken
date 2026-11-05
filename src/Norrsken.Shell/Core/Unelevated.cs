using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;

namespace Norrsken.Shell.Core;

/// <summary>
/// Norrsken runs elevated (so it can manage every window, including admin ones, and change
/// system settings without prompts) but starts your apps as you, not as administrator: the
/// launch is handed to the desktop shell, the way Windows itself starts apps. Falls back to a
/// normal start if that isn't possible (then the app inherits Norrsken's rights).
/// </summary>
public static class Unelevated
{
    public static bool IsElevated { get; } = new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);

    [ComImport, Guid("85CB6900-4D95-11CF-960C-0080C7F4EE85"), InterfaceType(ComInterfaceType.InterfaceIsDual)]
    interface IShellWindows
    {
        int Count { get; }
        [return: MarshalAs(UnmanagedType.IDispatch)] object Item(object index);
        [return: MarshalAs(UnmanagedType.IUnknown)] object _NewEnum();
        int Register([MarshalAs(UnmanagedType.IDispatch)] object pid, int hwnd, int swClass);
        int RegisterPending(int lThreadId, ref object pvarloc, ref object pvarlocRoot, int swClass);
        void Revoke(int lCookie);
        void OnNavigate(int lCookie, ref object pvarLoc);
        void OnActivated(int lCookie, bool fActive);
        [return: MarshalAs(UnmanagedType.IDispatch)] object FindWindowSW(ref object pvarLoc, ref object pvarLocRoot, int swClass, out int pHWND, int swfwOptions);
    }

    [ComImport, Guid("6D5140C1-7436-11CE-8034-00AA006009FA"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IServiceProvider
    {
        [return: MarshalAs(UnmanagedType.IUnknown)] object QueryService(ref Guid service, ref Guid riid);
    }

    [ComImport, Guid("000214E2-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IShellBrowser
    {
        void GetWindow(); void ContextSensitiveHelp(); void InsertMenusSB(); void SetMenuSB(); void RemoveMenusSB();
        void SetStatusTextSB(); void EnableModelessSB(); void TranslateAcceleratorSB(); void BrowseObject();
        void GetViewStateStream(); void GetControlWindow(); void SendControlMsg();
        IShellView QueryActiveShellView();
    }

    [ComImport, Guid("000214E3-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IShellView
    {
        void GetWindow(); void ContextSensitiveHelp(); void TranslateAccelerator(); void EnableModeless(); void UIActivate();
        void Refresh(); void CreateViewWindow(); void DestroyViewWindow(); void GetCurrentInfo(); void AddPropertySheetPages();
        void SaveViewState(); void SelectItem();
        [return: MarshalAs(UnmanagedType.Interface)] object GetItemObject(uint item, ref Guid riid);
    }

    const int SWC_DESKTOP = 8, SWFO_NEEDDISPATCH = 1;
    static readonly Guid CLSID_ShellWindows = new("9BA05972-F6A8-11CF-A442-00A0C90A8F39");
    static readonly Guid SID_STopLevelBrowser = new("4C96BE40-915C-11CF-99D3-00AA004AE837");

    /// <summary>Starts a program, document, folder or URL as the signed-in user.</summary>
    public static void Start(string file, string args = "", string? workingDir = null)
    {
        if (App.TestMode) { App.TestLaunches.Add("start:" + file + (args.Length > 0 ? " " + args : "")); return; }
        // Whatever opens may come to the front, and its first window gets focus when it appears.
        Interop.Native.AllowAnyForeground();
        WindowTracker.ExpectNewWindow();
        if (IsElevated)
        {
            try { ViaShell(file, args, workingDir ?? ""); return; }
            catch (Exception ex) { Log.Write($"Unelevated start via the desktop failed ({ex.Message})"); }
            // Second route: start it with Explorer's own (non-admin) rights. Running it as administrator would
            // break things like Windows' "choose an app" picker, which shows up blank and ignores clicks.
            try { if (ViaExplorerToken(file, args, workingDir)) return; }
            catch (Exception ex) { Log.Write($"Unelevated start via Explorer's token failed ({ex.Message}); starting {file} normally"); }
        }
        Process.Start(new ProcessStartInfo(file, args) { UseShellExecute = true, WorkingDirectory = workingDir ?? "" });
    }

    /// <summary>"Run as administrator": elevated on purpose, and still brought to the front when it opens.</summary>
    public static void StartAdmin(string file, string args = "")
    {
        if (App.TestMode) { App.TestLaunches.Add("admin:" + file + (args.Length > 0 ? " " + args : "")); return; }
        Interop.Native.AllowAnyForeground();
        WindowTracker.ExpectNewWindow();
        try { Process.Start(new ProcessStartInfo(file, args) { UseShellExecute = true, Verb = "runas" }); }
        catch (System.ComponentModel.Win32Exception) { } // the admin prompt was declined
    }

    [DllImport("user32.dll")] static extern IntPtr GetShellWindow();
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("kernel32.dll", SetLastError = true)] static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
    [DllImport("advapi32.dll", SetLastError = true)] static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);
    [DllImport("advapi32.dll", SetLastError = true)] static extern bool DuplicateTokenEx(IntPtr token, uint access, IntPtr attrs, int level, int type, out IntPtr dup);
    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern bool CreateProcessWithTokenW(IntPtr token, uint logon, string? app, System.Text.StringBuilder cmd, uint flags, IntPtr env, string? dir, ref StartupInfo si, out ProcessInfo pi);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct StartupInfo { public int cb; public string? reserved, desktop, title; public int x, y, w, h, xc, yc, fill, flags; public short show, r2; public IntPtr r3, stdin, stdout, stderr; }
    [StructLayout(LayoutKind.Sequential)] struct ProcessInfo { public IntPtr process, thread; public int pid, tid; }

    /// <summary>
    /// Starts <paramref name="file"/> under the token of Explorer (the signed-in user, not elevated). Programs run
    /// directly; documents, folders and links are handed to explorer.exe, which opens them with their usual app.
    /// </summary>
    static bool ViaExplorerToken(string file, string args, string? dir)
    {
        IntPtr shell = GetShellWindow();
        uint pid = 0;
        if (shell != IntPtr.Zero) GetWindowThreadProcessId(shell, out pid);
        if (pid == 0) pid = (uint)(Process.GetProcessesByName("explorer").FirstOrDefault(p => p.SessionId == Process.GetCurrentProcess().SessionId)?.Id ?? 0);
        if (pid == 0) return false;
        IntPtr proc = OpenProcess(0x1000 /* QUERY_LIMITED_INFORMATION */, false, pid), token = IntPtr.Zero, dup = IntPtr.Zero;
        try
        {
            if (proc == IntPtr.Zero || !OpenProcessToken(proc, 0x000B /* ASSIGN_PRIMARY | DUPLICATE | QUERY */, out token)) return false;
            if (!DuplicateTokenEx(token, 0x02000000 /* MAXIMUM_ALLOWED */, IntPtr.Zero, 2 /* SecurityImpersonation */, 1 /* TokenPrimary */, out dup)) return false;
            bool program = file.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && File.Exists(file);
            string explorer = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");
            var cmd = new System.Text.StringBuilder(program ? $"\"{file}\" {args}" : $"\"{explorer}\" \"{file}\"");
            var si = new StartupInfo { cb = Marshal.SizeOf<StartupInfo>(), desktop = @"winsta0\default" };
            string? workDir = dir is { Length: > 0 } d && Directory.Exists(d) ? d : null;
            if (!CreateProcessWithTokenW(dup, 0, null, cmd, 0x00000400 /* CREATE_UNICODE_ENVIRONMENT */, IntPtr.Zero, workDir, ref si, out var pi)) return false;
            CloseHandle(pi.thread);
            CloseHandle(pi.process);
            return true;
        }
        finally
        {
            if (dup != IntPtr.Zero) CloseHandle(dup);
            if (token != IntPtr.Zero) CloseHandle(token);
            if (proc != IntPtr.Zero) CloseHandle(proc);
        }
    }

    static void ViaShell(string file, string args, string dir)
    {
        var windows = (IShellWindows)Activator.CreateInstance(Type.GetTypeFromCLSID(CLSID_ShellWindows)!)!;
        object loc = 0 /* CSIDL_DESKTOP */, root = Type.Missing;
        object desktop = windows.FindWindowSW(ref loc, ref root, SWC_DESKTOP, out _, SWFO_NEEDDISPATCH);
        var sid = SID_STopLevelBrowser;
        var iidBrowser = typeof(IShellBrowser).GUID;
        var browser = (IShellBrowser)((IServiceProvider)desktop).QueryService(ref sid, ref iidBrowser);
        var view = browser.QueryActiveShellView();
        var iidDispatch = new Guid("00020400-0000-0000-C000-000000000046");
        dynamic folderView = view.GetItemObject(0 /* SVGIO_BACKGROUND */, ref iidDispatch);
        dynamic shell = folderView.Application;
        shell.ShellExecute(file, args, dir, "open", 1);
    }
}
