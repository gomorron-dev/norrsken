using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Norrsken.Shell.Core;

/// <summary>
/// Renders Norrsken's own monochrome artwork for the OS image (`--render-branding dir`):
/// 4K wallpapers for each theme and the 120×120 OEM logo shown in Windows' About page.
/// Everything is drawn from the Norrsken mark, so it can be regenerated at any size.
/// </summary>
public static class Branding
{
    const int W = 3840, H = 2160;

    public static int Render(string dir)
    {
        Directory.CreateDirectory(dir);
        Wallpaper(Path.Combine(dir, "norrsken-graphite.png"), Color.FromRgb(44, 44, 46), Color.FromRgb(14, 14, 15), Color.FromArgb(46, 255, 255, 255));
        Wallpaper(Path.Combine(dir, "norrsken-ink.png"), Color.FromRgb(18, 18, 18), Color.FromRgb(0, 0, 0), Color.FromArgb(40, 255, 255, 255));
        Wallpaper(Path.Combine(dir, "norrsken-paper.png"), Color.FromRgb(246, 246, 246), Color.FromRgb(214, 214, 216), Color.FromArgb(40, 0, 0, 0));
        Dunes(Path.Combine(dir, "norrsken-ash.png"), Color.FromRgb(58, 58, 58), Color.FromRgb(22, 22, 22), light: false);
        Rings(Path.Combine(dir, "norrsken-slate.png"), Color.FromRgb(40, 44, 48), Color.FromRgb(14, 16, 18), Color.FromArgb(34, 255, 255, 255));
        Dunes(Path.Combine(dir, "norrsken-fog.png"), Color.FromRgb(238, 238, 238), Color.FromRgb(196, 196, 198), light: true);
        Rings(Path.Combine(dir, "norrsken-sand.png"), Color.FromRgb(246, 243, 237), Color.FromRgb(216, 210, 200), Color.FromArgb(36, 60, 40, 10));
        Dunes(Path.Combine(dir, "norrsken-dusk.png"), Color.FromRgb(30, 30, 32), Color.FromRgb(4, 4, 5), light: false);
        Rings(Path.Combine(dir, "norrsken-orbit.png"), Color.FromRgb(20, 20, 20), Color.FromRgb(0, 0, 0), Color.FromArgb(44, 255, 255, 255));
        Logo(Path.Combine(dir, "oemlogo.bmp"));
        // Default account pictures (C:\ProgramData\Microsoft\User Account Pictures), every size Windows asks for.
        string avatars = Path.Combine(dir, "account-pictures");
        Directory.CreateDirectory(avatars);
        foreach (var (name, size) in new[] { ("user", 448), ("user-192", 192), ("user-48", 48), ("user-40", 40), ("user-32", 32), ("guest", 448) })
        {
            Avatar(Path.Combine(avatars, name + ".png"), size, new PngBitmapEncoder());
            if (size == 448) Avatar(Path.Combine(avatars, name + ".bmp"), size, new BmpBitmapEncoder());
        }
        return 0;
    }

