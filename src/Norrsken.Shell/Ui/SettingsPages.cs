using System.Diagnostics;
using System.Management;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using Microsoft.Win32;
using Norrsken.Shell.Core;
using Windows.Devices.Radios;

namespace Norrsken.Shell.Ui;

/// <summary>
/// The system pages of Norrsken Settings. The everyday controls are native here (brightness,
/// volume, Wi-Fi and Bluetooth radios, power mode, installed apps with uninstall, time zone,
/// Game Mode, storage, device details); each page ends with links into Windows' own pages for
/// the rarely used rest, so nothing becomes unreachable.
/// </summary>
public partial class SettingsWindow
{
    sealed record Page(string Id, string Title, string Glyph, string Keywords, Func<FrameworkElement> Build);
    readonly Dictionary<string, FrameworkElement> _pages = new();
    List<Page> _pageList = new();

    /// <summary>Every page's id, title and search words, for the launcher.</summary>
    public static IEnumerable<(string Id, string Title, string Keywords)> PageIndex =>
    [
        ("personal", "Personalisation", "theme wallpaper dock colour dark light cursor pointer title bar motion keyboard shortcuts background"),
        ("customise", "Customise", "accent colour color corners shape round square dock size magnification glass transparency animation speed motion springy smooth snappy sound sounds sfx effects"),
        ("display", "Display", "brightness night light scale resolution monitor hdr refresh screen"),
        ("sound", "Sound", "volume mute speakers headphones microphone output input audio"),
        ("network", "Network & internet", "wifi wi-fi ethernet vpn airplane proxy hotspot internet"),
        ("bluetooth", "Bluetooth & devices", "bluetooth mouse keyboard printer controller devices pair"),
        ("apps", "Apps", "installed uninstall remove programs default startup install"),
        ("power", "Power & battery", "battery power mode performance sleep shutdown restart lock"),
        ("storage", "Storage", "disk drive space clean recycle"),
        ("gaming", "Gaming", "game mode game bar xbox"),
        ("time", "Time & language", "time zone clock date language region keyboard layout"),
        ("accounts", "Accounts", "user account password sign picture pin"),
        ("privacy", "Privacy & security", "privacy security defender antivirus virus location camera permissions"),
        ("update", "Updates", "update upgrade patch windows norrsken flickerbox github"),
        ("about", "About", "about device name cpu processor memory ram gpu graphics version activation specs"),
    ];

    void BuildPages()
    {
        _pageList =
        [
            new("personal", "Personalisation", "M12,3.5 A8.5,8.5 0 1 1 11.99,3.5 Z M12,3.5 V20.5 M12,7 H16 M12,11 H19.5 M12,15 H19 M12,19 H15", "theme wallpaper dock colour dark light cursor pointer title bar motion keyboard shortcuts", () => NorrskenPage),
            new("customise", "Customise", "M4,7 H11 M15,7 H20 M13,4.5 V9.5 M4,17 H7 M11,17 H20 M9,14.5 V19.5", "accent colour corners shape dock size magnification glass animation speed motion style sound effects sfx", CustomisePage),
            new("display", "Display", "M3.5,5 H20.5 V16 H3.5 Z M9,20 H15 M12,16 V20", "brightness night light scale resolution monitor hdr refresh", DisplayPage),
            new("sound", "Sound", "M4,9.5 H7.5 L12,5.5 V18.5 L7.5,14.5 H4 Z M15.5,9 A4,4 0 0 1 15.5,15 M18,6.5 A7.5,7.5 0 0 1 18,17.5", "volume mute speakers headphones microphone output input", SoundPage),
            new("network", "Network & internet", "M3,9.5 A13,13 0 0 1 21,9.5 M6,12.8 A8.5,8.5 0 0 1 18,12.8 M9,16 A4.2,4.2 0 0 1 15,16 M12,19.2 H12.1", "wifi wi-fi ethernet vpn airplane proxy hotspot", NetworkPage),
            new("bluetooth", "Bluetooth & devices", "M7,7.5 L17,16.5 L12,21 V3 L17,7.5 L7,16.5", "bluetooth mouse keyboard printer controller devices pair", BluetoothPage),
            new("apps", "Apps", "M4,4 H10 V10 H4 Z M14,4 H20 V10 H14 Z M4,14 H10 V20 H4 Z M14,14 H20 V20 H14 Z", "installed uninstall remove programs default startup install steam", AppsPage),
            new("power", "Power & battery", "M3.5,8 H18 V16 H3.5 Z M20.5,10.5 V13.5 M6,10.5 V13.5", "battery power mode performance sleep shut down restart lock", PowerPage),
            new("storage", "Storage", "M4,13 H20 V19 H4 Z M4,13 L6.5,6 H17.5 L20,13 M16.5,16 H16.6", "disk drive space clean recycle bin", StoragePage),
            new("gaming", "Gaming", "M7,8.5 H17 A4,4 0 0 1 21,12.5 V14.5 A2.5,2.5 0 0 1 16.8,16.3 L15,14.5 H9 L7.2,16.3 A2.5,2.5 0 0 1 3,14.5 V12.5 A4,4 0 0 1 7,8.5 Z", "game mode game bar xbox fullscreen", GamingPage),
            new("time", "Time & language", "M12,3.5 A8.5,8.5 0 1 1 11.99,3.5 Z M12,7.5 V12 L15,14", "time zone clock date language region keyboard layout", TimePage),
            new("accounts", "Accounts", "M9,11 A3,3 0 1 1 8.99,11 Z M3.5,20 C3.5,16.5 6,14.5 9,14.5 C12,14.5 14.5,16.5 14.5,20", "user account password sign in picture family", AccountsPage),
            new("privacy", "Privacy & security", "M12,3 L19.5,6 V11.5 C19.5,16 16.3,19.3 12,21 C7.7,19.3 4.5,16 4.5,11.5 V6 Z", "privacy security defender antivirus location camera microphone permissions", PrivacyPage),
            new("update", "Updates", "M20,12 A8,8 0 1 1 17.7,6.3 M20,4 V8 H16", "update upgrade patch windows norrsken flickerbox github", UpdatePage),
            new("about", "About", "M12,3.5 A8.5,8.5 0 1 1 11.99,3.5 Z M12,10.5 V16.5 M12,7.5 H12.1", "about device name cpu processor memory ram gpu graphics windows version activation", AboutPage),
        ];
        _pages["personal"] = NorrskenPage;
        foreach (var p in _pageList) NavList.Items.Add(NavRow(p));
        NavList.SelectedIndex = 0;
    }

    internal void ShowPage(string id)
    {
        var item = NavList.Items.Cast<ListBoxItem>().FirstOrDefault(i => (string)i.Tag == id);
        if (item != null) NavList.SelectedItem = item;
    }

    internal IEnumerable<string> PageIds => _pageList.Select(p => p.Id);
    internal FrameworkElement? CurrentPage => PageHost.Children.Cast<FrameworkElement>().FirstOrDefault(c => c.Visibility == Visibility.Visible);

