using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using Norrsken.Shell.Core;
using Release = Norrsken.Shell.Core.Updater.Release;

namespace Norrsken.Shell.Ui;

/// <summary>
/// The screen shown while an update installs: the version, each step as it happens, and an overall bar.
/// It runs from the new version, so it already looks like the version you're getting.
/// </summary>
public sealed class UpdateScreen : Window
{
    readonly TextBlock _title, _sub;
    readonly Border _fill;
    readonly List<(Grid Mark, TextBlock Label)> _steps = [];
    double _shown;

    public UpdateScreen(string version)
    {
        Title = "Updating Norrsken";
        WindowStyle = WindowStyle.None; AllowsTransparency = true; Background = Brushes.Transparent; ResizeMode = ResizeMode.NoResize;
        Topmost = true; WindowStartupLocation = WindowStartupLocation.CenterScreen; SizeToContent = SizeToContent.WidthAndHeight;
        FontFamily = (FontFamily)Application.Current.FindResource("UiFont");

        var card = new Border { Width = 520, CornerRadius = Theme.R(22), BorderThickness = new Thickness(1), Padding = new Thickness(34, 30, 34, 26), Margin = new Thickness(40) };
        card.SetResourceReference(Border.BackgroundProperty, "SurfaceSolid");
        card.SetResourceReference(Border.BorderBrushProperty, "Border");
        card.Effect = new DropShadowEffect { BlurRadius = 50, ShadowDepth = 12, Direction = 270, Opacity = 0.45 };
        var stack = new StackPanel();
        var head = new StackPanel { Orientation = Orientation.Horizontal };
        head.Children.Add(new Image { Source = Showcase.LogoBitmap(112), Width = 52, Height = 52, Margin = new Thickness(0, 0, 16, 0) });
        var titles = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        _title = Text("Updating Norrsken", 21, bold: true);
        _title.FontFamily = (FontFamily)Application.Current.FindResource("DisplayFont");
        _sub = Text($"Installing version {version}", 13, dim: true);
        titles.Children.Add(_title);
        titles.Children.Add(_sub);
        head.Children.Add(titles);
        stack.Children.Add(head);

        var list = new StackPanel { Margin = new Thickness(4, 24, 0, 22) };
        foreach (var s in Updater.Steps)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 10) };
            var mark = new Grid { Width = 18, Height = 18, Margin = new Thickness(0, 0, 12, 0) };
            var dot = new Ellipse { Width = 6, Height = 6 };
            dot.SetResourceReference(Shape.FillProperty, "TextDim");
            mark.Children.Add(dot);
            var label = Text(s, 13.5, dim: true);
            row.Children.Add(mark);
            row.Children.Add(label);
            list.Children.Add(row);
            _steps.Add((mark, label));
        }
        stack.Children.Add(list);

        var track = new Border { Height = 6, CornerRadius = new CornerRadius(3) };
        track.SetResourceReference(Border.BackgroundProperty, "SurfaceAlt");
        _fill = new Border { Height = 6, CornerRadius = new CornerRadius(3), HorizontalAlignment = HorizontalAlignment.Left, Width = 0 };
        _fill.SetResourceReference(Border.BackgroundProperty, "Accent");
        var bar = new Grid();
        bar.Children.Add(track);
        bar.Children.Add(_fill);
        stack.Children.Add(bar);
        var note = Text("Keep your PC on. Norrsken comes back by itself when it's done.", 12, dim: true);
        note.Margin = new Thickness(0, 12, 0, 0);
        stack.Children.Add(note);
        card.Child = stack;
        Content = card;
        Loaded += (_, _) =>
        {
            Motion.From(card, OpacityProperty, 0, 1, 260);
            var s = new ScaleTransform(0.94, 0.94);
            card.RenderTransformOrigin = new Point(0.5, 0.5);
            card.RenderTransform = s;
            Motion.To(s, ScaleTransform.ScaleXProperty, 1, 420, Motion.SoftSpring);
            Motion.To(s, ScaleTransform.ScaleYProperty, 1, 420, Motion.SoftSpring);
        };
    }

    /// <summary>Step <paramref name="index"/> is underway and <paramref name="fraction"/> done (any thread).</summary>
    public void Step(int index, double fraction) => Dispatcher.BeginInvoke(() =>
    {
        for (int i = 0; i < _steps.Count; i++)
        {
            var (mark, label) = _steps[i];
            string state = i < index ? "done" : i == index ? "now" : "next";
            if ((string?)mark.Tag == state) continue;
            mark.Tag = state;
            mark.Children.Clear();
            label.SetResourceReference(TextBlock.ForegroundProperty, state == "next" ? "TextDim" : "Text");
            label.FontWeight = state == "now" ? FontWeights.SemiBold : FontWeights.Normal;
            if (state == "done") { mark.Children.Add(new GlyphIcon { Glyph = "check", Width = 16, Height = 16 }); Motion.Pop(mark, 0.5); }
            else if (state == "now") mark.Children.Add(Spinner());
            else { var d = new Ellipse { Width = 6, Height = 6 }; d.SetResourceReference(Shape.FillProperty, "TextDim"); mark.Children.Add(d); }
        }
        double overall = Math.Clamp((index + fraction) / _steps.Count, 0, 1);
        if (overall < _shown) return;
        _shown = overall;
        double width = ((FrameworkElement)_fill.Parent).ActualWidth * overall;
        Motion.To(_fill, WidthProperty, width, 300, Motion.Out);
        if (index == _steps.Count - 1 && fraction >= 0.5) { _title.Text = "Done"; _sub.Text = "Starting the new Norrsken…"; }
    });

    public void Failed(string message) => Dispatcher.BeginInvoke(() =>
    {
        _title.Text = "The update didn't finish";
        _sub.Text = "Norrsken is starting again. Nothing of yours was touched.";
        _sub.ToolTip = message;
        Sound.Play("error");
    });

    static FrameworkElement Spinner()
    {
        var arc = new System.Windows.Shapes.Path
        {
            Data = Geometry.Parse("M9,1.5 A7.5,7.5 0 1 1 1.5,9"), StrokeThickness = 2, Width = 18, Height = 18, Stretch = Stretch.None,
            StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, RenderTransformOrigin = new Point(0.5, 0.5),
        };
        arc.SetResourceReference(Shape.StrokeProperty, "Accent");
        var spin = new RotateTransform();
        arc.RenderTransform = spin;
        spin.BeginAnimation(RotateTransform.AngleProperty, new System.Windows.Media.Animation.DoubleAnimation(0, 360, TimeSpan.FromSeconds(0.9)) { RepeatBehavior = System.Windows.Media.Animation.RepeatBehavior.Forever });
        return arc;
    }

    internal static TextBlock Text(string s, double size, bool bold = false, bool dim = false)
    {
        var t = new TextBlock { Text = s, FontSize = size, FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal, TextWrapping = TextWrapping.Wrap };
        t.SetResourceReference(TextBlock.ForegroundProperty, dim ? "TextDim" : "Text");
        return t;
    }
}

