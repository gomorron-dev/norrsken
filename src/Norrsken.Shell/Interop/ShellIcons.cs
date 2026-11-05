using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Norrsken.Shell.Interop;

/// <summary>Real icons for shell items (files, exes, shell:AppsFolder entries) via IShellItemImageFactory.</summary>
internal static class ShellIcons
{
    [StructLayout(LayoutKind.Sequential)]
    struct SIZE { public int cx, cy; public SIZE(int x, int y) { cx = x; cy = y; } }

    [StructLayout(LayoutKind.Sequential)]
    struct BITMAP { public int bmType, bmWidth, bmHeight, bmWidthBytes; public ushort bmPlanes, bmBitsPixel; public IntPtr bmBits; }

    [StructLayout(LayoutKind.Sequential)]
    struct BITMAPINFOHEADER
    {
        public uint biSize; public int biWidth, biHeight; public ushort biPlanes, biBitCount;
        public uint biCompression, biSizeImage; public int biXPelsPerMeter, biYPelsPerMeter;
        public uint biClrUsed, biClrImportant;
    }

    [ComImport, Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IShellItemImageFactory
    {
        [PreserveSig] int GetImage(SIZE size, int flags, out IntPtr phbm);
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
    static extern void SHCreateItemFromParsingName(string path, IntPtr pbc, [In] ref Guid riid,
        [MarshalAs(UnmanagedType.Interface)] out IShellItemImageFactory item);

    [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr h);
    [DllImport("gdi32.dll")] static extern int GetObjectW(IntPtr h, int size, out BITMAP bm);
    [DllImport("gdi32.dll")] static extern int GetDIBits(IntPtr hdc, IntPtr hbm, uint start, uint lines, byte[] bits, ref BITMAPINFOHEADER bmi, uint usage);
    [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr h);
    [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr h, IntPtr dc);

    const int SIIGBF_BIGGERSIZEOK = 0x1, SIIGBF_ICONONLY = 0x4;

    static readonly ConcurrentDictionary<string, ImageSource?> Cache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Dock icons render at 42 DIP and magnify to ~65 DIP (~98 px at 150 % scaling), so 96 px
    /// sources keep them sharp; the shell picks the best embedded size and downsamples.
    /// </summary>
    public const int DefaultSize = 96;

    static int _unframed;
    /// <summary>How many icons came back framed and were fetched at their real size instead.</summary>
    public static int Unframed => _unframed;

    public static ImageSource? Get(string parsingName, int size = DefaultSize) =>
        Cache.GetOrAdd(parsingName + "|" + size, _ => Load(parsingName, size));

    /// <summary>Uncached: a picture's thumbnail (or the file's icon when it has none).</summary>
    public static ImageSource? Thumbnail(string path, int size) => Load(path, size, thumbnail: true);

    static ImageSource? Load(string parsingName, int size, bool thumbnail = false)
    {
        IntPtr hbm = IntPtr.Zero;
        try
        {
            var iid = typeof(IShellItemImageFactory).GUID;
            SHCreateItemFromParsingName(parsingName, IntPtr.Zero, ref iid, out var factory);
            int hr = factory.GetImage(new SIZE(size, size), (thumbnail ? 0 : SIIGBF_ICONONLY) | SIIGBF_BIGGERSIZEOK, out hbm);
            if (hr != 0 || hbm == IntPtr.Zero) { Marshal.ReleaseComObject(factory); return null; }
            var img = FromHBitmap(hbm, out bool framed);
            // Apps that only ship small icons come back at large sizes inside a white "frame" that
            // Windows draws around them. Ask for the icon at its real size instead: just the icon.
            if (framed && !thumbnail && size > 48)
            {
                Interlocked.Increment(ref _unframed);
                foreach (int smaller in new[] { 48, 32 })
                {
                    DeleteObject(hbm);
                    hbm = IntPtr.Zero;
                    if (factory.GetImage(new SIZE(smaller, smaller), SIIGBF_ICONONLY, out hbm) != 0 || hbm == IntPtr.Zero) break;
                    img = FromHBitmap(hbm, out bool stillFramed, minSize: 32);
                    if (!stillFramed) break;
                }
            }
            Marshal.ReleaseComObject(factory);
            return img;
        }
        catch (Exception) { return null; }
        finally { if (hbm != IntPtr.Zero) DeleteObject(hbm); }
    }

    // CreateBitmapSourceFromHBitmap drops alpha, so read the pixels top-down ourselves.
    static BitmapSource? FromHBitmap(IntPtr hbm) => FromHBitmap(hbm, out _);

    /// <summary>
    /// The frame Windows draws around small icons scaled up: an opaque, light square well outside the
    /// icon itself. Real icons are transparent at those points (or not all four light and opaque).
    /// </summary>
    static bool IsFramed(byte[] px, int w, int h, int minSize = 64)
    {
        if (w < minSize || h < minSize) return false;
        // Dark mode: a thin grey outline right at the image edge, empty inside.
        bool Outline(int x, int y)
        {
            int i = (y * w + x) * 4;
            byte b = px[i], g = px[i + 1], r = px[i + 2], a = px[i + 3];
            return a > 150 && r > 40 && Math.Abs(r - g) < 18 && Math.Abs(g - b) < 18;
        }
        bool AnyOf(int x, int y, int dx, int dy) => Outline(x, y) || Outline(x + dx, y + dy);
        if (AnyOf(w / 2, 0, 0, 1) && AnyOf(w / 2, h - 1, 0, -1) && AnyOf(0, h / 2, 1, 0) && AnyOf(w - 1, h / 2, -1, 0)
            && AnyOf(w / 4, 0, 0, 1) && AnyOf(0, h / 4, 1, 0))
            return true;
        // Light mode: a light, opaque square around the icon.
        int inset = (int)(w * 0.13);
        (int X, int Y)[] probes = [(w / 2, inset), (w / 2, h - 1 - inset), (inset, h / 2), (w - 1 - inset, h / 2)];
        foreach (var (x, y) in probes)
        {
            int i = (y * w + x) * 4;
            byte b = px[i], g = px[i + 1], r = px[i + 2], a = px[i + 3];
            if (a < 200 || r < 190 || g < 190 || b < 190) return false;
        }
        return true;
    }

    static BitmapSource? FromHBitmap(IntPtr hbm, out bool framed, int minSize = 64)
    {
        framed = false;
        if (GetObjectW(hbm, Marshal.SizeOf<BITMAP>(), out var bm) == 0) return null;
        int w = bm.bmWidth, h = Math.Abs(bm.bmHeight);
        var bih = new BITMAPINFOHEADER
        {
            biSize = (uint)Marshal.SizeOf<BITMAPINFOHEADER>(), biWidth = w, biHeight = -h,
            biPlanes = 1, biBitCount = 32, biCompression = 0
        };
        var px = new byte[w * h * 4];
        IntPtr dc = GetDC(IntPtr.Zero);
        try { if (GetDIBits(dc, hbm, 0, (uint)h, px, ref bih, 0) == 0) return null; }
        finally { ReleaseDC(IntPtr.Zero, dc); }

        bool hasAlpha = false;
        for (int i = 3; i < px.Length; i += 4) if (px[i] != 0) { hasAlpha = true; break; }
        if (!hasAlpha) for (int i = 3; i < px.Length; i += 4) px[i] = 255;
        framed = hasAlpha && IsFramed(px, w, h, minSize);

        var src = BitmapSource.Create(w, h, 96, 96, PixelFormats.Pbgra32, null, px, w * 4);
        src.Freeze();
        return src;
    }
}