    ListBoxItem NavRow(Page p)
    {
        var glyph = new System.Windows.Shapes.Path { Data = Geometry.Parse(p.Glyph), StrokeThickness = 1.6, Width = 16, Height = 16, Stretch = Stretch.Uniform, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, StrokeLineJoin = PenLineJoin.Round, Margin = new Thickness(0, 0, 12, 0) };
        glyph.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, "TextDim");
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(glyph);
        row.Children.Add(new TextBlock { Text = p.Title, FontSize = 13, VerticalAlignment = VerticalAlignment.Center });
        return new ListBoxItem { Content = row, Tag = p.Id };
    }

    void Nav_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (NavList.SelectedItem is not ListBoxItem { Tag: string id }) return;
        var page = _pageList.First(p => p.Id == id);
        if (!_pages.TryGetValue(id, out var el))
        {
            try { el = page.Build(); }
            catch (Exception ex) { el = Section(page.Title); ((StackPanel)el).Children.Add(Note("This page couldn't load: " + ex.Message)); }
            _pages[id] = el;
            PageHost.Children.Add(el);
        }
        foreach (FrameworkElement c in PageHost.Children) c.Visibility = c == el ? Visibility.Visible : Visibility.Collapsed;
        PageTitleText.Text = page.Title;
        Body.ScrollToTop();
        Motion.From(Body, OpacityProperty, 0, 1, 160);
        if (el is Panel panel) Motion.Stagger(panel.Children.Cast<UIElement>(), stepMs: 26, max: 10, rise: 10, ms: 340);
        else Motion.From(BodyShift, TranslateTransform.YProperty, 10, 0, 300, Motion.Out);
    }

    void FindBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        string q = FindBox.Text.Trim();
        FindHint.Visibility = q.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        foreach (ListBoxItem item in NavList.Items)
        {
            var p = _pageList.First(x => x.Id == (string)item.Tag);
            bool match = q.Length == 0 || p.Title.Contains(q, StringComparison.CurrentCultureIgnoreCase) || p.Keywords.Contains(q, StringComparison.CurrentCultureIgnoreCase);
            item.Visibility = match ? Visibility.Visible : Visibility.Collapsed;
        }
        if (q.Length > 0 && NavList.Items.Cast<ListBoxItem>().FirstOrDefault(i => i.Visibility == Visibility.Visible) is { } first) NavList.SelectedItem = first;
    }

    // ───────────────────────── building blocks ─────────────────────────

    StackPanel Section(string title)
    {
        var s = new StackPanel();
        s.Children.Add(new TextBlock { Text = title, FontFamily = (FontFamily)FindResource("DisplayFont"), FontSize = 28, FontWeight = FontWeights.SemiBold, Margin = new Thickness(4, 4, 0, 6) });
        return s;
    }

    TextBlock Heading(string text) => new() { Text = text, Style = (Style)FindResource("PageTitle") };
    TextBlock Note(string text) => new() { Text = text, Style = (Style)FindResource("Note") };

    Border Group(params UIElement[] rows)
    {
        var stack = new StackPanel();
        for (int i = 0; i < rows.Length; i++)
        {
            if (i > 0) stack.Children.Add(new Border { Style = (Style)FindResource("Hairline") });
            stack.Children.Add(rows[i]);
        }
        return new Border { Style = (Style)FindResource("Group"), Child = stack };
    }

    DockPanel Row(string title, string? subtitle, UIElement? control)
    {
        var row = new DockPanel { Margin = new Thickness(0, 11, 0, 11), LastChildFill = true };
        if (control != null) { DockPanel.SetDock(control, Dock.Right); row.Children.Add(control); }
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) };
        text.Children.Add(new TextBlock { Text = title, FontSize = 13.5, TextWrapping = TextWrapping.Wrap });
        if (subtitle != null)
        {
            var sub = new TextBlock { Text = subtitle, FontSize = 12, TextWrapping = TextWrapping.Wrap };
            sub.SetResourceReference(TextBlock.ForegroundProperty, "TextDim");
            text.Children.Add(sub);
        }
        row.Children.Add(text);
        return row;
    }

    Button Action(string text, Action click, bool primary = false)
    {
        var b = new Button { Content = text, Style = (Style)FindResource(primary ? "PrimaryButton" : "LinkButton"), FontSize = 12.5, VerticalAlignment = VerticalAlignment.Center };
        if (!primary) b.Content = new TextBlock { Text = text + "  ›", FontSize = 12.5 };
        b.Click += (_, _) => click();
        return b;
    }

    CheckBox Toggle(string title, bool on, Action<bool> changed)
    {
        var cb = new CheckBox { Style = (Style)FindResource("Toggle"), Content = title, IsChecked = on };
        bool ready = false;
        cb.Checked += (_, _) => { if (ready) changed(true); };
        cb.Unchecked += (_, _) => { if (ready) changed(false); };
        cb.Loaded += (_, _) => ready = true;
        return cb;
    }

    Slider SliderFor(double value, Action<double> changed)
    {
        var s = new Slider { Minimum = 0, Maximum = 100, Value = value, Width = 260, Style = (Style)FindResource("NSlider"), VerticalAlignment = VerticalAlignment.Center, IsMoveToPointEnabled = true };
        s.ValueChanged += (_, e) => changed(e.NewValue);
        return s;
    }

    /// <summary>"More in Windows": rows that open Windows' own page for things Norrsken doesn't redo.</summary>
    Border Links(params (string Title, string Uri)[] links) =>
        Group(links.Select(l => (UIElement)Row(l.Title, null, Action("Open", () => OpenUri(l.Uri)))).ToArray());

    static void OpenUri(string uri)
    {
        if (App.TestMode) { App.TestLaunches.Add(uri); return; }
        try { Unelevated.Start(uri); }
        catch (Exception ex) { Log.Write($"{uri}: {ex.Message}"); }
    }

    static string Run(string exe, string args)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo(exe, args) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true })!;
            string o = p.StandardOutput.ReadToEnd();
            p.WaitForExit(8000);
            return o;
        }
        catch (Exception ex) { return "error: " + ex.Message; }
    }

    // ───────────────────────── Customise: accent, shape, dock, motion, sound ─────────────────────────

    /// <summary>Saves, re-applies the look here and in the dock, and tells open Norrsken apps to follow.</summary>
    void LookChanged()
    {
        App.Settings.Save();
        Theme.Crossfade(() => { Theme.Apply(App.Settings.Theme); _dock?.ApplySettings(); });
        NorrskenApps.BroadcastTheme();
    }

    readonly DispatcherTimer _lookDebounce = new() { Interval = TimeSpan.FromMilliseconds(140) };

    /// <summary>Sliders re-apply the look a moment after they stop moving, not on every pixel.</summary>
    void LookChangedSoon()
    {
        _lookDebounce.Stop();
        _lookDebounce.Tick -= LookTick;
        _lookDebounce.Tick += LookTick;
        _lookDebounce.Start();
    }

    void LookTick(object? sender, EventArgs e) { _lookDebounce.Stop(); LookChanged(); }

    /// <summary>A row of mutually exclusive choices, like a segmented control.</summary>
    FrameworkElement Choices(string[] labels, int selected, Action<int> changed)
    {
        var row = new WrapPanel { VerticalAlignment = VerticalAlignment.Center };
        string group = "g" + Guid.NewGuid().ToString("N");
        for (int i = 0; i < labels.Length; i++)
        {
            int index = i;
            var rb = new RadioButton { GroupName = group, IsChecked = i == selected, Style = (Style)FindResource("AppChip"), Margin = new Thickness(0, 0, 8, 0), Padding = new Thickness(14, 6, 14, 6), Content = new TextBlock { Text = labels[i], FontSize = 12.5 } };
            rb.Checked += (_, _) => { if (rb.IsLoaded) changed(index); };
            row.Children.Add(rb);
        }
        return row;
    }

    FrameworkElement Range(double value, double min, double max, Action<double> changed, string left, string right)
    {
        var panel = new DockPanel { Width = 300, VerticalAlignment = VerticalAlignment.Center };
        var l = new TextBlock { Text = left, FontSize = 11.5, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
        var r = new TextBlock { Text = right, FontSize = 11.5, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) };
        l.SetResourceReference(TextBlock.ForegroundProperty, "TextDim");
        r.SetResourceReference(TextBlock.ForegroundProperty, "TextDim");
        DockPanel.SetDock(l, Dock.Left);
        DockPanel.SetDock(r, Dock.Right);
        panel.Children.Add(l);
        panel.Children.Add(r);
        var s = SliderFor((value - min) / (max - min) * 100, v => changed(min + (max - min) * v / 100));
        s.Width = double.NaN;
        panel.Children.Add(s);
        return panel;
    }

    FrameworkElement CustomisePage()
    {
        var s = Section("Customise");
        var set = App.Settings;

        // ── Accent ──
        s.Children.Add(Heading("Accent colour"));
        s.Children.Add(Note("Switches, selections, buttons, badges and the dock's running dots. Norrsken is monochrome until you pick one."));
        var swatches = new WrapPanel { Margin = new Thickness(4, 10, 0, 6) };
        var custom = new TextBox { Width = 110, Text = set.Accent.StartsWith('#') ? set.Accent : "", ToolTip = "Any colour as #RRGGBB" };
        void Paint()
        {
            foreach (FrameworkElement el in swatches.Children)
                if (el is Button { Tag: string id, Content: Grid g } && g.Children[^1] is Ellipse ring)
                    ring.Opacity = id == set.Accent || (id == "custom" && set.Accent.StartsWith('#')) ? 1 : 0;
        }
        foreach (var (id, name, hex) in Theme.Accents)
        {
            var g = new Grid { Width = 34, Height = 34 };
            var fill = new Ellipse { Width = 26, Height = 26 };
            if (hex.Length > 0) fill.Fill = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
            else { fill.SetResourceReference(Shape.FillProperty, "Text"); fill.StrokeThickness = 1; fill.SetResourceReference(Shape.StrokeProperty, "Border"); }
            var ring = new Ellipse { Width = 34, Height = 34, StrokeThickness = 2, Opacity = 0 };
            ring.SetResourceReference(Shape.StrokeProperty, "Text");
            g.Children.Add(fill);
            g.Children.Add(ring);
            var b = new Button { Style = (Style)FindResource("IconButton"), Content = g, Tag = id, ToolTip = name, Margin = new Thickness(0, 0, 6, 6), Width = 40, Height = 40 };
            b.Click += (_, _) => { set.Accent = id; Paint(); LookChanged(); Motion.Pop(g, 0.8); };
            swatches.Children.Add(b);
        }
        Paint();
        custom.LostFocus += (_, _) =>
        {
            string v = custom.Text.Trim();
            if (v.Length == 0) return;
            if (!v.StartsWith('#')) v = "#" + v;
            try { ColorConverter.ConvertFromString(v); set.Accent = v; Paint(); LookChanged(); }
            catch (FormatException) { custom.Text = ""; }
        };
        s.Children.Add(swatches);
        s.Children.Add(Group(Row("Your own colour", "Type a hex colour, like #7AA2F7, and press Tab.", custom)));

        // ── Shape ──
        s.Children.Add(Heading("Shape"));
        double[] corners = [0, 0.5, 1, 1.5];
        int ci = Array.FindIndex(corners, c => Math.Abs(c - set.CornerScale) < 0.01);
        s.Children.Add(Group(Row("Corners", "Windows, cards, buttons and the dock.", Choices(["Square", "Subtle", "Norrsken", "Round"], ci < 0 ? 2 : ci, i => { set.CornerScale = corners[i]; LookChanged(); }))));

        // ── Dock ──
        s.Children.Add(Heading("Dock"));
        s.Children.Add(Group(
            Row("Size", null, Range(set.DockScale, 0.75, 1.4, v => { set.DockScale = Math.Round(v, 2); LookChangedSoon(); }, "Small", "Large")),
            Row("Magnification", "How much icons grow under the pointer.", Range(set.MagnifyAmount, 0.1, 1.0, v => { set.MagnifyAmount = Math.Round(v, 2); LookChangedSoon(); }, "Subtle", "Big")),
            Row("Glass", "How see-through the dock is.", Range(set.DockOpacity, 0.4, 1.0, v => { set.DockOpacity = Math.Round(v, 2); LookChangedSoon(); }, "Clear", "Solid"))));

        // ── Motion ──
        s.Children.Add(Heading("Motion"));
        string[] styles = ["natural", "springy", "smooth", "snappy"];
        var demo = new Border { Width = 34, Height = 34, CornerRadius = Theme.R(10), Margin = new Thickness(0, 0, 4, 0) };
        demo.SetResourceReference(Border.BackgroundProperty, "Accent");
        var demoShift = new TranslateTransform();
        demo.RenderTransform = demoShift;
        void Play()
        {
            Motion.From(demoShift, TranslateTransform.XProperty, -60, 0, 520, Motion.Spring);
            Motion.Pop(demo, 0.6);
        }
        var tryIt = new Button { Style = (Style)FindResource("LinkButton"), Content = new TextBlock { Text = "Try it  ›", FontSize = 12.5 }, Margin = new Thickness(12, 0, 0, 0) };
        tryIt.Click += (_, _) => Play();
        var demoRow = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        demoRow.Children.Add(demo);
        demoRow.Children.Add(tryIt);
        s.Children.Add(Group(
            Row("Style", "How things move and land.", Choices(["Natural", "Springy", "Smooth", "Snappy"], Math.Max(0, Array.IndexOf(styles, set.MotionStyle)), i => { set.MotionStyle = styles[i]; LookChanged(); Play(); })),
            Row("Speed", null, Range(set.AnimationScale, 0.5, 1.6, v => { set.AnimationScale = Math.Round(v, 2); LookChangedSoon(); }, "Faster", "Slower")),
            Row("Preview", null, demoRow)));

        // ── Sound ──
        s.Children.Add(Heading("Sounds"));
        bool pack = Sound.HasPack;
        s.Children.Add(Note(pack ? "Quiet sounds for things Norrsken does. Off until you turn them on." : "No sound pack was found. Choose a folder with files named after the events below (launcher.wav, trash.wav, …)."));
        var events = new StackPanel { IsEnabled = set.Sounds };
        var master = Toggle("Interface sounds", set.Sounds, on => { set.Sounds = on; events.IsEnabled = on; LookChanged(); if (on) Sound.Preview("toggle"); });
        var volume = Range(set.SoundVolume, 0, 1, v => { set.SoundVolume = Math.Round(v, 2); LookChangedSoon(); }, "Quiet", "Loud");
        s.Children.Add(Group(master, Row("Volume", null, volume)));
        var rows = new List<UIElement>();
        foreach (var (id, label) in Sound.Events)
        {
            string ev = id;
            var line = new DockPanel();
            var play = new Button { Style = (Style)FindResource("IconButton"), Width = 30, Height = 30, ToolTip = "Play", Margin = new Thickness(10, 0, 0, 0), IsEnabled = Sound.FileFor(id) != null, Content = new GlyphIcon { Glyph = "play", Width = 13, Height = 13 } };
            play.Click += (_, _) => Sound.Preview(ev);
            DockPanel.SetDock(play, Dock.Right);
            line.Children.Add(play);
            line.Children.Add(Toggle(label + (Sound.QuietByDefault.Contains(id) ? "  (off by default: it plays often)" : ""), Sound.IsOn(id), on =>
            {
                if (on) set.MutedSounds.Remove(ev); else if (!set.MutedSounds.Contains(ev)) set.MutedSounds.Add(ev);
                LookChanged();
            }));
            rows.Add(line);
        }
        events.Children.Add(Group(rows.ToArray()));
        s.Children.Add(events);
        var folder = Status(set.SoundPack.Length > 0 ? "Sound pack: " + set.SoundPack : "Sound pack: Norrsken's own");
        s.Children.Add(Group(Row("Sound pack", "A folder of .wav files named after the events. Missing ones stay silent.", Action("Choose", () =>
        {
            var dlg = new Microsoft.Win32.OpenFolderDialog { Title = "Choose a sound pack folder" };
            if (dlg.ShowDialog() == true) { set.SoundPack = dlg.FolderName; folder.Text = "Sound pack: " + dlg.FolderName; LookChanged(); }
        })), Row("Norrsken's own pack", null, Action("Use", () => { set.SoundPack = ""; folder.Text = "Sound pack: Norrsken's own"; LookChanged(); }))));
        s.Children.Add(folder);
        return s;
    }

    // ───────────────────────── pages (everything here works in place; nothing opens Windows Settings) ─────────────────────────
    ComboBox Combo(IEnumerable<string> items, string? selected, Action<int> changed, double width = 240)
    {
        var c = new ComboBox { Width = width, VerticalAlignment = VerticalAlignment.Center };
        foreach (var i in items) c.Items.Add(i);
        c.SelectedItem = selected;
        c.SelectionChanged += (_, _) => { if (c.IsLoaded && c.SelectedIndex >= 0) changed(c.SelectedIndex); };
        return c;
    }

    TextBlock Status(string text) { var t = Note(text); t.Margin = new Thickness(4, 6, 0, 0); return t; }

    FrameworkElement DisplayPage()
    {
        var s = Section("Display");
        int? b = SystemStatus.GetBrightness();
        s.Children.Add(Heading("Brightness"));
        s.Children.Add(Group(b is { } level
            ? Row("Built-in display", null, SliderFor(level, v => { if (!App.TestMode) SystemStatus.SetBrightness((int)v); }))
            : Row("Brightness", "This display sets its brightness with its own buttons.", null)));

        s.Children.Add(Heading("Night light"));
        var warmth = SliderFor(App.Settings.NightLightWarmth * 100, v => { App.Settings.NightLightWarmth = v / 100; SaveQuiet(); DisplayControl.ApplyNightLight(force: true); });
        s.Children.Add(Group(
            Toggle("Night light (warmer colours, easier on the eyes)", App.Settings.NightLight, on => { App.Settings.NightLight = on; SaveQuiet(); DisplayControl.ApplyNightLight(force: true); }),
            Toggle("Only in the evening (21:00–07:00)", App.Settings.NightLightScheduled, on => { App.Settings.NightLightScheduled = on; SaveQuiet(); DisplayControl.ApplyNightLight(force: true); }),
            Row("Warmth", null, warmth)));

        s.Children.Add(Heading("Resolution and refresh rate"));
        var modes = DisplayControl.Modes();
        var cur = DisplayControl.Current();
        var sizes = modes.Select(m => m.ToString()).Distinct().ToList();
        var status = Status("");
        ComboBox? hz = null;
        void FillHz(string size)
        {
            hz!.Items.Clear();
            foreach (var m in modes.Where(m => m.ToString() == size).Select(m => m.Hz).Distinct().OrderByDescending(x => x)) hz.Items.Add($"{m} Hz");
            hz.SelectedItem = cur != null && cur.ToString() == size ? $"{cur.Hz} Hz" : hz.Items.Count > 0 ? hz.Items[0] : null;
        }
        var res = Combo(sizes, cur?.ToString(), _ => { }, 200);
        hz = Combo([], null, _ => { }, 120);
        FillHz(cur?.ToString() ?? sizes.FirstOrDefault() ?? "");
        res.SelectionChanged += (_, _) => { if (res.SelectedItem is string sz) FillHz(sz); };
        var apply = Action("Apply", () =>
        {
            if (res.SelectedItem is not string sz || hz.SelectedItem is not string h) return;
            var m = modes.FirstOrDefault(x => x.ToString() == sz && $"{x.Hz} Hz" == h);
            if (m == null) return;
            var before = DisplayControl.Current();
            if (!DisplayControl.Apply(m)) { status.Text = "The display didn't accept that mode."; return; }
            // Keep it only if you confirm, like Windows does.
            if (!App.TestMode && MessageBox.Show(this, "Keep this display setting? It goes back in 15 seconds if you don't answer… (choose No to go back now)", "Display", MessageBoxButton.YesNo) != MessageBoxResult.Yes && before != null)
                DisplayControl.Apply(before);
            status.Text = "";
        }, primary: true);
        var pick = new StackPanel { Orientation = Orientation.Horizontal };
        pick.Children.Add(res); pick.Children.Add(new Border { Width = 8 }); pick.Children.Add(hz);
        s.Children.Add(Group(Row("Main display", cur != null ? $"Now {cur} at {cur.Hz} Hz" : null, pick), Row("", null, apply)));
        s.Children.Add(status);

        var (hdrOk, hdrOn) = DisplayControl.Hdr();
        if (hdrOk)
        {
            s.Children.Add(Heading("HDR"));
            s.Children.Add(Group(Toggle("Use HDR", hdrOn, DisplayControl.SetHdr)));
        }

        s.Children.Add(Heading("Graphics for each app"));
        var prefs = DisplayControl.GpuPreferences();
        var gpuRows = prefs.Select(p => (UIElement)Row(System.IO.Path.GetFileNameWithoutExtension(p.Exe), p.Exe,
            Combo(["Let Windows decide", "Power saving", "High performance"], new[] { "Let Windows decide", "Power saving", "High performance" }[Math.Clamp(p.Pref, 0, 2)], i => DisplayControl.SetGpuPreference(p.Exe, i), 190))).ToList();
        gpuRows.Add(Row("Add an app", "Pick a game or program to always run on the fast graphics card.", Action("Add…", () =>
        {
            var dlg = new Microsoft.Win32.OpenFileDialog { Filter = "Programs (*.exe)|*.exe" };
            if (dlg.ShowDialog(this) == true) { DisplayControl.SetGpuPreference(dlg.FileName, 2); ReloadPage("display"); }
        })));
        s.Children.Add(Group(gpuRows.ToArray()));
        return s;
    }

    static void SaveQuiet() { try { App.Settings.Save(); } catch (IOException) { } }

    void ReloadPage(string id)
    {
        if (_pages.Remove(id, out var old)) PageHost.Children.Remove(old);
        NavList.SelectedItem = null;
        ShowPage(id);
    }

    FrameworkElement SoundPage()
    {
        var s = Section("Sound");
        try { SystemStatus.Start(); } catch (Exception) { }
        s.Children.Add(Heading("Output"));
        var outs = AudioControl.Devices(output: true);
        s.Children.Add(Group(
            Row("Play sound through", null, Combo(outs.Select(d => d.Name), outs.FirstOrDefault(d => d.IsDefault)?.Name, i => AudioControl.SetDefault(outs[i].Id), 280)),
            Row("Volume", null, SliderFor(SystemStatus.Volume, v => { if (!App.TestMode) SystemStatus.Volume = v; })),
            Toggle("Mute", SystemStatus.Muted, on => { if (!App.TestMode) SystemStatus.Muted = on; })));
        s.Children.Add(Heading("Input"));
        var ins = AudioControl.Devices(output: false);
        s.Children.Add(Group(ins.Count > 0
            ? Row("Microphone", null, Combo(ins.Select(d => d.Name), ins.FirstOrDefault(d => d.IsDefault)?.Name, i => AudioControl.SetDefault(ins[i].Id), 280))
            : Row("Microphone", "No microphone connected.", null)));
        s.Children.Add(Heading("Apps"));
        var sessions = AudioControl.Sessions();
        s.Children.Add(Group(sessions.Count > 0
            ? sessions.Select(x => (UIElement)Row(x.Name, null, SliderFor(x.Volume * 100, v => AudioControl.SetVolume(x, (float)(v / 100))))).ToArray()
            : [Row("No apps are playing sound right now.", null, null)]));
        return s;
    }

    FrameworkElement NetworkPage()
    {
        var s = Section("Network & internet");
        var net = SystemStatus.Network();
        s.Children.Add(Group(Row(net.Online ? "Connected" : "Not connected", net.Label, null)));
        s.Children.Add(Heading("Wi-Fi"));
        var radios = new StackPanel();
        s.Children.Add(new Border { Style = (Style)FindResource("Group"), Child = radios });
        _ = FillRadios(radios, RadioKind.WiFi, "Wi-Fi");
        var list = new StackPanel();
        s.Children.Add(Heading("Networks"));
        s.Children.Add(new Border { Style = (Style)FindResource("Group"), Child = list });
        _ = FillWifi(list);
        s.Children.Add(Heading("Proxy"));
        using var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Internet Settings");
        bool proxy = (k?.GetValue("ProxyEnable") as int?) == 1;
        var server = new TextBox { Text = k?.GetValue("ProxyServer") as string ?? "", Width = 240, VerticalAlignment = VerticalAlignment.Center, Padding = new Thickness(8, 5, 8, 5) };
        s.Children.Add(Group(
            Toggle("Use a proxy server", proxy, on => SetProxy(on, server.Text)),
            Row("Address and port", "For example 127.0.0.1:8080", server)));
        server.LostFocus += (_, _) => SetProxy(proxy, server.Text);
        return s;
    }

    static void SetProxy(bool on, string server)
    {
        if (App.TestMode) return;
        using var k = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Internet Settings");
        k.SetValue("ProxyEnable", on ? 1 : 0, RegistryValueKind.DWord);
        if (server.Length > 0) k.SetValue("ProxyServer", server);
    }

    async Task FillWifi(StackPanel host)
    {
        try
        {
            if (await Windows.Devices.WiFi.WiFiAdapter.RequestAccessAsync() != Windows.Devices.WiFi.WiFiAccessStatus.Allowed) throw new InvalidOperationException("Windows didn't allow Wi-Fi access");
            var adapters = await Windows.Devices.WiFi.WiFiAdapter.FindAllAdaptersAsync();
            if (adapters.Count == 0) { host.Children.Add(Row("No Wi-Fi adapter", null, null)); return; }
            var a = adapters[0];
            await a.ScanAsync();
            var connected = await a.NetworkAdapter.GetConnectedProfileAsync();
            foreach (var n in a.NetworkReport.AvailableNetworks.Where(n => n.Ssid.Length > 0).GroupBy(n => n.Ssid).Select(g => g.OrderByDescending(x => x.SignalBars).First()).OrderByDescending(n => n.SignalBars).Take(15))
            {
                bool isOn = connected?.WlanConnectionProfileDetails?.GetConnectedSsid() == n.Ssid;
                bool open = n.SecuritySettings.NetworkAuthenticationType == Windows.Networking.Connectivity.NetworkAuthenticationType.Open80211;
                string sub = $"{new string('▮', n.SignalBars)}{new string('▯', 4 - Math.Min(4, (int)n.SignalBars))}  ·  {(open ? "Open" : "Secured")}";
                if (host.Children.Count > 0) host.Children.Add(new Border { Style = (Style)FindResource("Hairline") });
                var net = n;
                host.Children.Add(Row(n.Ssid, isOn ? "Connected  ·  " + sub : sub, isOn
                    ? Action("Disconnect", () => { if (!App.TestMode) a.Disconnect(); ReloadPage("network"); })
                    : Action("Connect", async () =>
                    {
                        var kind = Windows.Devices.WiFi.WiFiReconnectionKind.Automatic;
                        Windows.Devices.WiFi.WiFiConnectionResult r;
                        if (App.TestMode) return;
                        r = await a.ConnectAsync(net, kind);
                        if (r.ConnectionStatus != Windows.Devices.WiFi.WiFiConnectionStatus.Success && !open)
                        {
                            string? pw = AskPassword(net.Ssid);
                            if (pw == null) return;
                            r = await a.ConnectAsync(net, kind, new Windows.Security.Credentials.PasswordCredential { Password = pw });
                        }
                        if (r.ConnectionStatus != Windows.Devices.WiFi.WiFiConnectionStatus.Success) MessageBox.Show(this, $"Couldn't connect to {net.Ssid} ({r.ConnectionStatus}).", "Wi-Fi");
                        ReloadPage("network");
                    })));
            }
        }
        catch (Exception ex) { host.Children.Add(Row("Wi-Fi networks", "Not available here (" + ex.Message + ").", null)); }
    }

    /// <summary>A small Norrsken prompt for a Wi-Fi password.</summary>
    string? AskPassword(string ssid)
    {
        var box = new PasswordBox { Width = 260, Padding = new Thickness(8, 6, 8, 6), Margin = new Thickness(0, 10, 0, 14) };
        var ok = new Button { Content = "Connect", Style = (Style)FindResource("PrimaryButton"), FontSize = 13, IsDefault = true };
        var stack = new StackPanel { Margin = new Thickness(22) };
        var t = new TextBlock { Text = $"Password for {ssid}", FontSize = 15, FontWeight = FontWeights.SemiBold };
        t.SetResourceReference(TextBlock.ForegroundProperty, "Text");
        stack.Children.Add(t); stack.Children.Add(box); stack.Children.Add(ok);
        var w = new Window { Owner = this, WindowStyle = WindowStyle.ToolWindow, SizeToContent = SizeToContent.WidthAndHeight, Content = stack, Title = "Wi-Fi", ResizeMode = ResizeMode.NoResize, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        w.SetResourceReference(BackgroundProperty, "SurfaceSolid");
        string? result = null;
        ok.Click += (_, _) => { result = box.Password; w.Close(); };
        w.Loaded += (_, _) => box.Focus();
        w.ShowDialog();
        return string.IsNullOrEmpty(result) ? null : result;
    }

    FrameworkElement BluetoothPage()
    {
        var s = Section("Bluetooth & devices");
        var radios = new StackPanel();
        s.Children.Add(new Border { Style = (Style)FindResource("Group"), Child = radios });
        _ = FillRadios(radios, RadioKind.Bluetooth, "Bluetooth");
        s.Children.Add(Heading("Your devices"));
        var paired = new StackPanel();
        s.Children.Add(new Border { Style = (Style)FindResource("Group"), Child = paired });
        _ = FillBluetooth(paired, pairedOnly: true);
        s.Children.Add(Heading("Add a device"));
        var nearby = new StackPanel();
        s.Children.Add(new Border { Style = (Style)FindResource("Group"), Child = nearby });
        nearby.Children.Add(Row("Look for devices nearby", "Put the device in pairing mode first.", Action("Search", () => { nearby.Children.Clear(); _ = FillBluetooth(nearby, pairedOnly: false); })));

        s.Children.Add(Heading("Mouse"));
        s.Children.Add(Group(
            Row("Pointer speed", null, new Slider { Minimum = 1, Maximum = 20, Value = DeviceControl.MouseSpeed, Width = 260, Style = (Style)FindResource("NSlider"), IsSnapToTickEnabled = true, TickFrequency = 1 }.Also(sl => sl.ValueChanged += (_, e) => DeviceControl.MouseSpeed = (int)e.NewValue)),
            Row("Lines per scroll", null, new Slider { Minimum = 1, Maximum = 15, Value = DeviceControl.ScrollLines, Width = 260, Style = (Style)FindResource("NSlider"), IsSnapToTickEnabled = true, TickFrequency = 1 }.Also(sl => sl.ValueChanged += (_, e) => DeviceControl.ScrollLines = (int)e.NewValue)),
            Toggle("Enhance pointer precision", DeviceControl.Acceleration, on => DeviceControl.Acceleration = on),
            Toggle("Swap left and right buttons", DeviceControl.ButtonsSwapped, on => DeviceControl.ButtonsSwapped = on)));

        s.Children.Add(Heading("Printers"));
        var printers = new List<UIElement>();
        try
        {
            using var q = new System.Management.ManagementObjectSearcher("SELECT Name, Default FROM Win32_Printer");
            foreach (System.Management.ManagementObject p in q.Get())
            {
                string name = p["Name"]?.ToString() ?? "";
                bool def = (bool)(p["Default"] ?? false);
                var obj = p;
                printers.Add(Row(name, def ? "Default printer" : null, def ? null : Action("Make default", () => { if (!App.TestMode) obj.InvokeMethod("SetDefaultPrinter", null); ReloadPage("bluetooth"); })));
            }
        }
        catch (Exception) { }
        if (printers.Count == 0) printers.Add(Row("No printers", null, null));
        s.Children.Add(Group(printers.ToArray()));
        return s;
    }

    async Task FillBluetooth(StackPanel host, bool pairedOnly)
    {
        try
        {
            string selector = Windows.Devices.Bluetooth.BluetoothDevice.GetDeviceSelectorFromPairingState(pairedOnly);
            var found = await Windows.Devices.Enumeration.DeviceInformation.FindAllAsync(selector, ["System.Devices.Aep.IsConnected"]);
            if (found.Count == 0) { host.Children.Add(Row(pairedOnly ? "No paired devices" : "Nothing found nearby", null, null)); return; }
            foreach (var d in found.Where(d => d.Name.Length > 0))
            {
                bool connected = d.Properties.TryGetValue("System.Devices.Aep.IsConnected", out var c) && c is true;
                if (host.Children.Count > 0) host.Children.Add(new Border { Style = (Style)FindResource("Hairline") });
                var dev = d;
                host.Children.Add(Row(d.Name, pairedOnly ? (connected ? "Connected" : "Paired") : "Ready to pair", pairedOnly
                    ? Action("Remove", async () => { if (!App.TestMode) await dev.Pairing.UnpairAsync(); ReloadPage("bluetooth"); })
                    : Action("Pair", async () => { if (App.TestMode) return; var r = await dev.Pairing.PairAsync(); MessageBox.Show(this, r.Status.ToString(), dev.Name); ReloadPage("bluetooth"); })));
            }
        }
        catch (Exception ex) { host.Children.Add(Row("Bluetooth", "Not available here (" + ex.Message + ").", null)); }
    }

    async Task FillRadios(StackPanel host, RadioKind kind, string label)
    {
        try
        {
            if (await Radio.RequestAccessAsync() != RadioAccessStatus.Allowed) throw new InvalidOperationException("access denied");
            var list = (await Radio.GetRadiosAsync()).Where(r => r.Kind == kind).ToList();
            if (list.Count == 0) { host.Children.Add(Row(label, $"No {label} adapter on this PC.", null)); return; }
            foreach (var r in list)
            {
                if (host.Children.Count > 0) host.Children.Add(new Border { Style = (Style)FindResource("Hairline") });
                host.Children.Add(Toggle(label, r.State == RadioState.On, async on =>
                {
                    if (App.TestMode) { App.TestLaunches.Add($"radio {label} {(on ? "on" : "off")}"); return; }
                    await r.SetStateAsync(on ? RadioState.On : RadioState.Off);
                }));
            }
        }
        catch (Exception ex) { host.Children.Add(Row(label, "Radio control isn't available here (" + ex.Message + ").", null)); }
    }

    FrameworkElement AppsPage()
    {
        var s = Section("Apps");
        s.Children.Add(Group(Row("Install apps", "Steam, Epic, Discord, Claude, browsers and more", Action("Install apps…", () => InstallApps_Click(this, new RoutedEventArgs()), primary: true))));
        s.Children.Add(Heading("Installed apps"));
        var search = new TextBox { Style = (Style)FindResource("PlainTextBox"), FontSize = 13 };
        var box = new Border { CornerRadius = Theme.R(10), BorderThickness = new Thickness(1), Height = 34, Padding = new Thickness(10, 0, 10, 0), Margin = new Thickness(0, 0, 0, 8), Child = search };
        box.SetResourceReference(Border.BackgroundProperty, "SurfaceAlt");
        box.SetResourceReference(Border.BorderBrushProperty, "Border");
        s.Children.Add(box);
        var list = new StackPanel();
        s.Children.Add(new Border { Style = (Style)FindResource("Group"), Child = list });
        var apps = InstalledApps.List();
        var store = new List<(string Name, string Publisher, string Family, string FullName)>();
        try
        {
            foreach (var p in new Windows.Management.Deployment.PackageManager().FindPackagesForUser("").Where(p => !p.IsFramework && !p.IsResourcePackage && p.SignatureKind == Windows.ApplicationModel.PackageSignatureKind.Store))
                store.Add((p.DisplayName, p.PublisherDisplayName, p.Id.FamilyName, p.Id.FullName));
        }
        catch (Exception) { }
        void Fill(string q)
        {
            list.Children.Clear();
            foreach (var a in apps.Where(a => q.Length == 0 || a.Name.Contains(q, StringComparison.CurrentCultureIgnoreCase) || a.Publisher.Contains(q, StringComparison.CurrentCultureIgnoreCase)).Take(300))
            {
                if (list.Children.Count > 0) list.Children.Add(new Border { Style = (Style)FindResource("Hairline") });
                string sub = string.Join("  ·  ", new[] { a.Publisher, a.Version, a.SizeKb > 0 ? Files.FormatSize(a.SizeKb * 1024) : "" }.Where(x => x.Length > 0));
                list.Children.Add(Row(a.Name, sub.Length > 0 ? sub : null, a.Uninstall.Length > 0 ? Action("Uninstall", () => InstalledApps.Uninstall(a)) : null));
            }
            foreach (var st in store.Where(x => x.Name.Length > 0 && !x.Name.StartsWith("ms-resource") && (q.Length == 0 || x.Name.Contains(q, StringComparison.CurrentCultureIgnoreCase))).OrderBy(x => x.Name).Take(200))
            {
                if (list.Children.Count > 0) list.Children.Add(new Border { Style = (Style)FindResource("Hairline") });
                var pkg = st;
                list.Children.Add(Row(st.Name, $"{st.Publisher}  ·  Microsoft Store", Action("Uninstall", async () =>
                {
                    if (App.TestMode) return;
                    if (MessageBox.Show(this, $"Uninstall {pkg.Name}?", "Apps", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
                    try { await new Windows.Management.Deployment.PackageManager().RemovePackageAsync(pkg.FullName); ReloadPage("apps"); }
                    catch (Exception ex) { MessageBox.Show(this, ex.Message, "Apps"); }
                })));
            }
            if (list.Children.Count == 0) list.Children.Add(Row("No apps match", null, null));
        }
        Fill("");
        search.TextChanged += (_, _) => Fill(search.Text.Trim());

        s.Children.Add(Heading("Start with Windows"));
        var startup = StartupApps.List();
        s.Children.Add(Group(startup.Count > 0
            ? startup.Select(e => (UIElement)Toggle($"{e.Name}", e.Enabled, on => { if (App.TestMode) return; try { StartupApps.Set(e, on); } catch (Exception ex) { MessageBox.Show(this, ex.Message, "Startup"); } })).ToArray()
            : [Row("Nothing starts with Windows.", null, null)]));

        s.Children.Add(Heading("Default apps"));
        bool flicker = File.Exists(MediaDefaults.Exe);
        s.Children.Add(Group(
            Row("Photos, video, music and GIFs", flicker ? "Flickerbox" : "Windows' own apps (Flickerbox isn't installed)", null),
            Row("Text files", "Notes", null),
            Row("Folders", "Files", null),
            Row("Screenshots", "Screenshot (PrtScn, Win+Shift+S)", null)));
        return s;
    }

    FrameworkElement PowerPage()
    {
        var s = Section("Power & battery");
        var bat = Power.Battery();
        if (bat is { } b) s.Children.Add(Group(Row("Battery", $"{b.Percent}%" + (b.Charging ? ", charging" : ""), null)));
        s.Children.Add(Heading("Power mode"));
        var modes = new StackPanel();
        string list = Run("powercfg.exe", "/list");
        foreach (var line in list.Split('\n'))
        {
            var m = System.Text.RegularExpressions.Regex.Match(line, @"([0-9a-fA-F-]{36})\s+\((.+?)\)(\s*\*)?");
            if (!m.Success) continue;
            string guid = m.Groups[1].Value, name = m.Groups[2].Value;
            var rb = new RadioButton { Content = name, GroupName = "power", IsChecked = m.Groups[3].Success, Margin = new Thickness(0, 9, 0, 9), FontSize = 13.5 };
            rb.Checked += (_, _) => { if (!App.TestMode && rb.IsLoaded) Run("powercfg.exe", "/setactive " + guid); };
            if (modes.Children.Count > 0) modes.Children.Add(new Border { Style = (Style)FindResource("Hairline") });
            modes.Children.Add(rb);
        }
        if (modes.Children.Count == 0) modes.Children.Add(Row("Power plans aren't available", null, null));
        s.Children.Add(new Border { Style = (Style)FindResource("Group"), Child = modes });

        s.Children.Add(Heading("Screen and sleep"));
        string[] labels = ["1 minute", "2 minutes", "5 minutes", "10 minutes", "15 minutes", "30 minutes", "1 hour", "2 hours", "Never"];
        int[] mins = [1, 2, 5, 10, 15, 30, 60, 120, 0];
        string Label(int m) => labels[Math.Max(0, Array.IndexOf(mins, m) is var i && i >= 0 ? i : Array.FindIndex(mins, x => x >= m && x != 0) is var j && j >= 0 ? j : mins.Length - 1)];
        var rows = new List<UIElement>();
        foreach (var (what, title) in new[] { ("monitor", "Turn off the screen after"), ("standby", "Sleep after") })
        {
            bool hasBattery = bat != null;
            rows.Add(Row(title + (hasBattery ? " (plugged in)" : ""), null, Combo(labels, Label(DeviceControl.GetTimeout(what, false)), i => DeviceControl.SetTimeout(what, false, mins[i]), 160)));
            if (hasBattery) rows.Add(Row(title + " (on battery)", null, Combo(labels, Label(DeviceControl.GetTimeout(what, true)), i => DeviceControl.SetTimeout(what, true, mins[i]), 160)));
        }
        s.Children.Add(Group(rows.ToArray()));

        s.Children.Add(Heading("Session"));
        var buttons = new WrapPanel();
        foreach (var (label, act) in new (string, Action)[] { ("Lock", Power.Lock), ("Sleep", Power.Sleep), ("Sign out", Power.SignOut), ("Restart", Power.Restart), ("Shut down", Power.ShutDown) })
        {
            var btn = new Button { Style = (Style)FindResource("LinkButton"), Content = new TextBlock { Text = label, FontSize = 13 }, Margin = new Thickness(0, 6, 18, 6) };
            btn.Click += (_, _) => { if (App.TestMode) App.TestLaunches.Add("power:" + label); else act(); };
            buttons.Children.Add(btn);
        }
        s.Children.Add(new Border { Style = (Style)FindResource("Group"), Child = buttons, Padding = new Thickness(18, 6, 18, 6) });
        return s;
    }

    FrameworkElement StoragePage()
    {
        var s = Section("Storage");
        var rows = new List<UIElement>();
        foreach (var d in DriveInfo.GetDrives())
        {
            try
            {
                if (!d.IsReady || d.TotalSize == 0) continue;
                double used = 1 - (double)d.AvailableFreeSpace / d.TotalSize;
                var bar = new Grid { Width = 220, Height = 6, VerticalAlignment = VerticalAlignment.Center };
                var track = new Border { CornerRadius = Theme.R(3) };
                track.SetResourceReference(Border.BackgroundProperty, "Border");
                var fill = new Border { CornerRadius = Theme.R(3), HorizontalAlignment = HorizontalAlignment.Left, Width = 220 * used };
                fill.SetResourceReference(Border.BackgroundProperty, used > 0.9 ? "Text" : "TextDim");
                bar.Children.Add(track); bar.Children.Add(fill);
                string name = (d.VolumeLabel.Length > 0 ? d.VolumeLabel : "Local disk") + $" ({d.Name.TrimEnd('\\')})";
                rows.Add(Row(name, $"{Files.FormatSize(d.TotalSize - d.AvailableFreeSpace)} used of {Files.FormatSize(d.TotalSize)}  ·  {Files.FormatSize(d.AvailableFreeSpace)} free", bar));
            }
            catch (IOException) { }
        }
        s.Children.Add(Group(rows.ToArray()));
        s.Children.Add(Heading("Clean up"));
        var tempRow = Row("Temporary files", "Measuring…", null);
        var cleanBtn = Action("Clean up", () => { }, primary: true);
        tempRow.Children.Insert(0, cleanBtn);
        DockPanel.SetDock(cleanBtn, Dock.Right);
        cleanBtn.IsEnabled = false;
        _ = Task.Run(Maintenance.TempSize).ContinueWith(t =>
        {
            if (t.Status != TaskStatus.RanToCompletion) { ((TextBlock)((StackPanel)tempRow.Children[^1]).Children[1]).Text = "Couldn't measure the temporary files"; return; }
            var (bytes, files) = t.Result;
            ((TextBlock)((StackPanel)tempRow.Children[^1]).Children[1]).Text = $"{Files.FormatSize(bytes)} in {files:N0} files that apps and Windows left behind";
            cleanBtn.IsEnabled = bytes > 0;
        }, TaskScheduler.FromCurrentSynchronizationContext());
        cleanBtn.Click += async (_, _) =>
        {
            cleanBtn.IsEnabled = false;
            long freed = await Task.Run(Maintenance.CleanTemp);
            ((TextBlock)((StackPanel)tempRow.Children[^1]).Children[1]).Text = $"Freed {Files.FormatSize(freed)} (files in use were left alone)";
        };
        s.Children.Add(Group(
            tempRow,
            Row("Recycle Bin", "Permanently remove everything in the Recycle Bin.", Action("Empty", () => { if (!App.TestMode) Files.EmptyRecycleBin(new System.Windows.Interop.WindowInteropHelper(this).Handle); })),
            Row("Downloads", Files.Downloads, Action("Open in Files", () => NorrskenApps.Launch("files", Files.Downloads)))));
        return s;
    }

    FrameworkElement GamingPage()
    {
        var s = Section("Gaming");
        const string bar = @"Software\Microsoft\GameBar";
        bool gameMode = (Registry.CurrentUser.OpenSubKey(bar)?.GetValue("AutoGameModeEnabled") as int? ?? 1) != 0;
        bool gameBar = (Registry.CurrentUser.OpenSubKey(bar)?.GetValue("UseNexusForGameBarEnabled") as int? ?? 1) != 0;
        bool captures = (Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\GameDVR")?.GetValue("AppCaptureEnabled") as int? ?? 1) != 0;
        void Set(string key, string name, bool on) { if (App.TestMode) { App.TestLaunches.Add($"{name}={on}"); return; } using var k = Registry.CurrentUser.CreateSubKey(key); k.SetValue(name, on ? 1 : 0, RegistryValueKind.DWord); }
        s.Children.Add(Group(
            Toggle("Game Mode (prioritise the game you're playing)", gameMode, on => Set(bar, "AutoGameModeEnabled", on)),
            Toggle("Open Game Bar with the controller's Xbox button", gameBar, on => Set(bar, "UseNexusForGameBarEnabled", on)),
            Toggle("Background recording and captures", captures, on => Set(@"Software\Microsoft\Windows\CurrentVersion\GameDVR", "AppCaptureEnabled", on))));
        s.Children.Add(Note("Norrsken's dock steps aside in every full-screen game and video automatically. Per-game graphics card choice is under Display."));
        return s;
    }

    FrameworkElement TimePage()
    {
        var s = Section("Time & language");
        var zones = new ComboBox { Width = 300, VerticalAlignment = VerticalAlignment.Center };
        foreach (var z in TimeZoneInfo.GetSystemTimeZones()) zones.Items.Add(new ComboBoxItem { Content = z.DisplayName, Tag = z.Id });
        zones.SelectedItem = zones.Items.Cast<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == TimeZoneInfo.Local.Id);
        zones.SelectionChanged += (_, _) =>
        {
            if (zones.SelectedItem is not ComboBoxItem { Tag: string id } || id == TimeZoneInfo.Local.Id) return;
            if (App.TestMode) { App.TestLaunches.Add("tz " + id); return; }
            Run("tzutil.exe", $"/s \"{id}\"");
            TimeZoneInfo.ClearCachedData();
        };
        s.Children.Add(Group(
            Row("Now", DateTime.Now.ToString("dddd d MMMM yyyy, HH:mm"), null),
            Toggle("Set the time automatically", DeviceControl.AutoTime, on => DeviceControl.AutoTime = on),
            Row("Sync with the time server", null, Action("Sync now", DeviceControl.SyncTime)),
            Row("Time zone", null, zones)));
        s.Children.Add(Heading("Keyboard layouts"));
        var layouts = System.Windows.Input.InputLanguageManager.Current.AvailableInputLanguages?.Cast<System.Globalization.CultureInfo>().ToList() ?? [];
        s.Children.Add(Group(layouts.Count > 0
            ? layouts.Select(c => (UIElement)Row(c.DisplayName, c.Name, null)).ToArray()
            : [Row("Keyboard", System.Globalization.CultureInfo.CurrentCulture.DisplayName, null)]));
        s.Children.Add(Note("Switch layouts with Win+Space. Region and number formats follow your Windows language."));
        s.Children.Add(Heading("Format"));
        var c = System.Globalization.CultureInfo.CurrentCulture;
        s.Children.Add(Group(Row("Region", c.DisplayName, null), Row("Date", DateTime.Now.ToString("d", c), null), Row("Time", DateTime.Now.ToString("t", c), null), Row("Numbers", 1234567.89.ToString("N2", c), null)));
        return s;
    }

    FrameworkElement AccountsPage()
    {
        var s = Section("Accounts");
        s.Children.Add(Group(Row(Environment.UserName, Environment.UserDomainName == Environment.MachineName ? "Local account on this PC" : Environment.UserDomainName, null)));
        s.Children.Add(Heading("Password"));
        var oldPw = new PasswordBox { Width = 220, Padding = new Thickness(8, 5, 8, 5) };
        var newPw = new PasswordBox { Width = 220, Padding = new Thickness(8, 5, 8, 5) };
        var result = Status("");
        s.Children.Add(Group(
            Row("Current password", null, oldPw),
            Row("New password", null, newPw),
            Row("", null, Action("Change password", () =>
            {
                if (App.TestMode) return;
                int r = NetUserChangePassword(null, Environment.UserName, oldPw.Password, newPw.Password);
                result.Text = r == 0 ? "Password changed." : r == 86 ? "The current password isn't right." : $"Windows didn't accept it (code {r}). Microsoft-account passwords change online.";
            }))));
        s.Children.Add(result);
        s.Children.Add(Heading("Sign-in"));
        s.Children.Add(Group(
            Row("Lock", "Win+L, or the lock in the launcher.", Action("Lock now", () => { if (!App.TestMode) Power.Lock(); })),
            Row("Sign out", null, Action("Sign out", () => PowerDialog.Open()))));
        return s;
    }

    [System.Runtime.InteropServices.DllImport("netapi32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    static extern int NetUserChangePassword(string? domain, string user, string oldPassword, string newPassword);

    FrameworkElement PrivacyPage()
    {
        var s = Section("Privacy & security");
        s.Children.Add(Heading("Virus protection"));
        var sec = Maintenance.Defender();
        var scanStatus = Status("");
        s.Children.Add(Group(sec != null
            ? new UIElement[]
            {
                Row(sec.Antivirus && sec.RealTime ? "Protected" : "Check your protection", $"Microsoft Defender  ·  real-time protection {(sec.RealTime ? "on" : "off")}", null),
                Row("Virus definitions", sec.Signatures?.ToString("g") ?? "Unknown", null),
                Row("Quick scan", sec.LastScan is { } ls ? "Last: " + ls.ToString("g") : "Not run yet", Action("Scan now", async () =>
                {
                    scanStatus.Text = "Scanning… (you can keep working)";
                    var p = Maintenance.QuickScan();
                    if (p != null) await p.WaitForExitAsync();
                    scanStatus.Text = p == null ? "Defender isn't available." : p.ExitCode == 0 ? "No threats found." : "Defender found something; it's handled automatically.";
                })),
            }
            : [Row("Virus protection", "Status isn't available (another antivirus may be in charge).", null)]));
        s.Children.Add(scanStatus);
        s.Children.Add(Heading("What apps may use"));
        s.Children.Add(Group(DeviceControl.Permissions.Select(p => (UIElement)Toggle(p.Name, DeviceControl.Allowed(p.Id), on => DeviceControl.Allow(p.Id, on))).ToArray()));
        s.Children.Add(Heading("Windows"));
        bool ads = (Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\AdvertisingInfo")?.GetValue("Enabled") as int? ?? 1) != 0;
        bool tips = (Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager")?.GetValue("SubscribedContent-338389Enabled") as int? ?? 1) != 0;
        void SetDword(string key, string name, int v) { if (App.TestMode) return; using var k = Registry.CurrentUser.CreateSubKey(key); k.SetValue(name, v, RegistryValueKind.DWord); }
        s.Children.Add(Group(
            Toggle("Personalised ads in apps", ads, on => SetDword(@"Software\Microsoft\Windows\CurrentVersion\AdvertisingInfo", "Enabled", on ? 1 : 0)),
            Toggle("Tips and suggestions from Windows", tips, on => { foreach (var n in new[] { "SubscribedContent-338389Enabled", "SubscribedContent-338388Enabled", "SubscribedContent-310093Enabled", "SoftLandingEnabled", "RotatingLockScreenOverlayEnabled" }) SetDword(@"Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager", n, on ? 1 : 0); })));
        return s;
    }

    FrameworkElement UpdatePage()
    {
        var s = Section("Updates");
        // Norrsken and Flickerbox, from GitHub releases: the same states the update card shows.
        s.Children.Add(Heading("Norrsken"));
        var nStatus = Status("");
        var nBar = new Border { Height = 4, CornerRadius = new CornerRadius(2), HorizontalAlignment = HorizontalAlignment.Left, Width = 0, Margin = new Thickness(4, 8, 0, 0) };
        nBar.SetResourceReference(Border.BackgroundProperty, "Accent");
        var nCheck = Action("Check now", () => { });
        var nGo = Action("", () => { }, primary: true);
        void Show()
        {
            var rel = Updater.Staged ?? Updater.Latest;
            (nStatus.Text, string go) = Updater.State switch
            {
                Updater.Stage.Checking => ("Checking GitHub…", ""),
                Updater.Stage.UpToDate => ($"Norrsken {App.Version} is the newest version.", ""),
                Updater.Stage.Available => ($"Norrsken {rel?.Version} is out ({(rel?.Size ?? 0) / 1048576.0:0} MB).", Updater.CanUpdateHere ? "Download" : ""),
                Updater.Stage.Downloading => ($"Downloading Norrsken {rel?.Version}… {Updater.Progress:P0}", ""),
                Updater.Stage.Ready => ($"Norrsken {Updater.Staged?.Version} is downloaded and ready. Restart to install it, or it installs the next time you sign in.", "Restart now"),
                Updater.Stage.Failed => (Updater.Error, ""),
                _ => (Updater.CanUpdateHere ? $"Norrsken {App.Version}." : $"Norrsken {App.Version} (running from a build folder, so it doesn't update itself).", ""),
            };
            nGo.Content = go;
            nGo.Visibility = go.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
            nCheck.IsEnabled = Updater.State is not (Updater.Stage.Checking or Updater.Stage.Downloading);
            bool busy = Updater.State == Updater.Stage.Downloading;
            nBar.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
            if (busy && nBar.Parent is FrameworkElement host) Motion.To(nBar, WidthProperty, Math.Max(0, (host.ActualWidth - 8) * Updater.Progress), 200);
        }
        Updater.Changed += Show;
        Unloaded += (_, _) => Updater.Changed -= Show;
        nCheck.Click += async (_, _) => { if (!App.TestMode) try { await Updater.CheckAsync(); } catch (Exception) { } Show(); };
        nGo.Click += async (_, _) =>
        {
            if (Updater.State == Updater.Stage.Ready) { Updater.InstallNow(); return; }
            if (Updater.Latest is { } rel) try { await Updater.DownloadAsync(rel); } catch (Exception) { }
        };
        s.Children.Add(Group(
            Row("Norrsken and Flickerbox", "New versions come from github.com/" + Updater.Repo + ".", nCheck),
            Toggle("Download updates automatically", App.Settings.AutoUpdate, on => { App.Settings.AutoUpdate = on; App.Settings.Save(); }),
            Row("", null, nGo)));
        s.Children.Add(nStatus);
        s.Children.Add(nBar);
        Show();
        s.Children.Add(Heading("Windows"));
        var list = new StackPanel();
        var status = Status("");
        var check = Action("Check for updates", () => { }, primary: true);
        List<Maintenance.Update> found = new();
        var install = Action("Install all", () => { }, primary: true);
        install.Visibility = Visibility.Collapsed;
        check.Click += async (_, _) =>
        {
            check.IsEnabled = false;
            status.Text = "Checking with Microsoft…";
            list.Children.Clear();
            try
            {
                found = App.TestMode ? new() : await Files.RunSta(Maintenance.FindUpdates);
                status.Text = found.Count == 0 ? "You're up to date." : $"{found.Count} update{(found.Count == 1 ? "" : "s")} available.";
                foreach (var u in found)
                {
                    if (list.Children.Count > 0) list.Children.Add(new Border { Style = (Style)FindResource("Hairline") });
                    list.Children.Add(Row(u.Title, string.Join("  ·  ", new[] { u.Kb, u.SizeMb > 0 ? $"{u.SizeMb:0} MB" : "" }.Where(x => x.Length > 0)), null));
                }
                install.Visibility = found.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            }
            catch (Exception ex) { status.Text = "Windows Update didn't answer: " + ex.Message; }
            check.IsEnabled = true;
        };
        install.Click += async (_, _) =>
        {
            install.IsEnabled = false;
            status.Text = "Downloading and installing… this can take a while.";
            try
            {
                bool restart = await Files.RunSta(() => Maintenance.Install(found));
                status.Text = restart ? "Installed. Restart to finish." : "Installed.";
            }
            catch (Exception ex) { status.Text = "Install failed (Norrsken needs administrator rights for this): " + ex.Message; }
            install.IsEnabled = true;
        };
        s.Children.Add(Group(Row("Updates", "Security and driver updates from Microsoft.", check), Row("", null, install)));
        s.Children.Add(status);
        s.Children.Add(new Border { Style = (Style)FindResource("Group"), Child = list, Margin = new Thickness(0, 10, 0, 0) });
        return s;
    }

    FrameworkElement AboutPage()
    {
        var s = Section("About");
        var info = DeviceInfo.Read();
        var copy = Action("Copy", () => { if (!App.TestMode) Clipboard.SetText(info.Text); });
        var nameBox = new TextBox { Text = info.Name, Width = 200, Padding = new Thickness(8, 5, 8, 5), VerticalAlignment = VerticalAlignment.Center };
        var renameStatus = Status("");
        var rename = new StackPanel { Orientation = Orientation.Horizontal };
        rename.Children.Add(nameBox);
        rename.Children.Add(Action("Rename", () =>
        {
            string n = nameBox.Text.Trim();
            if (n.Length == 0 || n == info.Name) return;
            renameStatus.Text = Maintenance.RenamePc(n) is { } err ? "Couldn't rename: " + err : "Renamed. It takes effect after a restart.";
        }));
        s.Children.Add(Group(
            Row("Device name", null, rename),
            Row("Processor", info.Cpu, null),
            Row("Memory", info.Ram, null),
            Row("Graphics", info.Gpu, null),
            Row("System", info.Os, copy),
            Row("Activation", Maintenance.Activation(), null),
            Row("Norrsken", $"Norrsken OS {App.Version}", null)));
        s.Children.Add(renameStatus);
        s.Children.Add(Heading("Recovery"));
        s.Children.Add(Group(Row("Advanced startup", "Restart into Windows' recovery options (repair, reset, safe mode).", Action("Restart now", () =>
        {
            if (MessageBox.Show(this, "Restart into recovery now? Save your work first.", "Recovery", MessageBoxButton.OKCancel) == MessageBoxResult.OK) Maintenance.RestartToRecovery();
        }))));
        return s;
    }
}

static class FluentExtensions
{
    /// <summary>Configure an object inline and keep using it.</summary>
    public static T Also<T>(this T o, Action<T> a) { a(o); return o; }
}

/// <summary>Desktop programs from the uninstall registry (what "Installed apps" lists for non-Store apps).</summary>
public static class InstalledApps
{
    public sealed record Entry(string Name, string Publisher, string Version, long SizeKb, string Uninstall, bool Quiet);

    public static List<Entry> List()
    {
        var found = new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
        foreach (var (hive, path) in new[]
                 {
                     (Registry.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall"),
                     (Registry.LocalMachine, @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall"),
                     (Registry.CurrentUser, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall"),
                 })
        {
            using var root = hive.OpenSubKey(path);
            if (root == null) continue;
            foreach (var sub in root.GetSubKeyNames())
            {
                using var k = root.OpenSubKey(sub);
                if (k?.GetValue("DisplayName") is not string name || name.Length == 0) continue;
                if ((k.GetValue("SystemComponent") as int?) == 1 || k.GetValue("ParentKeyName") != null) continue;
                string un = k.GetValue("QuietUninstallString") as string ?? k.GetValue("UninstallString") as string ?? "";
                var e = new Entry(name, k.GetValue("Publisher") as string ?? "", k.GetValue("DisplayVersion") as string ?? "",
                    Convert.ToInt64(k.GetValue("EstimatedSize") ?? 0), un, k.GetValue("QuietUninstallString") != null);
                found.TryAdd(name, e);
            }
        }
        return found.Values.OrderBy(e => e.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    /// <summary>Starts the app's own uninstaller (it asks for permission and confirmation itself).</summary>
    public static void Uninstall(Entry e)
    {
        if (App.TestMode) { App.TestLaunches.Add("uninstall " + e.Name); return; }
        try
        {
            string cmd = e.Uninstall.Trim();
            string exe, args;
            if (cmd.StartsWith('"')) { int end = cmd.IndexOf('"', 1); exe = cmd[1..end]; args = cmd[(end + 1)..].Trim(); }
            else if (cmd.StartsWith("MsiExec", StringComparison.OrdinalIgnoreCase)) { exe = "msiexec.exe"; args = cmd[cmd.IndexOf(' ')..].Trim().Replace("/I{", "/X{", StringComparison.OrdinalIgnoreCase); }
            else { int sp = cmd.IndexOf(".exe", StringComparison.OrdinalIgnoreCase); exe = sp > 0 ? cmd[..(sp + 4)] : cmd; args = sp > 0 ? cmd[(sp + 4)..].Trim() : ""; }
            Interop.Native.AllowAnyForeground();
            WindowTracker.ExpectNewWindow(); // uninstallers often need admin, so this one stays elevated, but comes to the front
            Process.Start(new ProcessStartInfo(exe, args) { UseShellExecute = true });
        }
        catch (Exception ex) { Log.Write($"Uninstall {e.Name} failed: {ex.Message}"); MessageBox.Show($"{e.Name} couldn't start its uninstaller.\n\n{ex.Message}", "Settings"); }
    }
}

/// <summary>The facts on the About page.</summary>
public sealed record DeviceInfo(string Name, string Cpu, string Ram, string Gpu, string Os)
{
    [StructLayout(LayoutKind.Sequential)]
    struct MEMORYSTATUSEX { public uint dwLength, dwMemoryLoad; public ulong ullTotalPhys, ullAvailPhys, ullTotalPageFile, ullAvailPageFile, ullTotalVirtual, ullAvailVirtual, ullAvailExtendedVirtual; }
    [DllImport("kernel32.dll")] static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX m);

    public string Text => $"Device name: {Name}\nProcessor: {Cpu}\nMemory: {Ram}\nGraphics: {Gpu}\nSystem: {Os}\nNorrsken OS {App.Version}";

    public static DeviceInfo Read()
    {
        string cpu = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0")?.GetValue("ProcessorNameString") as string ?? "Unknown";
        var mem = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
        string ram = GlobalMemoryStatusEx(ref mem) ? $"{Math.Round(mem.ullTotalPhys / 1073741824.0)} GB" : "Unknown";
        string gpu = "Unknown";
        try
        {
            using var q = new ManagementObjectSearcher("SELECT Name FROM Win32_VideoController");
            gpu = string.Join(", ", q.Get().Cast<ManagementObject>().Select(o => o["Name"]?.ToString()).Where(n => !string.IsNullOrEmpty(n)));
        }
        catch (Exception) { }
        using var nt = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
        int build = int.TryParse(nt?.GetValue("CurrentBuildNumber") as string, out int b) ? b : Environment.OSVersion.Version.Build;
        string product = (nt?.GetValue("ProductName") as string ?? "Windows").Replace("Windows 10", build >= 22000 ? "Windows 11" : "Windows 10");
        string os = $"{product} {nt?.GetValue("DisplayVersion")}, build {build}.{nt?.GetValue("UBR")}";
        return new(Environment.MachineName, cpu.Trim(), ram, gpu.Length > 0 ? gpu : "Unknown", os);
    }
}
