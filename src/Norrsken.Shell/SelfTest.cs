using System.Diagnostics;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using Norrsken.Shell.Core;
using Norrsken.Shell.Ui;
using Shapes = System.Windows.Shapes;

namespace Norrsken.Shell;

/// <summary>
/// `Norrsken.Shell.exe --selftest [outDir] [--launch-test]`
/// Exercises the real subsystems against the real machine without registering the AppBar
/// or touching the user's settings, then renders each surface to PNG for review.
/// </summary>
internal static class SelfTest
{
    sealed record Result(string Name, bool Ok, string Detail, long Ms);

    public static int Run(string outDir, bool launchTest)
    {
        outDir = Path.GetFullPath(outDir);
        Directory.CreateDirectory(outDir);
        Environment.SetEnvironmentVariable("NORRSKEN_SETTINGS_DIR", Path.Combine(outDir, "settings"));
        Motion.ForceInstant = true;
        var results = new List<Result>();

        void Check(string name, Func<(bool ok, string detail)> body)
        {
            var sw = Stopwatch.StartNew();
            try
            {
                var (ok, detail) = body();
                results.Add(new(name, ok, detail, sw.ElapsedMilliseconds));
            }
            catch (Exception ex) { results.Add(new(name, false, ex.GetType().Name + ": " + ex.Message, sw.ElapsedMilliseconds)); }
        }

        Check("settings.roundtrip", () =>
        {
            var s = new ShellSettings { Theme = "ink", Magnify = false, ReserveSpace = false, Clock24h = false, Pinned = ["a", "b"] };
            s.Save();
            var r = ShellSettings.Load();
            bool ok = r.Theme == "ink" && !r.Magnify && !r.ReserveSpace && !r.Clock24h && r.Pinned?.Count == 2;
            File.WriteAllText(ShellSettings.FilePath, "{ not json");
            var fallback = ShellSettings.Load();
            return (ok && fallback.Theme == "graphite", "saved+reloaded; corrupt file falls back to defaults");
        });

        Check("theme.apply.all", () =>
        {
            foreach (var t in Theme.All)
            {
                Theme.Apply(t.Id);
                if (Application.Current.Resources["DockBg"] is not SolidColorBrush) return (false, "no DockBg after " + t.Id);
            }
            Theme.Apply("polar-night"); // legacy id from 0.1.0
            bool legacy = Theme.Current == "graphite";
            return (legacy, $"{Theme.All.Count} monochrome themes; legacy 'polar-night' -> {Theme.Current}");
        });

        Check("theme.monochrome", () =>
        {
            // Every Norrsken colour must be a grey (R=G=B); colour comes only from third-party app icons.
            var offenders = new List<string>();
            foreach (var t in Theme.All)
            {
                Theme.Apply(t.Id);
                foreach (var key in new[] { "DockBg", "DockLine", "Surface", "SurfaceSolid", "SurfaceAlt", "Border", "Text", "TextDim", "Hover", "Pressed", "Selection", "Dot", "PebbleFill" })
                    if (Application.Current.Resources[key] is SolidColorBrush { Color: var c } && !(c.R == c.G && c.G == c.B))
                        offenders.Add($"{t.Id}.{key}");
            }
            Theme.Apply("graphite");
            return (offenders.Count == 0, offenders.Count == 0 ? "all theme colours are neutral greys" : string.Join(", ", offenders));
        });

        Check("icons.glyphs.parse", () =>
        {
            var names = IconPack.Names.ToList();
            var bad = names.Where(n => IconPack.Named(n) is not { Bounds: { Width: > 4, Height: > 4 } b } || b.Right > 24.5 || b.Bottom > 24.5 || b.Left < -0.5 || b.Top < -0.5).ToList();
            return (bad.Count == 0, bad.Count == 0 ? $"{names.Count} glyphs parse and sit inside the 24-unit grid" : "out of grid: " + string.Join(", ", bad));
        });

        var tracker = new WindowTracker();
        App.Tracker = tracker;
        Check("windows.enumerate", () =>
        {
            tracker.Refresh();
            int withIcon = tracker.Windows.Count(w => w.Icon != null);
            int withId = tracker.Windows.Count(w => w.Aumid != null);
            return (true, $"{tracker.Windows.Count} app windows, {withIcon} with icons, {withId} with an app identity");
        });

        List<AppEntry> apps = [];
        Check("apps.index", () =>
        {
            apps = AppIndex.LoadAsync().GetAwaiter().GetResult();
            foreach (var a in apps) App.Apps.Add(a);
            App.ResolvePinned();
            int targets = apps.Count(a => a.TargetPath != null);
            return (apps.Count > 0, $"{apps.Count} apps listed, {targets} with a resolved executable, {App.Pinned.Count} default pins");
        });

        Check("apps.icons", () =>
        {
            int icons = AppIndex.LoadIconsAsync(apps).GetAwaiter().GetResult();
            return (icons > apps.Count * 9 / 10, $"{icons}/{apps.Count} icons loaded in background pass");
        });

        Check("icons.builtin.coverage", () =>
        {
            var themed = apps.Where(a => a.HasGlyph).Select(a => a.Name).ToList();
            return (themed.Count > 0, $"{themed.Count} built-in apps drawn with Norrsken glyphs: {string.Join(", ", themed.Take(24))}");
        });

        Check("apps.claude.present", () =>
        {
            var hits = apps.Where(a => a.Name.Contains("Claude", StringComparison.OrdinalIgnoreCase)).ToList();
            return (hits.Count > 0, hits.Count > 0 ? string.Join(", ", hits.Select(h => $"{h.Name} [{h.ParsingName}]")) : "no Claude entry");
        });

        Check("launcher.ranking", () =>
        {
            var cases = new (string Name, string Query, int Min)[]
            {
                ("Visual Studio Code", "vis", 100), ("Visual Studio Code", "code", 80),
                ("Visual Studio Code", "vsc", 70), ("Aktivitetshanteraren", "hanter", 50), ("Task Manager", "tmgr", 20),
            };
            var bad = cases.Where(c => AppIndex.Score(c.Name, c.Query) < c.Min).Select(c => $"{c.Query}->{c.Name}").ToList();
            bool noise = AppIndex.Score("Calculator", "zz") == 0;
            var top = AppIndex.Search(apps, "term").FirstOrDefault()?.Name;
            return (bad.Count == 0 && noise, $"prefix/word/initials/substring/in-order ranks hold; 'term' -> {top}" + (bad.Count > 0 ? "; failed: " + string.Join(", ", bad) : ""));
        });

        Check("status.audio", () =>
        {
            SystemStatus.ReopenAudio();
            double v = SystemStatus.Volume;
            return (SystemStatus.HasAudio && v is >= 0 and <= 100, $"default output readable: volume {Math.Round(v)}, muted={SystemStatus.Muted}, glyph {SystemStatus.VolumeGlyph} (read-only)");
        });

        Check("status.network", () =>
        {
            var n = SystemStatus.Network();
            return (true, $"{n.Glyph}, '{n.Label}', online={n.Online}");
        });

        Check("status.brightness", () =>
        {
            int? b = SystemStatus.GetBrightness();
            return (true, b is int v ? $"built-in display at {v}%" : "no controllable display (expected on a desktop); slider hidden");
        });

        Check("status.media", () =>
        {
            // Off the UI thread: blocking on WinRT awaits from the dispatcher thread deadlocks.
            Task.Run(SystemStatus.Media.StartAsync).Wait(TimeSpan.FromSeconds(10));
            var m = Task.Run(SystemStatus.Media.GetAsync).GetAwaiter().GetResult();
            return (true, m == null ? "no media session right now" : $"session from {m.SourceAppId}, playing={m.Playing}, art={(m.Art != null)}");
        });

        var dockModel = new DockModel();
        App.Dock = dockModel;
        Check("dock.model", () =>
        {
            dockModel.RefreshNow();
            var items = dockModel.Items;
            int matched = tracker.Windows.Count(w => dockModel.Match(w) != null);
            var unmatched = tracker.Windows.Where(w => dockModel.Match(w) == null)
                .Select(w => Path.GetFileName(w.ExePath ?? "?")).Distinct().ToList();
            bool pinsFirst = items.Take(App.Pinned.Count).All(i => i.IsPinned);
            return (items.Count > 0 && pinsFirst,
                $"{items.Count} dock items ({items.Count(i => i.IsPinned)} pinned, {items.Count(i => i.IsRunning)} running); " +
                $"{matched}/{tracker.Windows.Count} windows matched to their app" + (unmatched.Count > 0 ? $"; unmatched: {string.Join(", ", unmatched)}" : ""));
        });

        Check("dock.diff.stable", () =>
        {
            var before = dockModel.Items.ToList();
            dockModel.RefreshNow();
            bool same = before.SequenceEqual(dockModel.Items);
            return (same, same ? "re-refresh keeps every item instance (no animation churn)" : "items were recreated");
        });

        if (launchTest)
            Check("apps.launch.charmap", () => LaunchAndClose(tracker));

        var backdrop = Backdrop();
        Check("render.dock.graphite", () => RenderDock(Path.Combine(outDir, "dock-graphite.png"), backdrop, hover: -1));
        Check("render.dock.hover", () => RenderDock(Path.Combine(outDir, "dock-hover.png"), backdrop, hover: 2));
        Check("render.launcher.graphite", () => RenderWindow(new LauncherWindow(), 700, 620, Path.Combine(outDir, "launcher-graphite.png"), backdrop));
        Check("render.settings.graphite", () => RenderWindow(new SettingsWindow(null), 700, 720, Path.Combine(outDir, "settings-graphite.png"), null));
        Check("render.icons", () => RenderIconSheet(Path.Combine(outDir, "icons.png")));
        Check("render.quick.graphite", () => RenderQuick(Path.Combine(outDir, "quick-graphite.png"), backdrop));
        Theme.Apply("paper");
        Check("render.dock.paper", () => RenderDock(Path.Combine(outDir, "dock-paper.png"), backdrop, hover: -1));
        Check("render.launcher.paper", () => RenderWindow(new LauncherWindow(), 700, 620, Path.Combine(outDir, "launcher-paper.png"), backdrop));
        Check("render.settings.paper", () => RenderWindow(new SettingsWindow(null), 700, 720, Path.Combine(outDir, "settings-paper.png"), null));
        Check("render.quick.paper", () => RenderQuick(Path.Combine(outDir, "quick-paper.png"), backdrop));
        Theme.Apply("graphite");

        results.Add(new("process.workingset", true, $"{Process.GetCurrentProcess().WorkingSet64 / (1024 * 1024)} MB after all tests", 0));

        File.WriteAllText(Path.Combine(outDir, "results.json"),
            JsonSerializer.Serialize(new { version = App.Version, machine = Environment.MachineName, os = Environment.OSVersion.VersionString, at = DateTime.Now, results },
                new JsonSerializerOptions { WriteIndented = true }));
        tracker.Dispose();
        return results.All(r => r.Ok) ? 0 : 1;
    }

