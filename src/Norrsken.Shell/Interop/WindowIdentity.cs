using System.Runtime.InteropServices;
using System.Text;
using static Norrsken.Shell.Interop.Native;

namespace Norrsken.Shell.Interop;

/// <summary>
/// Works out which app a window really belongs to, so the dock can put a running window
/// under its pinned icon: explicit AppUserModelIDs (Chrome, Electron apps such as Claude/Discord),
/// packaged-app IDs (Terminal, Store apps) and UWP frames hosted by ApplicationFrameHost.
/// </summary>
internal static class WindowIdentity
{
    [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IPropertyStore
    {
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int GetAt(uint index, out PROPERTYKEY key);
        [PreserveSig] int GetValue(ref PROPERTYKEY key, out PROPVARIANT value);
        [PreserveSig] int SetValue(ref PROPERTYKEY key, ref PROPVARIANT value);
        [PreserveSig] int Commit();
    }

    [StructLayout(LayoutKind.Sequential)]
    struct PROPERTYKEY { public Guid fmtid; public uint pid; }

    [StructLayout(LayoutKind.Explicit, Size = 24)]
    struct PROPVARIANT { [FieldOffset(0)] public ushort vt; [FieldOffset(8)] public IntPtr p; }

    const ushort VT_LPWSTR = 31;
    static readonly Guid PKEY_AppUserModel = new("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3");

    [DllImport("shell32.dll")]
    static extern int SHGetPropertyStoreForWindow(IntPtr hwnd, ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out IPropertyStore store);
    [DllImport("ole32.dll")] static extern int PropVariantClear(ref PROPVARIANT pv);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    static extern int GetApplicationUserModelId(IntPtr hProcess, ref uint length, StringBuilder? aumid);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern IntPtr FindWindowExW(IntPtr parent, IntPtr after, string? cls, string? title);

    public readonly record struct Identity(uint ProcessId, string? Aumid, bool Packaged);

    public static Identity Resolve(IntPtr hwnd)
    {
        GetWindowThreadProcessId(hwnd, out uint pid);
        // UWP frames belong to ApplicationFrameHost; the app itself lives in a CoreWindow child.
        if (Native.GetClassName(hwnd) == "ApplicationFrameWindow")
        {
            IntPtr core = FindWindowExW(hwnd, IntPtr.Zero, "Windows.UI.Core.CoreWindow", null);
            if (core != IntPtr.Zero)
            {
                GetWindowThreadProcessId(core, out uint corePid);
                if (corePid != 0) pid = corePid;
            }
        }
        if (ExplicitAumid(hwnd) is { } explicitId) return new(pid, explicitId, false);
        return PackagedAumid(pid) is { } packagedId ? new(pid, packagedId, true) : new(pid, null, false);
    }

    static string? ExplicitAumid(IntPtr hwnd)
    {
        var iid = typeof(IPropertyStore).GUID;
        IPropertyStore? store = null;
        try
        {
            if (SHGetPropertyStoreForWindow(hwnd, ref iid, out store) != 0 || store == null) return null;
            var key = new PROPERTYKEY { fmtid = PKEY_AppUserModel, pid = 5 };
            if (store.GetValue(ref key, out var pv) != 0) return null;
            try { return pv.vt == VT_LPWSTR ? Marshal.PtrToStringUni(pv.p) : null; }
            finally { PropVariantClear(ref pv); }
        }
        catch (COMException) { return null; }
        finally { if (store != null) Marshal.ReleaseComObject(store); }
    }

    static string? PackagedAumid(uint pid)
    {
        IntPtr hp = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (hp == IntPtr.Zero) return null;
        try
        {
            uint len = 512;
            var sb = new StringBuilder((int)len);
            return GetApplicationUserModelId(hp, ref len, sb) == 0 ? sb.ToString() : null;
        }
        finally { CloseHandle(hp); }
    }
}
