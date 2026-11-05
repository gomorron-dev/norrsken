using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using Norrsken.Shell.Core;
using Norrsken.Shell.Ui;

namespace Norrsken.Shell;

/// <summary>
/// `--render-showcase dir`: the pictures for the README, the releases and the repository's social preview,
/// drawn from Norrsken's real windows offscreen (nothing appears on screen). Anything personal is replaced:
/// the account is "Alex" and Quick Settings plays a demo track.
/// </summary>
public static class Showcase
{
    const double Scale = 2; // crisp on high-DPI screens and GitHub's retina rendering

    public static int Run(string dir)
    {
        Directory.CreateDirectory(dir);
        App.DemoName = "Alex";
        App.TestMode = true; // no hooks, no system changes, no launches
        Theme.Apply("graphite");

        // The same data the dock and launcher use, without starting the desktop.
        var tracker = new WindowTracker();
        App.Tracker = tracker;
        tracker.Refresh();
        var apps = AppIndex.LoadAsync().GetAwaiter().GetResult();
        foreach (var a in apps) App.Apps.Add(a);
        App.ResolvePinned();
        AppIndex.LoadIconsAsync(apps).GetAwaiter().GetResult();
        App.Dock = new DockModel();
        App.Dock.RefreshNow();

        string walls = Path.Combine(Path.GetTempPath(), "norrsken-showcase-walls");
        Branding.Render(walls);
        var dusk = Wallpaper(Path.Combine(walls, "norrsken-dusk.png"));
        var paper = Wallpaper(Path.Combine(walls, "norrsken-fog.png"));

        int ok = 0;
        void Do(string name, Func<FrameworkElement> build, double w, double h)
        {
            try { Save(build(), w, h, Path.Combine(dir, name)); ok++; }
            catch (Exception ex) { Log.Write($"Showcase {name}: {ex}"); }
        }

        if (Environment.GetEnvironmentVariable("NORRSKEN_DUMP_ICONS") is { Length: > 0 } dump)
        {
            Directory.CreateDirectory(dump);
            foreach (var a in apps.Where(a => a.Icon is BitmapSource))
            {
                var enc = new PngBitmapEncoder();
                enc.Frames.Add(BitmapFrame.Create((BitmapSource)a.Icon!));
                string safe = string.Concat(a.Name.Select(ch => Path.GetInvalidFileNameChars().Contains(ch) ? '_' : ch));
                using var fs = File.Create(Path.Combine(dump, safe + ".png"));
                enc.Save(fs);
            }
        }
        Do("hero.png", () => Hero(dusk), 1600, 1000);
        Do("dock.png", () => DockStrip(dusk, hover: 3), 1200, 190);
        Do("launcher.png", () => Framed(Launcher(), dusk, 760, 680), 760, 680);
        Do("quick.png", () => { var q = Quick(); q.VerticalAlignment = VerticalAlignment.Bottom; return Framed(q, dusk, 460, 600); }, 460, 600);
        Do("settings.png", () => WindowShot(new SettingsWindow(null), 1000, 680, dusk), 1080, 760);
        Do("themes.png", Themes, 1200, 560);
        Do("social.png", () => Social(dusk), 1280, 640);
        Do("bin.png", () => BinShot(dusk), 1080, 700);
        Do("logo.png", () => Logo(512), 512, 512);
        Theme.Apply("paper");
        Do("hero-light.png", () => Hero(paper), 1600, 1000);
        Theme.Apply("graphite");
        WriteIcon(Path.Combine(dir, "norrsken.ico"));
        Log.Write($"Showcase: {ok} pictures in {dir}");
        return ok >= 9 ? 0 : 1;
    }

    // ───────────────────────── scenes ─────────────────────────