    /// <summary>Launches Character Map through shell:AppsFolder, confirms its window is tracked, closes only that window.</summary>
    static (bool, string) LaunchAndClose(WindowTracker tracker)
    {
        tracker.Refresh();
        var before = tracker.Windows.Select(w => w.Handle).ToHashSet();
        var app = App.Apps.FirstOrDefault(a => a.ParsingName.EndsWith("charmap.exe", StringComparison.OrdinalIgnoreCase))
                  ?? new AppEntry("Character Map", @"{1AC14E77-02E7-4E5D-B744-2EB1AE5198B7}\charmap.exe");
        AppIndex.Launch(app);
        for (int i = 0; i < 50; i++)
        {
            Thread.Sleep(100);
            tracker.Refresh();
            var fresh = tracker.Windows.FirstOrDefault(w => !before.Contains(w.Handle)
                && w.ExePath?.EndsWith("charmap.exe", StringComparison.OrdinalIgnoreCase) == true);
            if (fresh != null)
            {
                bool matched = App.Dock.Match(fresh) != null;
                WindowTracker.Close(fresh);
                Thread.Sleep(500);
                tracker.Refresh();
                bool gone = tracker.Windows.All(w => w.Handle != fresh.Handle);
                return (gone, $"launched via {app.ShellPath}; tracked after {(i + 1) * 100} ms; matched to app={matched}; closed={gone}");
            }
        }
        return (false, "charmap window did not appear within 5 s");
    }

