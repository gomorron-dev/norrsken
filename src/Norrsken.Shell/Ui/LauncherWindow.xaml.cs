using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Norrsken.Shell.Core;

namespace Norrsken.Shell.Ui;

/// <summary>
/// Norrsken's start: Pinned apps and All apps (as folders by kind, or A–Z) when idle; type to search
/// apps, files and settings with filter chips, grouped results and a preview of the selection.
/// Arrows move, Enter opens, Tab changes filter, Esc goes back or closes.
/// </summary>
public partial class LauncherWindow : Window
{
    public DateTime LastHidden { get; private set; }
    bool _refilterQueued, _hiding;
    string _filter = "all";

    public sealed class Result
    {
        public required string Title { get; init; }
        public string Subtitle { get; init; } = "";
        public string Kind { get; init; } = "";
        public string Group { get; init; } = "";
        public ImageSource? Icon { get; init; }
        public AppEntry? Entry { get; init; }
        public string? Path { get; init; }
        public required Action Open { get; init; }
    }

    readonly DispatcherTimer _searchDelay = new() { Interval = TimeSpan.FromMilliseconds(140) };
    CancellationTokenSource? _searchCts;
    List<Result> _appResults = new(), _otherResults = new(), _shown = new();
    readonly List<Border> _rows = new();
    int _selected;
    internal IReadOnlyList<Result> Results => _appResults.Concat(_otherResults).ToList();
    internal string? SelectedTitle => _shown.ElementAtOrDefault(_selected)?.Title;
    internal int SelectedIndex => _selected;

    public LauncherWindow()
    {
        InitializeComponent();
        App.Apps.CollectionChanged += (_, _) => { _homeDirty = true; QueueRefilter(); };
        App.Pinned.CollectionChanged += (_, _) => _homeDirty = true;
        string user = App.DemoName ?? AccountName();
        UserName.Text = user;
        Initial.Text = user.Length > 0 ? user[..1].ToUpperInvariant() : "?";
        LoadAvatar();
        _searchDelay.Tick += (_, _) => SearchOthers();
        Refilter();
    }

