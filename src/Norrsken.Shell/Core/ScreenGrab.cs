using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using static Norrsken.Shell.Interop.Native;

namespace Norrsken.Shell.Core;

/// <summary>Freezes the whole virtual screen (every monitor, physical pixels) and lists windows to snap to.</summary>
public static class ScreenGrab
{
    [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr h);
    [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr h, IntPtr dc);
    [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleBitmap(IntPtr dc, int w, int h);
    [DllImport("gdi32.dll")] static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
    [DllImport("gdi32.dll")] static extern bool BitBlt(IntPtr dst, int x, int y, int w, int h, IntPtr src, int sx, int sy, uint rop);
    [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr h);
    [DllImport("gdi32.dll")] static extern bool DeleteDC(IntPtr dc);
    [DllImport("gdi32.dll")] static extern int GetDIBits(IntPtr dc, IntPtr bmp, uint start, uint lines, byte[] bits, ref BITMAPINFOHEADER bmi, uint usage);
    [DllImport("dwmapi.dll", EntryPoint = "DwmGetWindowAttribute")] static extern int DwmGetRect(IntPtr h, int attr, out RECT r, int size);
    [DllImport("user32.dll")] static extern IntPtr MonitorFromPoint(POINT p, uint flags);
    [DllImport("user32.dll")] static extern bool GetCursorPos(out POINT p);

    [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    struct BITMAPINFOHEADER
    {
        public uint biSize; public int biWidth, biHeight; public ushort biPlanes, biBitCount;
        public uint biCompression, biSizeImage; public int biXPelsPerMeter, biYPelsPerMeter; public uint biClrUsed, biClrImportant;
    }

    const uint SRCCOPY = 0x00CC0020, CAPTUREBLT = 0x40000000;

    /// <summary>The virtual screen in physical pixels (may start at negative coordinates).</summary>
    public static Int32Rect VirtualScreen => new(GetSystemMetrics(76), GetSystemMetrics(77), GetSystemMetrics(78), GetSystemMetrics(79));

    public static BitmapSource Grab(Int32Rect area)
    {
        IntPtr screen = GetDC(IntPtr.Zero), mem = CreateCompatibleDC(screen), bmp = CreateCompatibleBitmap(screen, area.Width, area.Height);
        IntPtr old = SelectObject(mem, bmp);
        try
        {
            BitBlt(mem, 0, 0, area.Width, area.Height, screen, area.X, area.Y, SRCCOPY | CAPTUREBLT);
            SelectObject(mem, old);
            var bih = new BITMAPINFOHEADER { biSize = (uint)Marshal.SizeOf<BITMAPINFOHEADER>(), biWidth = area.Width, biHeight = -area.Height, biPlanes = 1, biBitCount = 32 };
            var px = new byte[area.Width * area.Height * 4];
            GetDIBits(screen, bmp, 0, (uint)area.Height, px, ref bih, 0);
            for (int i = 3; i < px.Length; i += 4) px[i] = 255;
            var src = BitmapSource.Create(area.Width, area.Height, 96, 96, PixelFormats.Bgra32, null, px, area.Width * 4);
            src.Freeze();
            return src;
        }
        finally { DeleteObject(bmp); DeleteDC(mem); ReleaseDC(IntPtr.Zero, screen); }
    }

    /// <summary>Visible top-level windows front to back, with their visible frame (no invisible resize borders).</summary>
    public static List<Int32Rect> Windows(IntPtr exclude)
    {
        var list = new List<Int32Rect>();
        EnumWindows((h, _) =>
        {
            if (h == exclude || !IsWindowVisible(h) || IsIconic(h)) return true;
            if (DwmGetWindowAttribute(h, DWMWA_CLOAKED, out int cloaked, 4) == 0 && cloaked != 0) return true;
            if (DwmGetRect(h, 9 /* EXTENDED_FRAME_BOUNDS */, out RECT r, Marshal.SizeOf<RECT>()) != 0 && !GetWindowRect(h, out r)) return true;
            int w = r.Right - r.Left, ht = r.Bottom - r.Top;
            if (w < 24 || ht < 24) return true;
            string cls = GetClassName(h);
            if (cls is "Progman" or "WorkerW") { list.Add(new(r.Left, r.Top, w, ht)); return true; }
            if ((GetWindowLongPtr(h, GWL_EXSTYLE).ToInt64() & WS_EX_TOOLWINDOW) != 0 && GetWindowText(h).Length == 0) return true;
            list.Add(new(r.Left, r.Top, w, ht));
            return true;
        }, IntPtr.Zero);
        return list;
    }

    /// <summary>The monitor under the pointer, in physical pixels.</summary>
    public static Int32Rect MonitorAt(int x, int y)
    {
        var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        GetMonitorInfoW(MonitorFromPoint(new POINT { X = x, Y = y }, MONITOR_DEFAULTTONEAREST), ref mi);
        return new(mi.rcMonitor.Left, mi.rcMonitor.Top, mi.rcMonitor.Right - mi.rcMonitor.Left, mi.rcMonitor.Bottom - mi.rcMonitor.Top);
    }

    public static POINT Cursor() { GetCursorPos(out var p); return p; }
}