    static Brush Backdrop()
    {
        try
        {
            if (Registry.GetValue(@"HKEY_CURRENT_USER\Control Panel\Desktop", "WallPaper", null) is string p && File.Exists(p))
            {
                var img = new BitmapImage();
                img.BeginInit();
                img.UriSource = new Uri(p);
                img.DecodePixelWidth = 1920;
                img.CacheOption = BitmapCacheOption.OnLoad;
                img.EndInit();
                img.Freeze();
                return new ImageBrush(img) { Stretch = Stretch.UniformToFill, AlignmentY = AlignmentY.Bottom };
            }
        }
        catch (Exception) { }
        return new LinearGradientBrush(Color.FromRgb(70, 70, 76), Color.FromRgb(24, 24, 26), 90);
    }

    static (bool, string) RenderDock(string path, Brush backdrop, int hover)
    {
        var dock = new DockWindow();
        var root = (FrameworkElement)dock.Content;
        dock.Content = null;
        double frameMs = 0;
        var r = Render(root, 1100, 150, path, backdrop, () =>
        {
            frameMs = dock.MeasureFrameMs(600);
            if (hover >= 0) dock.PreviewHover(hover);
            else dock.PreviewHover(-1);
        }, dock);
        bool dotsOk = dock.RunningDotsShown(out string dots);
        // A 60 Hz frame is 16.7 ms; magnification must stay a small fraction of that.
        return (r.Item1 && dotsOk && frameMs < 2, $"{r.Item2}; {dots}; magnify frame {frameMs:0.000} ms");
    }