    void LoadAvatar()
    {
        try
        {
            string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), @"Microsoft\Windows\AccountPictures");
            string? pic = Directory.Exists(dir) ? Directory.EnumerateFiles(dir, "*.accountpicture-ms").FirstOrDefault() : null;
            string fallback = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), @"Microsoft\User Account Pictures\user.png");
            string? png = pic == null && File.Exists(fallback) ? fallback : null;
            if (png != null) Avatar.Fill = new ImageBrush(new BitmapImage(new Uri(png))) { Stretch = Stretch.UniformToFill };
        }
        catch (Exception) { }
    }

    void QueueRefilter()
    {
        if (_refilterQueued) return;
        _refilterQueued = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => { _refilterQueued = false; Refilter(); }));
    }

    const double FullHeight = 680, ShadowMargin = 30;

    /// <summary>The top of the dock on screen (DIPs); the launcher always ends above it.</summary>
    /// <summary>The account's display name ("Hannes Sorhall") when Windows has one, else the sign-in name.</summary>
    static string AccountName()
    {
        try
        {
            var sb = new System.Text.StringBuilder(256);
            uint n = (uint)sb.Capacity;
            if (GetUserNameExW(3 /* NameDisplay */, sb, ref n) && sb.Length > 0) return sb.ToString();
        }
        catch (Exception) { }
        return Environment.UserName;
    }

    [System.Runtime.InteropServices.DllImport("secur32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    static extern bool GetUserNameExW(int format, System.Text.StringBuilder name, ref uint size);

    static double DockTop()
    {
        var screen = SystemParameters.PrimaryScreenHeight;
        if (App.MainDock is { } d && d.IsLoaded && d.DockHostTop() is double top) return top;
        return screen - 80;
    }

    public void ShowCentered()
    {
        var area = SystemParameters.WorkArea;
        double limit = Math.Min(area.Bottom, DockTop() - 10);
        Height = Math.Clamp(limit - area.Top + ShadowMargin - 8, 420, FullHeight);
        Left = area.Left + (area.Width - Width) / 2;
        double cardHeight = Height - ShadowMargin * 2;
        double ideal = area.Top + Math.Max(0, (limit - area.Top - cardHeight) * 0.4) - ShadowMargin;
        Top = Math.Min(ideal, limit - cardHeight - ShadowMargin);
        SearchBox.Text = "";
        CloseFolder();
        Refilter();
        _hiding = false;
        Show();
        UpdateLayout();
        HomeView.ScrollToTop();
        StaggerIn();
        Activate();
        SearchBox.Focus();
        Keyboard.Focus(SearchBox);
        // Animate a cached bitmap of the card (shadow included), so the big soft shadow isn't
        // re-rendered on every frame; drop the cache once it has settled so text stays crisp.
        Card.CacheMode = new BitmapCache { RenderAtScale = 1, SnapsToDevicePixels = true };
        Motion.From(Card, OpacityProperty, 0, 1, 170);
        Motion.From(CardScale, ScaleTransform.ScaleXProperty, 0.965, 1, 300, Motion.SoftSpring);
        Motion.From(CardScale, ScaleTransform.ScaleYProperty, 0.965, 1, 300, Motion.SoftSpring);
        Motion.From(CardShift, TranslateTransform.YProperty, 16, 0, 300, Motion.Out, () => Card.CacheMode = null);
    }

    /// <summary>Tiles cascade in, 12 ms apart, as the card settles.</summary>
    void StaggerIn()
    {
        var items = Tiles().Take(30).ToList();
        for (int i = 0; i < items.Count; i++)
        {
            var el = items[i];
            if (Motion.Reduced) { el.BeginAnimation(OpacityProperty, null); continue; }
            var shift = new TranslateTransform();
            el.RenderTransform = shift;
            var delay = TimeSpan.FromMilliseconds(30 + i * 12);
            el.BeginAnimation(OpacityProperty, Cascade(0, 1, delay, 220));
            shift.BeginAnimation(TranslateTransform.YProperty, Cascade(10, 0, delay, 320));
        }
    }

    internal IEnumerable<FrameworkElement> Tiles()
    {
        for (int i = 0; i < PinnedGrid.Items.Count; i++)
            if (PinnedGrid.ItemContainerGenerator.ContainerFromIndex(i) is FrameworkElement f) yield return f;
        foreach (FrameworkElement f in FolderGrid.Children) yield return f;
    }

    static DoubleAnimationUsingKeyFrames Cascade(double from, double to, TimeSpan delay, int ms)
    {
        var a = new DoubleAnimationUsingKeyFrames();
        a.KeyFrames.Add(new DiscreteDoubleKeyFrame(from, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        a.KeyFrames.Add(new DiscreteDoubleKeyFrame(from, KeyTime.FromTimeSpan(delay)));
        a.KeyFrames.Add(new EasingDoubleKeyFrame(to, KeyTime.FromTimeSpan(delay + TimeSpan.FromMilliseconds(ms)), Motion.Out));
        return a;
    }

    /// <summary>Raised as the launcher starts to close (the dock lets go of a summon then).</summary>
    public event Action? Hiding;

    public void HideLauncher()
    {
        if (!IsVisible || _hiding) return;
        _hiding = true;
        LastHidden = DateTime.UtcNow;
        Hiding?.Invoke();
        Motion.To(CardScale, ScaleTransform.ScaleXProperty, 0.98, 120);
        Motion.To(CardScale, ScaleTransform.ScaleYProperty, 0.98, 120);
        Motion.To(Card, OpacityProperty, 0, 120, done: () =>
        {
            if (!_hiding) return;
            Hide();
            _hiding = false;
        });
    }

    protected override void OnDeactivated(EventArgs e)
    {
        base.OnDeactivated(e);
        if (!PowerMenu.IsOpen && !App.TestMode) HideLauncher();
    }

    // ───────────────────────── home: pinned and all apps ─────────────────────────

    static readonly string[] DefaultStartPins =
    [
        "Norrsken.Files", "Norrsken.Notes", "Norrsken.Settings", "Norrsken.Capture", "Microsoft.WindowsTerminal_8wekyb3d8bbwe!App",
        "Claude", "Chrome", "Steam", "Discord", "Spotify", "Visual Studio Code", "OBS Studio",
    ];

    List<AppEntry> PinnedApps()
    {
        var keys = App.Settings.StartPins ?? DefaultStartPins.ToList();
        var list = new List<AppEntry>();
        foreach (var k in keys)
        {
            var a = App.Apps.FirstOrDefault(x => x.ParsingName.Equals(k, StringComparison.OrdinalIgnoreCase))
                    ?? (App.Settings.StartPins == null ? App.Apps.FirstOrDefault(x => x.Name.Equals(k, StringComparison.OrdinalIgnoreCase)) : null);
            if (a != null && !list.Contains(a)) list.Add(a);
        }
        if (App.Settings.StartPins == null)
            foreach (var f in AppIndex.Frequent(App.Apps, App.Settings.Launches, 12)) if (list.Count < 18 && !list.Contains(f)) list.Add(f);
        return list;
    }

    void TogglePinStart_Click(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is not AppEntry app) return;
        var pins = App.Settings.StartPins ?? PinnedApps().Select(a => a.ParsingName).ToList();
        if (!pins.Remove(app.ParsingName)) pins.Add(app.ParsingName);
        App.Settings.StartPins = pins;
        App.Settings.Save();
        _homeDirty = true;
        Refilter();
    }

    static readonly (string Name, string[] Words)[] Categories =
    [
        ("Games", ["steam", "epic games", "battle.net", "ubisoft", "rockstar", "roblox", "minecraft", "lunar", "modrinth", "curseforge", "xbox", "gog", "ea app", "riot", "valorant", "league", "medal", "geometry dash", "five nights", "among us", "genshin", "hoyoplay", "counter-strike", "forza", "phasmophobia", "fishstrap", "bloxstrap", "prism", "heroic", "game", "clash", "incredibox", "cookie clicker"]),
        ("AI", ["claude", "chatgpt", "gemini", "copilot", "ollama", "perplexity", "devin"]),
        ("Development", ["visual studio", "code", "cursor", "git", "python", "idle", "node", "cmake", "msys", "java", "android studio", "unity", "terminal", "powershell", "command prompt", "kommandotolken", "developer", "antigravity", "wsl", "docker", "postman", "jetbrains", "pydoc", "sdk", "debug", "lua", "cheat engine", "autohotkey", "ahk"]),
        ("Creative", ["blender", "obs", "capcut", "ableton", "paint", "flickerbox", "photoshop", "gimp", "audacity", "davinci", "premiere", "krita", "figma", "focusrite", "fl studio", "openscad", "clipchamp"]),
        ("Internet & chat", ["chrome", "firefox", "opera", "edge", "brave", "discord", "telegram", "teams", "zoom", "outlook", "mail", "e-post", "whatsapp", "signal", "browser"]),
        ("Media", ["spotify", "vlc", "media", "music", "film", "foton", "photos", "netflix", "youtube", "player"]),
        ("System", ["norrsken", "files", "notes", "screenshot", "settings", "inställningar", "control panel", "kontrollpanelen", "task manager", "aktivitetshanteraren", "7-zip", "winrar", "powertoys", "crystal", "malwarebytes", "nvidia", "icue", "logitech", "warp", "cloudflare", "virtualbox", "everything", "registry", "event viewer", "loggboken", "disk", "uninstall", "avinstallera", "defrag", "odbc", "computer management", "datorhantering", "magnifier", "förstoringsglaset", "on-screen", "kör", "run"]),
    ];

    static string CategoryOf(AppEntry a)
    {
        string n = (a.Name + " " + (a.TargetPath ?? "")).ToLowerInvariant();
        if (n.Contains(@"\steamapps\") || n.Contains(@"\epic games\")) return "Games";
        foreach (var (name, words) in Categories)
            if (words.Any(w => n.Contains(w))) return name;
        return "Other";
    }

    bool _homeDirty = true;

    void BuildHome()
    {
        if (!_homeDirty) return; // built once; rebuilt only when apps or pins change
        _homeDirty = false;
        PinnedGrid.ItemsSource = PinnedApps();
        var apps = App.Apps.ToList();
        AllGrid.ItemsSource = apps;
        FolderGrid.Children.Clear();
        var order = Categories.Select(c => c.Name).Append("Other").ToList();
        foreach (var g in apps.GroupBy(CategoryOf).OrderBy(g => order.IndexOf(g.Key)))
            FolderGrid.Children.Add(FolderTile(g.Key, g.OrderByDescending(a => App.Settings.Launches.GetValueOrDefault(a.ParsingName)).ThenBy(a => a.Name).ToList()));
    }

    /// <summary>A folder: four of its apps in a 2×2 grid, its name and count.</summary>
    Border FolderTile(string name, List<AppEntry> apps)
    {
        var grid = new System.Windows.Controls.Primitives.UniformGrid { Columns = 2, Rows = 2, Width = 92, Height = 92, Margin = new Thickness(0, 4, 0, 6) };
        foreach (var a in apps.Take(4))
            grid.Children.Add(new ContentControl { Content = a, ContentTemplate = (DataTemplate)FindResource("AppIcon"), Width = 34, Height = 34, Focusable = false });
        var label = new TextBlock { Text = name, FontSize = 12.5, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        label.SetResourceReference(TextBlock.ForegroundProperty, "Text");
        var count = new TextBlock { Text = $"{apps.Count} apps", FontSize = 11, HorizontalAlignment = HorizontalAlignment.Center };
        count.SetResourceReference(TextBlock.ForegroundProperty, "TextDim");
        var stack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        stack.Children.Add(grid); stack.Children.Add(label); stack.Children.Add(count);
        var tile = new Border { Width = 148, Height = 160, Margin = new Thickness(4), CornerRadius = new CornerRadius(18), Child = stack, Cursor = Cursors.Hand, BorderThickness = new Thickness(1) };
        tile.SetResourceReference(Border.BackgroundProperty, "SurfaceAlt");
        tile.SetResourceReference(Border.BorderBrushProperty, "Border");
        tile.MouseEnter += (_, _) => tile.SetResourceReference(Border.BackgroundProperty, "Hover");
        tile.MouseLeave += (_, _) => tile.SetResourceReference(Border.BackgroundProperty, "SurfaceAlt");
        tile.MouseLeftButtonUp += (_, _) => OpenFolder(name, apps);
        System.Windows.Automation.AutomationProperties.SetName(tile, name + " folder");
        return tile;
    }

    void OpenFolder(string name, List<AppEntry> apps)
    {
        FolderTitle.Text = name;
        FolderApps.ItemsSource = apps;
        HomeView.Visibility = Visibility.Collapsed;
        FolderView.Visibility = Visibility.Visible;
        Motion.From(FolderView, OpacityProperty, 0, 1, 180);
        Motion.Pop(FolderView, 0.95);
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, () =>
        {
            var tiles = Enumerable.Range(0, FolderApps.Items.Count).Select(i => FolderApps.ItemContainerGenerator.ContainerFromIndex(i)).OfType<UIElement>();
            Motion.Stagger(tiles, stepMs: 16, max: 24, rise: 10);
        });
    }

    void CloseFolder()
    {
        FolderView.Visibility = Visibility.Collapsed;
        HomeView.Visibility = SearchBox.Text.Trim().Length == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    void FolderBack_Click(object sender, RoutedEventArgs e) => CloseFolder();

    void AllView_Checked(object sender, RoutedEventArgs e)
    {
        if (FolderGrid == null || AllGrid == null) return;
        bool folders = FoldersView.IsChecked == true;
        FolderGrid.Visibility = folders ? Visibility.Visible : Visibility.Collapsed;
        AllGrid.Visibility = folders ? Visibility.Collapsed : Visibility.Visible;

    }

    void AppTile_Click(object sender, MouseButtonEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is AppEntry app) Launch(app);
    }

    // ───────────────────────── search ─────────────────────────

    void Refilter()
    {
        string q = SearchBox.Text.Trim();
        Placeholder.Visibility = SearchBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        bool searching = q.Length > 0;
        Chips.Visibility = SearchView.Visibility = searching ? Visibility.Visible : Visibility.Collapsed;
        if (!searching)
        {
            FolderView.Visibility = FolderView.Visibility; // keep an open folder
            if (FolderView.Visibility != Visibility.Visible) HomeView.Visibility = Visibility.Visible;
            CountText.Text = "";
            BuildHome();
            return;
        }
        HomeView.Visibility = FolderView.Visibility = Visibility.Collapsed;
        _appResults = AppIndex.Search(App.Apps, q, App.Settings.Launches).Take(24).Select(a => new Result
        {
            Title = a.Name, Subtitle = "App", Kind = "App", Group = "Apps", Entry = a, Icon = a.Icon, Open = () => Launch(a),
        }).ToList();
        _otherResults = _otherResults.Where(r => r.Kind == "Setting" && r.Title.Contains(q, StringComparison.CurrentCultureIgnoreCase)).ToList();
        Render();
    }

    async void SearchOthers()
    {
        _searchDelay.Stop();
        string q = SearchBox.Text.Trim();
        _searchCts?.Cancel();
        if (q.Length < 2) { _otherResults = new(); Render(); return; }
        var cts = _searchCts = new CancellationTokenSource();
        var list = new List<Result>();
        foreach (var (id, title, keywords) in SettingsWindow.PageIndex)
            if (title.Contains(q, StringComparison.CurrentCultureIgnoreCase) || keywords.Split(' ').Any(k => k.StartsWith(q, StringComparison.CurrentCultureIgnoreCase)))
                list.Add(new Result { Title = title, Subtitle = "Settings", Kind = "Setting", Group = "Settings", Icon = NorrskenApps.RenderTile("settings", 44), Open = () => App.MainDock?.OpenSettings(id) });
        var hits = await Task.Run(() => SystemSearch.Files(q, 8, cts.Token));
        if (cts.IsCancellationRequested) return;
        foreach (var h in hits)
            list.Add(new Result
            {
                Title = h.Name, Subtitle = System.IO.Path.GetDirectoryName(h.Path) ?? "", Kind = h.IsDir ? "Folder" : "File", Group = "Files and folders", Path = h.Path,
                Icon = Interop.ShellIcons.Get(h.Path, 48),
                Open = () => { if (h.IsDir) NorrskenApps.Launch("files", h.Path); else Files.Open(h.Path); },
            });
        list.Add(new Result { Title = $"Search for “{q}” in Files", Subtitle = "Home and every subfolder", Kind = "", Group = "Files and folders", Icon = NorrskenApps.RenderTile("files", 44), Open = () => NorrskenApps.Launch("files", Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)) });
        _otherResults = list;
        Render();
    }

    void Chip_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton { Tag: string f }) { _filter = f; if (IsLoaded) Render(); }
    }

    void Render()
    {
        if (ResultsPanel == null) return;
        string? keep = SelectedTitle;
        IEnumerable<Result> apps = _appResults, settings = _otherResults.Where(r => r.Kind == "Setting"), files = _otherResults.Where(r => r.Kind != "Setting");
        _shown = _filter switch
        {
            "apps" => apps.ToList(),
            "settings" => settings.ToList(),
            "files" => files.ToList(),
            _ => apps.Take(6).Concat(settings.Take(4)).Concat(files.Take(7)).ToList(),
        };
        ResultsPanel.Children.Clear();
        _rows.Clear();
        string? group = null;
        for (int i = 0; i < _shown.Count; i++)
        {
            var r = _shown[i];
            string g = i == 0 && _filter == "all" ? "Best match" : r.Group;
            if (g != group)
            {
                group = g;
                var h = new TextBlock { Text = g, FontSize = 12, FontWeight = FontWeights.SemiBold, Margin = new Thickness(12, i == 0 ? 4 : 14, 0, 4) };
                h.SetResourceReference(TextBlock.ForegroundProperty, "TextDim");
                ResultsPanel.Children.Add(h);
            }
            var row = ResultRow(r, i, big: i == 0 && _filter == "all");
            _rows.Add(row);
            ResultsPanel.Children.Add(row);
        }
        int count = _appResults.Count + _otherResults.Count;
        CountText.Text = count > 0 ? $"{count}" : "";
        EmptyText.Text = "Nothing matches";
        EmptyText.Visibility = _shown.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        PreviewCard.Visibility = _shown.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        int idx = keep == null ? 0 : _shown.FindIndex(r => r.Title == keep);
        Select(idx < 0 ? 0 : idx);
        // New results cascade in; the same results (only the selection moved) stay put.
        string signature = string.Join("|", _shown.Select(r => r.Title));
        if (signature != _lastSignature) { _lastSignature = signature; Motion.Stagger(ResultsPanel.Children.Cast<UIElement>(), stepMs: 14, max: 14, rise: 6, ms: 260); }
    }

    string _lastSignature = "";

    Border ResultRow(Result r, int index, bool big)
    {
        FrameworkElement icon = r.Entry != null
            ? new ContentControl { Content = r.Entry, ContentTemplate = (DataTemplate)FindResource("AppIcon"), Width = big ? 40 : 28, Height = big ? 40 : 28, Focusable = false }
            : new Image { Source = r.Icon, Width = big ? 36 : 24, Height = big ? 36 : 24 };
        icon.Margin = new Thickness(0, 0, 12, 0);
        var title = new TextBlock { Text = r.Title, FontSize = big ? 15 : 13.5, FontWeight = big ? FontWeights.SemiBold : FontWeights.Normal, TextTrimming = TextTrimming.CharacterEllipsis };
        title.SetResourceReference(TextBlock.ForegroundProperty, "Text");
        var sub = new TextBlock { Text = r.Kind == "App" ? "App" : r.Subtitle, FontSize = 11.5, TextTrimming = TextTrimming.CharacterEllipsis };
        sub.SetResourceReference(TextBlock.ForegroundProperty, "TextDim");
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(title); text.Children.Add(sub);
        var dock = new DockPanel();
        dock.Children.Add(icon);
        dock.Children.Add(text);
        var row = new Border { CornerRadius = new CornerRadius(12), Padding = new Thickness(12, big ? 10 : 7, 12, big ? 10 : 7), Margin = new Thickness(0, 1, 0, 1), Child = dock, Cursor = Cursors.Hand, Background = Brushes.Transparent };
        row.MouseEnter += (_, _) => { if (_rows.IndexOf(row) != _selected) row.SetResourceReference(Border.BackgroundProperty, "Hover"); };
        row.MouseLeave += (_, _) => { if (_rows.IndexOf(row) != _selected) row.Background = Brushes.Transparent; };
        row.MouseLeftButtonUp += (_, _) => OpenResult(r);
        row.MouseRightButtonUp += (_, _) => Select(index);
        return row;
    }

    void Select(int i)
    {
        if (_rows.Count == 0) { _selected = 0; PreviewPanel.Children.Clear(); return; }
        _selected = Math.Clamp(i, 0, _rows.Count - 1);
        for (int k = 0; k < _rows.Count; k++)
            if (k == _selected) _rows[k].SetResourceReference(Border.BackgroundProperty, "Selection"); else _rows[k].Background = Brushes.Transparent;
        _rows[_selected].BringIntoView();
        ShowPreview(_shown[_selected]);
    }

    void ShowPreview(Result r)
    {
        PreviewPanel.Children.Clear();
        FrameworkElement icon = r.Entry != null
            ? new ContentControl { Content = r.Entry, ContentTemplate = (DataTemplate)FindResource("AppIcon"), Width = 72, Height = 72, Focusable = false }
            : new Image { Source = r.Path != null && !Directory.Exists(r.Path) ? (Interop.ShellIcons.Thumbnail(r.Path, 256) ?? r.Icon) : r.Icon, MaxWidth = 220, MaxHeight = 150, Stretch = Stretch.Uniform };
        icon.HorizontalAlignment = HorizontalAlignment.Center;
        icon.Margin = new Thickness(0, 10, 0, 16);
        PreviewPanel.Children.Add(icon);
        var title = new TextBlock { Text = r.Title, FontSize = 16, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center };
        title.SetResourceReference(TextBlock.ForegroundProperty, "Text");
        PreviewPanel.Children.Add(title);
        var kind = new TextBlock { Text = r.Kind.Length > 0 ? r.Kind : "Search", FontSize = 12, TextAlignment = TextAlignment.Center, Margin = new Thickness(0, 2, 0, 0) };
        kind.SetResourceReference(TextBlock.ForegroundProperty, "TextDim");
        PreviewPanel.Children.Add(kind);
        if (r.Path != null)
        {
            var where = new TextBlock { Text = r.Subtitle, FontSize = 11.5, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center, Margin = new Thickness(0, 8, 0, 0) };
            where.SetResourceReference(TextBlock.ForegroundProperty, "TextDim");
            PreviewPanel.Children.Add(where);
        }
        var open = new Button { Style = (Style)FindResource("PrimaryButton"), Content = "Open", FontSize = 13, Margin = new Thickness(0, 18, 0, 8), HorizontalAlignment = HorizontalAlignment.Stretch };
        open.Click += (_, _) => OpenResult(r);
        PreviewPanel.Children.Add(open);
        void Extra(string label, Action a)
        {
            var b = new Button { Style = (Style)FindResource("IconButton"), Content = new TextBlock { Text = label, FontSize = 12.5, Margin = new Thickness(0, 7, 0, 7) }, HorizontalContentAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 2, 0, 0) };
            b.Click += (_, _) => { HideLauncher(); a(); };
            PreviewPanel.Children.Add(b);
        }
        if (r.Entry is { } app)
        {
            Extra(App.Pinned.Contains(app) ? "Remove from dock" : "Keep in dock", () => App.TogglePin(app));
            if (app.TargetPath is { } exe && exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && File.Exists(exe))
            {
                Extra("Run as administrator", () => { if (!App.TestMode) try { Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true, Verb = "runas" }); } catch (Exception) { } });
                Extra("Show in Files", () => NorrskenApps.Launch("files", exe));
            }
        }
        else if (r.Path is { } p)
        {
            Extra("Show in Files", () => NorrskenApps.Launch("files", p));
            Extra("Copy path", () => { if (!App.TestMode) Clipboard.SetText(p); });
        }
    }

    void OpenResult(Result r)
    {
        HideLauncher();
        try { r.Open(); } catch (Exception ex) { Log.Write("Search result failed: " + ex.Message); }
    }

    void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        Refilter();
        _searchDelay.Stop();
        if (SearchBox.Text.Trim().Length >= 2) _searchDelay.Start();
    }

    void SearchBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        bool searching = SearchBox.Text.Trim().Length > 0;
        switch (e.Key)
        {
            case Key.Down when searching: Select(_selected + 1); break;
            case Key.Up when searching: Select(_selected - 1); break;
            case Key.PageDown when searching: Select(_selected + 6); break;
            case Key.PageUp when searching: Select(_selected - 6); break;
            case Key.Tab when searching:
                var chips = Chips.Children.OfType<RadioButton>().ToList();
                int at = chips.FindIndex(c => c.IsChecked == true);
                chips[(at + (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? chips.Count - 1 : 1)) % chips.Count].IsChecked = true;
                break;
            case Key.Enter:
                if (searching && _shown.ElementAtOrDefault(_selected) is { } r) OpenResult(r);
                else if (!searching && PinnedGrid.Items.Count > 0 && PinnedGrid.Items[0] is AppEntry first) Launch(first);
                break;
            case Key.Escape:
                if (FolderView.Visibility == Visibility.Visible) CloseFolder();
                else if (SearchBox.Text.Length > 0) SearchBox.Text = "";
                else HideLauncher();
                break;
            default: return;
        }
        e.Handled = true;
    }

    void Open_Click(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is AppEntry app) Launch(app);
    }

    void TogglePin_Click(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is AppEntry app) App.TogglePin(app);
    }

    void Launch(AppEntry app)
    {
        HideLauncher();
        App.LaunchSafely(app);
    }

    // ───────────────────────── profile and power ─────────────────────────

    void Settings_Click(object sender, RoutedEventArgs e) { HideLauncher(); App.MainDock?.OpenSettings(); }
    void Account_Click(object sender, RoutedEventArgs e) { HideLauncher(); App.MainDock?.OpenSettings("accounts"); }
    void Lock_Click(object sender, RoutedEventArgs e) { HideLauncher(); if (!App.TestMode) Power.Lock(); }
    void Sleep_Click(object sender, RoutedEventArgs e) { HideLauncher(); if (!App.TestMode) Power.Sleep(); }
    void SignOut_Click(object sender, RoutedEventArgs e) => Confirm("Sign out? Unsaved work in open apps may be lost.", Power.SignOut);
    void Restart_Click(object sender, RoutedEventArgs e) => Confirm("Restart now? Unsaved work in open apps may be lost.", Power.Restart);
    void ShutDown_Click(object sender, RoutedEventArgs e) => Confirm("Shut down now? Unsaved work in open apps may be lost.", Power.ShutDown);

    void Confirm(string message, Action action)
    {
        HideLauncher();
        if (App.TestMode) { App.TestLaunches.Add("confirm:" + message); return; }
        if (MessageBox.Show(message, "Norrsken", MessageBoxButton.OKCancel, MessageBoxImage.Question) == MessageBoxResult.OK)
            action();
    }
}