    /// <summary>A quiet monochrome silhouette on graphite, softened by a faint aurora line.</summary>
    static void Avatar(string path, int s, BitmapEncoder enc)
    {
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            dc.DrawRectangle(new LinearGradientBrush(Color.FromRgb(58, 58, 60), Color.FromRgb(26, 26, 27), 90), null, new Rect(0, 0, s, s));
            dc.PushTransform(new ScaleTransform(s / 24.0, s / 24.0));
            var fill = new SolidColorBrush(Color.FromRgb(214, 214, 214));
            dc.DrawEllipse(fill, null, new Point(12, 9.4), 3.6, 3.6);
            dc.DrawGeometry(fill, null, Geometry.Parse("M4.6,24 C4.6,18.2 8,15.4 12,15.4 C16,15.4 19.4,18.2 19.4,24 Z"));
            dc.DrawGeometry(null, new Pen(new SolidColorBrush(Color.FromArgb(70, 255, 255, 255)), 0.45) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round },
                Geometry.Parse("M1.5,5.5 C5,1.5 8.5,1.5 11,4 C13.5,6.5 18,6.5 22.5,2.5"));
            dc.Pop();
        }
        Save(dv, s, s, path, enc);
    }

    /// <summary>A soft vertical gradient with faint aurora ribbons sweeping across the lower half.</summary>
    static void Wallpaper(string path, Color top, Color bottom, Color ribbon)
    {
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            dc.DrawRectangle(new LinearGradientBrush(top, bottom, 90), null, new Rect(0, 0, W, H));
            // Five ribbons, each a slightly offset copy of the mark's curve, fading in strength.
            for (int i = 0; i < 5; i++)
            {
                double y = H * 0.56 + i * 46, amp = H * (0.16 - i * 0.012);
                var g = new StreamGeometry();
                using (var ctx = g.Open())
                {
                    ctx.BeginFigure(new Point(-200, y + amp * 0.4), false, false);
                    ctx.BezierTo(new Point(W * 0.22, y - amp), new Point(W * 0.42, y - amp), new Point(W * 0.52, y), true, true);
                    ctx.BezierTo(new Point(W * 0.62, y + amp), new Point(W * 0.82, y + amp), new Point(W + 200, y - amp * 0.9), true, true);
                }
                g.Freeze();
                var c = Color.FromArgb((byte)(ribbon.A * (1 - i * 0.17)), ribbon.R, ribbon.G, ribbon.B);
                dc.DrawGeometry(null, new Pen(new SolidColorBrush(c), 5 - i * 0.6) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round }, g);
            }
        }
        Save(dv, W, H, path, new PngBitmapEncoder());
    }

    /// <summary>Layered soft hills, lighter toward the horizon, like snow dunes under a night sky.</summary>
    static void Dunes(string path, Color top, Color bottom, bool light)
    {
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            dc.DrawRectangle(new LinearGradientBrush(top, bottom, 90), null, new Rect(0, 0, W, H));
            for (int i = 0; i < 6; i++)
            {
                double baseY = H * (0.48 + i * 0.09), amp = H * (0.07 - i * 0.006), phase = i * 0.9;
                var g = new StreamGeometry();
                using (var ctx = g.Open())
                {
                    ctx.BeginFigure(new Point(0, H), true, true);
                    ctx.LineTo(new Point(0, baseY), false, false);
                    for (int x = 0; x <= W; x += 40)
                    {
                        double y = baseY + Math.Sin(x / (W / 2.6) * Math.PI + phase) * amp + Math.Sin(x / (W / 7.0) * Math.PI + phase * 2) * amp * 0.25;
                        ctx.LineTo(new Point(x, y), true, true);
                    }
                    ctx.LineTo(new Point(W, H), false, false);
                }
                g.Freeze();
                byte a = (byte)(light ? 26 + i * 8 : 18 + i * 10);
                var shade = light ? Color.FromArgb(a, 0, 0, 0) : Color.FromArgb(a, 0, 0, 0);
                var edge = light ? Color.FromArgb(60, 255, 255, 255) : Color.FromArgb((byte)(26 - i * 3), 255, 255, 255);
                dc.DrawGeometry(new SolidColorBrush(shade), new Pen(new SolidColorBrush(edge), 2.5), g);
            }
        }
        Save(dv, W, H, path, new PngBitmapEncoder());
    }

    /// <summary>Fine concentric arcs rising from below the frame: a quiet horizon.</summary>
    static void Rings(string path, Color top, Color bottom, Color line)
    {
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            dc.DrawRectangle(new RadialGradientBrush(top, bottom) { Center = new Point(0.5, 1.05), GradientOrigin = new Point(0.5, 1.05), RadiusX = 0.9, RadiusY = 1.2 }, null, new Rect(0, 0, W, H));
            var centre = new Point(W * 0.5, H * 1.32);
            for (int i = 0; i < 26; i++)
            {
                double r = H * 0.62 + i * H * 0.045;
                double fade = 1 - i / 26.0;
                var c = Color.FromArgb((byte)(line.A * fade), line.R, line.G, line.B);
                dc.DrawEllipse(null, new Pen(new SolidColorBrush(c), i % 5 == 0 ? 3 : 1.6), centre, r, r);
            }
        }
        Save(dv, W, H, path, new PngBitmapEncoder());
    }

    static void Logo(string path)
    {
        const int S = 120;
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, S, S));
            dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromRgb(20, 20, 20)), null, new Rect(14, 14, 92, 92), 24, 24);
            dc.PushTransform(new TranslateTransform(28, 28));
            dc.PushTransform(new ScaleTransform(64 / 24.0, 64 / 24.0));
            var pen = new Pen(Brushes.White, 2.4) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
            dc.DrawGeometry(null, pen, Geometry.Parse("M3,14 C6,4 10,4 12,9.5 C14,15 18,15 21,5"));
            dc.DrawGeometry(null, new Pen(new SolidColorBrush(Color.FromRgb(150, 150, 150)), 2.2) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round },
                Geometry.Parse("M5,20 L19,20"));
            dc.Pop();
            dc.Pop();
        }
        Save(dv, S, S, path, new BmpBitmapEncoder()); // Windows requires a .bmp for OEM logos
    }

    static void Save(Visual v, int w, int h, string path, BitmapEncoder enc)
    {
        var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(v);
        enc.Frames.Add(BitmapFrame.Create(enc is BmpBitmapEncoder ? new FormatConvertedBitmap(rtb, PixelFormats.Bgr24, null, 0) : rtb));
        using var fs = File.Create(path);
        enc.Save(fs);
    }
}