/// <summary>
/// The card in the top-right corner that talks about updates: a new version is out, it's ready to install,
/// or it just got installed. It stays until you answer it and never steals focus.
/// </summary>
public sealed class UpdateCard : Window
{
    static UpdateCard? _open;
    readonly StackPanel _body = new();
    readonly Border _card;
    readonly TranslateTransform _shift = new();

    UpdateCard()
    {
        WindowStyle = WindowStyle.None; AllowsTransparency = true; Background = Brushes.Transparent; ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false; Topmost = true; ShowActivated = false; SizeToContent = SizeToContent.WidthAndHeight;
        FontFamily = (FontFamily)Application.Current.FindResource("UiFont");
        _card = new Border { Width = 380, CornerRadius = Theme.R(16), BorderThickness = new Thickness(1), Padding = new Thickness(18, 16, 18, 16), Margin = new Thickness(20), Child = _body, RenderTransform = _shift };
        _card.SetResourceReference(Border.BackgroundProperty, "Surface");
        _card.SetResourceReference(Border.BorderBrushProperty, "DockLine");
        _card.Effect = new DropShadowEffect { BlurRadius = 30, ShadowDepth = 8, Direction = 270, Opacity = 0.35 };
        Content = _card;
        Loaded += (_, _) =>
        {
            var w = SystemParameters.WorkArea;
            Left = w.Right - ActualWidth;
            Top = w.Top;
            Motion.From(_card, OpacityProperty, 0, 1, 220);
            Motion.From(_shift, TranslateTransform.XProperty, 70, 0, 460, Motion.SoftSpring);
        };
    }

    static UpdateCard Open()
    {
        if (_open != null) { _open._body.Children.Clear(); return _open; }
        var c = new UpdateCard();
        c.Closed += (_, _) => { if (_open == c) _open = null; };
        _open = c;
        return c;
    }

    void Leave()
    {
        Motion.To(_shift, TranslateTransform.XProperty, 70, 240, Motion.InOut);
        Motion.To(_card, OpacityProperty, 0, 220, done: Close);
    }

