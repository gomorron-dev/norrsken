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
    /// Dock icons render at 42 DIP and magnify to ~65 DIP: 130 px at 200 % scaling, so 128 px sources stay sharp
    /// on high-DPI screens too. Big previews ask for <see cref="LargeSize"/>.
    /// </summary>
    public const int DefaultSize = 128;

    /// <summary>For icons shown large (previews): Windows' 256 px jumbo icon when the app ships one.</summary>
    public const int LargeSize = 256;

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
                bool clean = false;
                foreach (int smaller in new[] { 48, 32 })
                {
                    DeleteObject(hbm);
                    hbm = IntPtr.Zero;
                    if (factory.GetImage(new SIZE(smaller, smaller), SIIGBF_ICONONLY, out hbm) != 0 || hbm == IntPtr.Zero) break;
                    img = FromHBitmap(hbm, out bool stillFramed, minSize: 32);
                    if (!stillFramed) { clean = true; break; }
                }
                // Framed at every size: take the big one and cut the frame away, keeping only the icon.
                if (!clean)
                {
                    if (hbm != IntPtr.Zero) DeleteObject(hbm);
                    hbm = IntPtr.Zero;
                    if (factory.GetImage(new SIZE(size, size), SIIGBF_ICONONLY | SIIGBF_BIGGERSIZEOK, out hbm) == 0 && hbm != IntPtr.Zero)
                        img = FromHBitmap(hbm, out _, unframe: true) ?? img;
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
        // A faint square line around the icon (at the edge or a few pixels in), empty or different inside.
        if (SquareLine(px, w, h)) return true;
        // Light mode: a light, opaque square around the icon.
        int inset = (int)(w * 0.13);
        (int X, int Y)[] probes = [(w / 2, inset), (w / 2, h - 1 - inset), (inset, h / 2), (w - 1 - inset, h / 2)];
        foreach (var (x, y) in probes)
        {
            var c = Px(px, (y * w + x) * 4);
            if (c.A < 200 || c.R < 190 || c.G < 190 || c.B < 190) return false;
        }
        return true;
    }

    /// <summary>A pixel's real colour (the bitmap stores it multiplied by its alpha).</summary>
    static (int R, int G, int B, int A) Px(byte[] px, int i)
    {
        int a = px[i + 3];
        if (a == 0) return (0, 0, 0, 0);
        return (Math.Min(255, px[i + 2] * 255 / a), Math.Min(255, px[i + 1] * 255 / a), Math.Min(255, px[i] * 255 / a), a);
    }

    static bool Neutral((int R, int G, int B, int A) c) => Math.Abs(c.R - c.G) < 22 && Math.Abs(c.G - c.B) < 22;

    static int Distance((int R, int G, int B, int A) a, (int R, int G, int B, int A) b) =>
        Math.Abs(a.R - b.R) + Math.Abs(a.G - b.G) + Math.Abs(a.B - b.B);

    /// <summary>
    /// Windows' frame: a translucent neutral line (white at ~20 % or grey at ~40 %), square, the same distance
    /// in from every side. Inside it is empty or a plain plate of another colour. Real icons are opaque at their
    /// edge, round, or not the same distance in everywhere.
    /// </summary>
    static bool SquareLine(byte[] px, int w, int h)
    {
        int limit = (int)(Math.Min(w, h) * 0.3);
        var probes = new (int X, int Y, int Dx, int Dy)[]
        {
            (w / 2, 0, 0, 1), (w / 3, 0, 0, 1), (w / 2, h - 1, 0, -1), (w / 3, h - 1, 0, -1),
            (0, h / 2, 1, 0), (0, h / 3, 1, 0), (w - 1, h / 2, -1, 0), (w - 1, h / 3, -1, 0),
        };
        int first = -1;
        foreach (var (x0, y0, dx, dy) in probes)
        {
            int d = 0, x = x0, y = y0;
            while (d <= limit && px[(y * w + x) * 4 + 3] < 24) { x += dx; y += dy; d++; }
            if (d > limit) return false;
            var line = Px(px, (y * w + x) * 4);
            if (line.A > 235 || !Neutral(line) || line.R < 90) return false;
            if (first < 0) first = d; else if (Math.Abs(d - first) > 2) return false;
            int xi = x + dx * 4, yi = y + dy * 4;
            if (xi < 0 || yi < 0 || xi >= w || yi >= h) return false;
            var inner = Px(px, (yi * w + xi) * 4);
            bool empty = inner.A < 24;
            bool different = Distance(inner, line) > 60 || Math.Abs(inner.A - line.A) > 80;
            if (!empty && !different) return false;
        }
        return true;
    }
    static BitmapSource? FromHBitmap(IntPtr hbm, out bool framed, int minSize = 64, bool unframe = false)
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
        // Only Windows' translucent frame line is cut away. An opaque light square can be the app's real
        // icon (Android Studio, many others), so that case only falls back to the smaller sizes above.
        if (unframe && SquareLine(px, w, h) && Unframe(px, w, h) is { } cropped) return cropped;

        var src = BitmapSource.Create(w, h, 96, 96, PixelFormats.Pbgra32, null, px, w * 4);
        src.Freeze();
        return src;
    }

    /// <summary>
    /// Removes the frame Windows draws around a small icon: peels the outermost layer when it's one plain
    /// colour (the translucent line first, then a neutral plate under it), then crops the icon out square.
    /// </summary>
    static BitmapSource? Unframe(byte[] px, int w, int h)
    {
        var outside = new bool[w * h];
        var queue = new Queue<int>();
        void Spread()
        {
            // Everything transparent that touches the outside is outside too.
            while (queue.Count > 0)
            {
                int p = queue.Dequeue(), x = p % w, y = p / w;
                void Try(int q) { if (!outside[q] && px[q * 4 + 3] < 24) { outside[q] = true; queue.Enqueue(q); } }
                if (x > 0) Try(p - 1);
                if (x < w - 1) Try(p + 1);
                if (y > 0) Try(p - w);
                if (y < h - 1) Try(p + w);
            }
        }
        void Seed(int p) { if (!outside[p] && px[p * 4 + 3] < 24) { outside[p] = true; queue.Enqueue(p); } }
        for (int x = 0; x < w; x++) { Seed(x); Seed((h - 1) * w + x); }
        for (int y = 0; y < h; y++) { Seed(y * w); Seed(y * w + w - 1); }
        Spread();

        bool Touches(int p) { int x = p % w, y = p / w; return x == 0 || y == 0 || x == w - 1 || y == h - 1 || outside[p - 1] || outside[p + 1] || outside[p - w] || outside[p + w]; }

        for (int pass = 0; pass < 3; pass++)
        {
            // The outermost layer and its most common colour.
            var layer = new List<int>();
            for (int p = 0; p < w * h; p++) if (!outside[p] && px[p * 4 + 3] >= 24 && Touches(p)) layer.Add(p);
            if (layer.Count == 0) break;
            var key = layer.GroupBy(p => { var c = Px(px, p * 4); return (c.R >> 4) << 8 | (c.G >> 4) << 4 | c.B >> 4; })
                           .OrderByDescending(g => g.Count()).First();
            var colour = Px(px, key.First() * 4);
            double share = key.Count() / (double)layer.Count;
            // First the frame line; after it only a plain neutral plate, never the icon itself.
            if (pass > 0 && (share < 0.7 || !Neutral(colour))) break;
            bool Like(int p) { var c = Px(px, p * 4); return c.A >= 24 && Distance(c, colour) < 60; }
            var fill = new Queue<int>(layer.Where(Like));
            var taken = new HashSet<int>(fill);
            while (fill.Count > 0)
            {
                int p = fill.Dequeue(), x = p % w, y = p / w;
                px[p * 4] = px[p * 4 + 1] = px[p * 4 + 2] = px[p * 4 + 3] = 0;
                outside[p] = true;
                queue.Enqueue(p);
                void Next(int q) { if (!outside[q] && !taken.Contains(q) && Like(q)) { taken.Add(q); fill.Enqueue(q); } }
                if (x > 0) Next(p - 1);
                if (x < w - 1) Next(p + 1);
                if (y > 0) Next(p - w);
                if (y < h - 1) Next(p + w);
            }
            Spread();
        }

        int minX = w, minY = h, maxX = -1, maxY = -1;
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                if (px[(y * w + x) * 4 + 3] > 20) { minX = Math.Min(minX, x); maxX = Math.Max(maxX, x); minY = Math.Min(minY, y); maxY = Math.Max(maxY, y); }
        if (maxX < 0 || maxX - minX < 6) return null; // nothing (or almost nothing) left: keep the original

        // A square crop with a little air, so the icon sits like a normal one.
        int side = Math.Max(maxX - minX, maxY - minY) + 1;
        int pad = Math.Max(1, side / 12);
        side += pad * 2;
        int cx = (minX + maxX) / 2, cy = (minY + maxY) / 2;
        int sx = cx - side / 2, sy = cy - side / 2;
        var outPx = new byte[side * side * 4];
        for (int y = 0; y < side; y++)
            for (int x = 0; x < side; x++)
            {
                int ox = sx + x, oy = sy + y;
                if (ox < 0 || oy < 0 || ox >= w || oy >= h) continue;
                Buffer.BlockCopy(px, (oy * w + ox) * 4, outPx, (y * side + x) * 4, 4);
            }
        Interlocked.Increment(ref _unframed);
        var src = BitmapSource.Create(side, side, 96, 96, PixelFormats.Pbgra32, null, outPx, side * 4);
        src.Freeze();
        return src;
    }
    static byte[]? _generic;

    /// <summary>
    /// True for Windows' blank "unknown file" page, which it hands out for apps whose icon is missing
    /// (Steam game shortcuts whose icon hasn't been downloaded, broken shortcuts). Norrsken draws its own then.
    /// </summary>
    public static bool IsGenericPage(ImageSource? img)
    {
        if (img is not BitmapSource b) return false;
        _generic ??= GenericSignature();
        return _generic.Length > 0 && Signature(b) is { } s && Similar(s, _generic);
    }

    static byte[] GenericSignature()
    {
        try
        {
            string probe = Path.Combine(Path.GetTempPath(), "norrsken-icon-probe.nrskzz");
            if (!File.Exists(probe)) File.WriteAllText(probe, "");
            return Load(probe, DefaultSize) is BitmapSource b ? Signature(b) ?? [] : [];
        }
        catch (Exception) { return []; }
    }

    /// <summary>16×16 grey-and-alpha thumbnail: enough to recognise the same picture at any source size.</summary>
    static byte[]? Signature(BitmapSource b)
    {
        try
        {
            var small = new TransformedBitmap(b, new ScaleTransform(16.0 / b.PixelWidth, 16.0 / b.PixelHeight));
            var conv = new FormatConvertedBitmap(small, PixelFormats.Pbgra32, null, 0);
            var px = new byte[16 * 16 * 4];
            conv.CopyPixels(px, 16 * 4, 0);
            var sig = new byte[16 * 16 * 2];
            for (int i = 0; i < 256; i++) { sig[i * 2] = (byte)((px[i * 4] + px[i * 4 + 1] + px[i * 4 + 2]) / 3); sig[i * 2 + 1] = px[i * 4 + 3]; }
            return sig;
        }
        catch (Exception) { return null; }
    }

    static bool Similar(byte[] a, byte[] b)
    {
        if (a.Length != b.Length) return false;
        long diff = 0;
        for (int i = 0; i < a.Length; i++) diff += Math.Abs(a[i] - b[i]);
        return diff / (double)a.Length < 10;
    }}