    /// <summary>The Bin with demo items in a throwaway bin folder (never the real Recycle Bin).</summary>
    static FrameworkElement BinShot(Brush wall)
    {
        string bin = Path.Combine(Path.GetTempPath(), "norrsken-showcase-bin");
        if (Directory.Exists(bin)) Directory.Delete(bin, true);
        Directory.CreateDirectory(bin);
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string pics = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), docs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        var demo = new (string Path, long Size, TimeSpan Ago)[]
        {
            (Path.Combine(pics, "Lofoten 2026", "aurora-0412.jpg"), 6_400_000, TimeSpan.FromMinutes(4)),
            (Path.Combine(pics, "Lofoten 2026", "aurora-0413.jpg"), 6_100_000, TimeSpan.FromMinutes(4)),
            (Path.Combine(docs, "Drafts", "talk-notes-old.md"), 18_000, TimeSpan.FromHours(3)),
            (Path.Combine(home, "Downloads", "setup-1.4.2.exe"), 92_000_000, TimeSpan.FromHours(26)),
            (Path.Combine(docs, "Invoices", "2025"), 3_200_000, TimeSpan.FromDays(3)),
            (Path.Combine(home, "Downloads", "screen-recording.mp4"), 412_000_000, TimeSpan.FromDays(12)),
        };
        int n = 0;
        foreach (var (p, size, ago) in demo)
        {
            string id = $"SHW{++n:D3}" + Path.GetExtension(p);
            bool folder = Path.GetExtension(p).Length == 0;
            string data = Path.Combine(bin, "$R" + id);
            if (folder) Directory.CreateDirectory(data); else File.WriteAllText(data, "demo");
            using var w = new BinaryWriter(File.Create(Path.Combine(bin, "$I" + id)));
            w.Write(2L); w.Write(size); w.Write((DateTime.Now - ago).ToFileTime());
            w.Write(p.Length + 1); w.Write(System.Text.Encoding.Unicode.GetBytes(p + "\0"));
        }
        Bin.TestRoot = bin;
        var window = new BinWindow();
        var shot = WindowShot(window, 1000, 620, wall);
        window.Refresh();
        window.TestSelectFirst();
        Bin.TestRoot = null;
        return shot;
    }

    static FrameworkElement Hero(Brush wall)
    {
        var canvas = new Canvas { Width = 1600, Height = 1000, Background = wall, ClipToBounds = true };
        var launcher = Launcher();
        Place(canvas, launcher, 170, 60, 760, 680);
        var quick = Quick();
        // Its natural height, standing just above the dock like the real panel.
        quick.Width = 460;
        Canvas.SetLeft(quick, 1010);
        Canvas.SetBottom(quick, 196);
        canvas.Children.Add(quick);
        var (dock, window) = Dock(hover: 3);
        Place(canvas, dock, 200, 820, 1200, 170);
        return canvas;
    }

    static FrameworkElement DockStrip(Brush wall, int hover)
    {
        var grid = new Grid { Width = 1200, Height = 190, Background = wall };
        var (dock, _) = Dock(hover);
        dock.VerticalAlignment = VerticalAlignment.Bottom;
        dock.Height = 170;
        grid.Children.Add(dock);
        return grid;
    }

    static FrameworkElement Themes()
    {
        // The dock in four themes, each on its own wallpaper.
        var stack = new System.Windows.Controls.Primitives.UniformGrid { Rows = 4, Width = 1200, Height = 560 };
        foreach (var (theme, wall) in new[] { ("graphite", "norrsken-graphite.png"), ("paper", "norrsken-paper.png"), ("slate", "norrsken-slate.png"), ("sand", "norrsken-sand.png") })
        {
            Theme.Apply(theme);
            var strip = new Grid { Background = Wallpaper(Path.Combine(Path.GetTempPath(), "norrsken-showcase-walls", wall)), ClipToBounds = true };
            var (dock, _) = Dock(-1);
            dock.VerticalAlignment = VerticalAlignment.Center;
            dock.Height = 140;
            strip.Children.Add(dock);
            // Freeze this theme into pixels before the next one changes the shared brushes.
            stack.Children.Add(new Image { Source = Snapshot(strip, 1200, 140), Width = 1200, Height = 140 });
        }
        Theme.Apply("graphite");
        return stack;
    }

    static FrameworkElement Social(Brush wall)
    {
        var grid = new Grid { Width = 1280, Height = 640, Background = wall, ClipToBounds = true };
        var text = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 120, 0, 0) };
        var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
        row.Children.Add(new Image { Source = LogoBitmap(112), Width = 84, Height = 84, Margin = new Thickness(0, 0, 26, 0), VerticalAlignment = VerticalAlignment.Center });
        row.Children.Add(new TextBlock
        {
            Text = "Norrsken", FontSize = 96, FontWeight = FontWeights.SemiBold, Foreground = Brushes.White, VerticalAlignment = VerticalAlignment.Center,
            FontFamily = (FontFamily)Application.Current.FindResource("DisplayFont"),
        });
        text.Children.Add(row);
        text.Children.Add(new TextBlock
        {
            Text = "A calm, monochrome desktop for Windows 11", FontSize = 30, Foreground = new SolidColorBrush(Color.FromArgb(190, 255, 255, 255)),
            HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 14, 0, 0),
            FontFamily = (FontFamily)Application.Current.FindResource("UiFont"),
        });
        grid.Children.Add(text);
        var (dock, window) = Dock(hover: 3);
        dock.VerticalAlignment = VerticalAlignment.Bottom;
        dock.Width = 1200;
        dock.Height = 170;
        dock.Margin = new Thickness(0, 0, 0, 30);
        grid.Children.Add(dock);
        return grid;
    }

    // ───────────────────────── pieces ─────────────────────────

    static (FrameworkElement Root, DockWindow Window) Dock(int hover)
    {
        var dock = new DockWindow();
        var root = (FrameworkElement)dock.Content;
        dock.Content = null;
        _hover.Add((dock, hover));
        return (root, dock);
    }

    static readonly List<(DockWindow Dock, int Hover)> _hover = [];

    static FrameworkElement Launcher()
    {
        var w = new LauncherWindow();
        var root = (FrameworkElement)w.Content;
        w.Content = null;
        return root;
    }

    static FrameworkElement Quick()
    {
        var q = new QuickSettingsWindow();
        q.PrepareForPreview(new MediaController.State("Northern Lights", "Aurora Ensemble", "Spotify", true, DemoArt()));
        var root = (FrameworkElement)q.Content;
        q.Content = null;
        return root;
    }

    /// <summary>A window as it appears on the desktop: its content in a rounded frame with a soft shadow.</summary>
    static FrameworkElement WindowShot(Window window, double w, double h, Brush wall)
    {
        var root = (FrameworkElement)window.Content;
        window.Content = null;
        var body = new Border
        {
            Width = w, Height = h, CornerRadius = new CornerRadius(12), ClipToBounds = true,
            Background = window.Background ?? (Brush)Application.Current.Resources["SurfaceSolid"],
            BorderThickness = new Thickness(1), BorderBrush = new SolidColorBrush(Color.FromArgb(40, 255, 255, 255)),
            Child = root,
        };
        body.Clip = new RectangleGeometry(new Rect(0, 0, w, h), 12, 12);
        var shadowed = new Border
        {
            Width = w, Height = h, CornerRadius = new CornerRadius(12), Background = Brushes.Black,
            Effect = new DropShadowEffect { BlurRadius = 60, ShadowDepth = 16, Direction = 270, Opacity = 0.55 },
        };
        var grid = new Grid { Background = wall };
        grid.Children.Add(shadowed);
        grid.Children.Add(body);
        return grid;
    }

    static FrameworkElement Framed(FrameworkElement root, Brush wall, double w, double h)
    {
        var grid = new Grid { Width = w, Height = h, Background = wall };
        grid.Children.Add(root);
        return grid;
    }

    static void Place(Canvas c, FrameworkElement e, double x, double y, double w, double h)
    {
        e.Width = w; e.Height = h;
        Canvas.SetLeft(e, x); Canvas.SetTop(e, y);
        c.Children.Add(e);
    }

    static Brush Wallpaper(string path)
    {
        var img = new BitmapImage();
        img.BeginInit();
        img.UriSource = new Uri(path);
        img.CacheOption = BitmapCacheOption.OnLoad;
        img.EndInit();
        img.Freeze();
        return new ImageBrush(img) { Stretch = Stretch.UniformToFill };
    }

    /// <summary>Album art for the demo track: a small aurora.</summary>
    static ImageSource DemoArt()
    {
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            dc.DrawRectangle(new LinearGradientBrush(Color.FromRgb(16, 24, 40), Color.FromRgb(4, 6, 12), 90), null, new Rect(0, 0, 200, 200));
            var glow = new LinearGradientBrush(new GradientStopCollection
            {
                new(Color.FromArgb(0, 80, 255, 190), 0), new(Color.FromArgb(200, 80, 255, 190), 0.5), new(Color.FromArgb(0, 120, 140, 255), 1),
            }, 0);
            var path = Geometry.Parse("M10,120 C60,40 110,40 130,95 C150,150 175,140 195,70");
            // A soft glow: wide faint strokes under a narrow bright one.
            foreach (var (width, alpha) in new[] { (44.0, 0.18), (30.0, 0.3), (18.0, 0.5), (8.0, 0.9) })
            {
                dc.PushOpacity(alpha);
                dc.DrawGeometry(null, new Pen(glow, width) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round }, path);
                dc.Pop();
            }
            dc.DrawRectangle(new SolidColorBrush(Color.FromArgb(70, 0, 0, 0)), null, new Rect(0, 150, 200, 50));
        }
        var rtb = new RenderTargetBitmap(200, 200, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(dv);
        rtb.Freeze();
        return rtb;
    }

    // ───────────────────────── the mark ─────────────────────────

    /// <summary>Norrsken's mark: an aurora wave over the horizon, white on a graphite tile.</summary>
    public static BitmapSource LogoBitmap(int size)
    {
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            double inset = size * 0.06, r = size * 0.23;
            var tile = new Rect(inset, inset, size - 2 * inset, size - 2 * inset);
            dc.DrawRoundedRectangle(new LinearGradientBrush(Color.FromRgb(58, 58, 62), Color.FromRgb(18, 18, 20), 90),
                new Pen(new SolidColorBrush(Color.FromArgb(70, 255, 255, 255)), Math.Max(1, size / 128.0)), tile, r, r);
            double s = (size - 2 * inset) * 0.64 / 24;
            dc.PushTransform(new TranslateTransform(size / 2.0 - 12 * s, size / 2.0 - 12 * s));
            dc.PushTransform(new ScaleTransform(s, s));
            var wave = new Pen(Brushes.White, size <= 32 ? 2.6 : 2.2) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
            dc.DrawGeometry(null, wave, Geometry.Parse("M3,14 C6,4 10,4 12,9.5 C14,15 18,15 21,5"));
            var horizon = new Pen(new SolidColorBrush(Color.FromRgb(150, 150, 154)), size <= 32 ? 2.4 : 2) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
            dc.DrawGeometry(null, horizon, Geometry.Parse("M5,20 L19,20"));
            dc.Pop(); dc.Pop();
        }
        var rtb = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(dv);
        rtb.Freeze();
        return rtb;
    }

    static FrameworkElement Logo(int size) => new Image { Source = LogoBitmap(size), Width = size, Height = size };

    /// <summary>The app icon (Norrsken.Shell.exe): PNG frames from 256 down to 16 px.</summary>
    public static void WriteIcon(string path)
    {
        int[] sizes = [256, 64, 48, 40, 32, 24, 20, 16];
        var frames = sizes.Select(s => { var e = new PngBitmapEncoder(); e.Frames.Add(BitmapFrame.Create(LogoBitmap(s))); using var ms = new MemoryStream(); e.Save(ms); return ms.ToArray(); }).ToList();
        using var fs = File.Create(path);
        using var w = new BinaryWriter(fs);
        w.Write((short)0); w.Write((short)1); w.Write((short)frames.Count);
        int offset = 6 + 16 * frames.Count;
        for (int i = 0; i < frames.Count; i++)
        {
            int s = sizes[i];
            w.Write((byte)(s >= 256 ? 0 : s)); w.Write((byte)(s >= 256 ? 0 : s));
            w.Write((byte)0); w.Write((byte)0); w.Write((short)1); w.Write((short)32);
            w.Write(frames[i].Length); w.Write(offset);
            offset += frames[i].Length;
        }
        foreach (var f in frames) w.Write(f);
    }

    // ───────────────────────── rendering ─────────────────────────

    static BitmapSource Snapshot(FrameworkElement root, double w, double h)
    {
        root.Width = w; root.Height = h;
        root.Measure(new Size(w, h));
        root.Arrange(new Rect(0, 0, w, h));
        root.UpdateLayout();
        foreach (var (d, hv) in _hover) if (hv >= 0) d.PreviewHover(hv);
        Switches.SettleAll(root);
        root.UpdateLayout();
        var rtb = new RenderTargetBitmap((int)(w * Scale), (int)(h * Scale), 96 * Scale, 96 * Scale, PixelFormats.Pbgra32);
        rtb.Render(root);
        rtb.Freeze();
        _hover.Clear();
        return rtb;
    }

    static void Save(FrameworkElement root, double w, double h, string path)
    {
        var host = new Grid { Width = w, Height = h };
        host.Children.Add(root);
        var bmp = Snapshot(host, w, h);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(bmp));
        using var fs = File.Create(path);
        enc.Save(fs);
    }
}
