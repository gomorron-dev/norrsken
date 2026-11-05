using System.Runtime.InteropServices;

namespace Norrsken.Shell.Interop;

/// <summary>DWM live thumbnails: the compositor draws another window's current contents into ours.</summary>
internal static class Dwm
{
    [StructLayout(LayoutKind.Sequential)]
    public struct SIZE { public int cx, cy; }

    [StructLayout(LayoutKind.Sequential)]
    public struct THUMBNAIL_PROPERTIES
    {
        public int dwFlags;
        public Native.RECT rcDestination;
        public Native.RECT rcSource;
        public byte opacity;
        [MarshalAs(UnmanagedType.Bool)] public bool fVisible;
        [MarshalAs(UnmanagedType.Bool)] public bool fSourceClientAreaOnly;
    }

    public const int TNP_RECTDESTINATION = 0x1, TNP_OPACITY = 0x4, TNP_VISIBLE = 0x8, TNP_SOURCECLIENTAREAONLY = 0x10;
    public const int DWMWA_BORDER_COLOR = 34;

    [DllImport("dwmapi.dll")] public static extern int DwmRegisterThumbnail(IntPtr dest, IntPtr src, out IntPtr thumb);
    [DllImport("dwmapi.dll")] public static extern int DwmUnregisterThumbnail(IntPtr thumb);
    [DllImport("dwmapi.dll")] public static extern int DwmUpdateThumbnailProperties(IntPtr thumb, ref THUMBNAIL_PROPERTIES props);
    [DllImport("dwmapi.dll")] public static extern int DwmQueryThumbnailSourceSize(IntPtr thumb, out SIZE size);
}