    static (bool, string) RenderQuick(string path, Brush backdrop)
    {
        var q = new QuickSettingsWindow();
        q.PrepareForPreview();
        var root = (FrameworkElement)q.Content;
        q.Content = null;
        root.Measure(new Size(396, double.PositiveInfinity));
        return Render(root, 396, Math.Ceiling(root.DesiredSize.Height), path, backdrop, null, q);
    }

    static (bool, string) RenderWindow(Window window, double w, double h, string path, Brush? backdrop)
    {
        var root = (FrameworkElement)window.Content;
        window.Content = null;
        if (window.Background is Brush bg && backdrop == null) root = Wrap(root, bg);
        return Render(root, w, h, path, backdrop, null, window);
    }

    static FrameworkElement Wrap(FrameworkElement content, Brush bg) => new Grid { Background = bg, Children = { content } };

    static (bool, string) RenderIconSheet(string path)
    {
        var panel = new WrapPanel { Width = 760, Margin = new Thickness(20) };
        foreach (var name in IconPack.Names)
        {
            var item = new { Icon = (ImageSource?)null, Glyph = IconPack.Named(name), HasGlyph = true };
            panel.Children.Add(new StackPanel
            {
                Width = 88, Margin = new Thickness(0, 0, 0, 14),
                Children =
                {
                    new ContentControl { Width = 48, Height = 48, Content = item, ContentTemplate = (DataTemplate)Application.Current.Resources["AppIcon"] },
                    new TextBlock { Text = name, FontSize = 11, Margin = new Thickness(0, 6, 0, 0), HorizontalAlignment = HorizontalAlignment.Center,
                                    Foreground = (Brush)Application.Current.Resources["TextDim"] },
                },
            });
        }
        var root = Wrap(panel, (Brush)Application.Current.Resources["SurfaceSolid"]);
        root.Measure(new Size(800, double.PositiveInfinity));
        return Render(root, 800, Math.Ceiling(root.DesiredSize.Height), path, null, null, null);
    }

    static (bool, string) Render(FrameworkElement root, double w, double h, string path, Brush? backdrop, Action? beforeRender, Window? owner)
    {
        var host = new Grid { Width = w, Height = h, Background = backdrop ?? Brushes.Transparent };
        host.Children.Add(root);
        host.Measure(new Size(w, h));
        host.Arrange(new Rect(0, 0, w, h));
        host.UpdateLayout();
        if (beforeRender != null)
        {
            beforeRender();
            host.UpdateLayout();
        }
        const double scale = 1.5;
        var rtb = new RenderTargetBitmap((int)(w * scale), (int)(h * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        rtb.Render(host);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(rtb));
        using (var fs = File.Create(path)) enc.Save(fs);
        owner?.Close();
        return (new FileInfo(path).Length > 1000, Path.GetFileName(path));
    }
}