    void Build(string title, string subtitle, string notes, (string Label, Action Click) primary, (string Label, Action Click)? secondary, Release? page = null)
    {
        var head = new StackPanel { Orientation = Orientation.Horizontal };
        head.Children.Add(new Image { Source = Showcase.LogoBitmap(72), Width = 36, Height = 36, Margin = new Thickness(0, 0, 12, 0), VerticalAlignment = VerticalAlignment.Top });
        var t = new StackPanel { Width = 290 };
        t.Children.Add(UpdateScreen.Text(title, 14.5, bold: true));
        var sub = UpdateScreen.Text(subtitle, 12.5, dim: true);
        sub.Margin = new Thickness(0, 2, 0, 0);
        t.Children.Add(sub);
        head.Children.Add(t);
        _body.Children.Add(head);

        var lines = Highlights(notes);
        if (lines.Count > 0)
        {
            var list = new StackPanel { Margin = new Thickness(48, 10, 0, 0) };
            foreach (var l in lines)
            {
                var row = new DockPanel { Margin = new Thickness(0, 0, 0, 4) };
                var dot = new Ellipse { Width = 4, Height = 4, Margin = new Thickness(0, 7, 8, 0), VerticalAlignment = VerticalAlignment.Top };
                dot.SetResourceReference(Shape.FillProperty, "Accent");
                row.Children.Add(dot);
                row.Children.Add(UpdateScreen.Text(l, 12.5));
                list.Children.Add(row);
            }
            _body.Children.Add(list);
        }

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(48, 14, 0, 0) };
        var go = new Button { Style = (Style)FindResource("PrimaryButton"), Content = primary.Label, FontSize = 12.5 };
        go.Click += (_, _) => primary.Click();
        buttons.Children.Add(go);
        if (secondary is { } s)
        {
            var later = new Button { Style = (Style)FindResource("IconButton"), Content = new TextBlock { Text = s.Label, FontSize = 12.5, Margin = new Thickness(12, 6, 12, 6) }, Margin = new Thickness(8, 0, 0, 0) };
            later.Click += (_, _) => s.Click();
            buttons.Children.Add(later);
        }
        if (page != null)
        {
            var link = new TextBlock { FontSize = 12.5, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0) };
            var h = new Hyperlink(new Run("What's new")) { TextDecorations = null };
            h.SetResourceReference(TextElement.ForegroundProperty, "TextDim");
            h.Click += (_, _) => OpenPage(Updater.ReleasePage(page));
            link.Inlines.Add(h);
            buttons.Children.Add(link);
        }
        _body.Children.Add(buttons);
        Motion.Stagger(_body.Children.Cast<UIElement>(), stepMs: 40, max: 4, rise: 6, startMs: 80);
    }

    /// <summary>A few readable lines from a release's notes (the bullet points, without the markdown).</summary>
    static List<string> Highlights(string notes)
    {
        var lines = new List<string>();
        foreach (var raw in notes.Split('\n'))
        {
            string l = raw.Trim();
            if (!l.StartsWith("- ")) continue;
            l = Regex.Replace(l[2..], @"\*\*|\*|`", "");
            l = Regex.Replace(l, @"\[([^\]]+)\]\([^)]+\)", "$1");
            if (l.Length > 110) l = l[..l.LastIndexOf(' ', 107)] + "…";
            lines.Add(l);
            if (lines.Count == 3) break;
        }
        return lines;
    }

    static void OpenPage(string url)
    {
        if (App.TestMode) { App.TestLaunches.Add(url); return; }
        try { Unelevated.Start(url); } catch (Exception) { try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); } catch (Exception) { } }
    }

    // ───────────────────────── the three moments ─────────────────────────

    /// <summary>A new version is out (automatic updates are off).</summary>
    public static void Available(Release rel)
    {
        var c = Open();
        c.Build($"Norrsken {rel.Version} is out", "Download it now and install it whenever you like.", rel.Notes,
            ("Download", async () =>
            {
                c._body.Children.Clear();
                c.Build($"Downloading Norrsken {rel.Version}…", "You can keep working.", "", ("Hide", c.Leave), null);
                try { await Updater.DownloadAsync(rel); Ready(rel); }
                catch (Exception ex) { c._body.Children.Clear(); c.Build("The download didn't work", ex.Message, "", ("Close", c.Leave), null); }
            }),
            ("Later", c.Leave), rel);
        if (!c.IsVisible) c.Show();
    }

    /// <summary>Downloaded and checked: restart to install, or it installs at the next sign-in.</summary>
    public static void Ready(Release rel)
    {
        var c = Open();
        c.Build($"Norrsken {rel.Version} is ready", "Restart Norrsken to install it. It only takes a moment. Or leave it, and it'll install the next time you sign in.", rel.Notes,
            ("Restart now", () => { if (!Updater.InstallNow()) c.Leave(); }),
            ("Later", c.Leave), rel);
        if (!c.IsVisible) c.Show();
        Sound.Play("notify");
    }

    /// <summary>Right after an update: what's new, and a restart of the PC just to be safe.</summary>
    public static void Updated(Updater.AppliedInfo info)
    {
        var c = Open();
        var rel = new Release(Version.TryParse(info.To, out var v) ? v : new Version(0, 0), "v" + info.To, "", 0, null, info.Notes);
        c.Build($"Norrsken is updated to {info.To}", $"You were on {info.From}. Restart your PC when it suits you, so everything picks up the new version.", info.Notes,
            ("Restart PC", () =>
            {
                if (App.TestMode) { App.TestLaunches.Add("restart-pc"); return; }
                try { Process.Start(new ProcessStartInfo("shutdown.exe", "/r /t 2") { UseShellExecute = false, CreateNoWindow = true }); } catch (Exception ex) { Log.Write("Restart: " + ex.Message); }
            }),
            ("Not now", c.Leave), rel);
        if (!c.IsVisible) c.Show();
    }

    internal static UpdateCard? Current => _open;
}
