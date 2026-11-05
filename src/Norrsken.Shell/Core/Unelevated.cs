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
        if (IsElevated)
        {
            try { ViaShell(file, args, workingDir ?? ""); return; }
            catch (Exception ex) { Log.Write($"Unelevated start failed ({ex.Message}); starting {file} normally"); }
        }
        Process.Start(new ProcessStartInfo(file, args) { UseShellExecute = true, WorkingDirectory = workingDir ?? "" });
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
