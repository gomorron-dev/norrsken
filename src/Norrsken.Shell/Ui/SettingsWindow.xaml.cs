using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using Norrsken.Shell.Core;
using Norrsken.Shell.Interop;
using Ellipse = System.Windows.Shapes.Ellipse;

namespace Norrsken.Shell.Ui;

public partial class SettingsWindow : Window
{
    readonly DockWindow? _dock;
    bool _loading = true;

    public SettingsWindow(DockWindow? dock)
    {
        _dock = dock;
        InitializeComponent();

        foreach (var t in Theme.All)
        {
            var rb = new RadioButton
            {
                GroupName = "Theme", Tag = t.Name, Uid = t.Id, Style = (Style)FindResource("ThemeCard"),
                IsChecked = Theme.Normalize(App.Settings.Theme) == t.Id, Content = PreviewTile(t.Id),
            };
            System.Windows.Automation.AutomationProperties.SetName(rb, t.Name + " theme");
            rb.Checked += Theme_Checked;
            ThemeList.Children.Add(rb);
        }

        MatchWallpaperToggle.IsChecked = App.Settings.MatchWallpaper;
        MagnifyToggle.IsChecked = App.Settings.Magnify;
        PreviewsToggle.IsChecked = App.Settings.WindowPreviews;
        ReserveToggle.IsChecked = App.Settings.ReserveSpace;
        AutoHideToggle.IsChecked = App.Settings.AutoHide;
        ClockToggle.IsChecked = App.Settings.ShowClock;
        BinToggle.IsChecked = App.Settings.ShowBinInDock;
        Clock24Toggle.IsChecked = App.Settings.Clock24h;
        SecondsToggle.IsChecked = App.Settings.ShowSeconds;
        ReduceMotionToggle.IsChecked = App.Settings.ReduceMotion;
        HideTaskbarToggle.IsChecked = App.Settings.HideWindowsTaskbar;
        FramesToggle.IsChecked = App.Settings.WindowFrames;
        CursorsToggle.IsChecked = App.Settings.CustomCursors;
        TitleBarsToggle.IsChecked = App.Settings.NorrskenTitleBars;
        ReplaceToggle.IsChecked = App.Settings.ReplaceSystemApps;
        BuildWallpapers();
        try { AutostartToggle.IsChecked = Autostart.IsEnabled; }
        catch (Exception ex) { AutostartToggle.IsEnabled = false; Log.Write("Autostart state unreadable: " + ex.Message); }

        AddKey("Open the launcher", DockWindow.LauncherHotkey);
        AddKey("Screenshot", "PrtScn");
        AddKey("Screenshot", "Win+Shift+S");
        AddKey("Files", "Win+E");
        AddKey("Settings", "Win+I");
        AddKey("Switch apps", "Alt+Tab");
        AddKey("Task view", "Win+Tab");
        AddKey("Show desktop", "Win+D");
        AddKey("Notifications", "Win+N");
        AddKey("Snap window", "Win+←/→");

        VersionText.Text = $"Norrsken {App.Version}";
        PathsText.Text = $"Settings  {ShellSettings.FilePath}\nLog  {Log.FilePath}";
        BuildPages();
        _loading = false;
        AppChrome.Attach(this, Root);
    }

    /// <summary>Wallpaper gallery: every Norrsken wallpaper; picking one stops the theme from changing it.</summary>
    void BuildWallpapers()
    {
        WallpaperList.Children.Clear();
        string dir = SystemLook.WallpaperDir;
        if (!Directory.Exists(dir)) { WallpaperList.Children.Add(new TextBlock { Text = "Norrsken wallpapers are installed with Norrsken OS.", Style = (Style)FindResource("Note") }); return; }
        foreach (var file in Directory.EnumerateFiles(dir, "*.png").Order())
        {
            var thumb = new Border { Width = 132, Height = 76, CornerRadius = Theme.R(10), ClipToBounds = true };
            var b = new Button { Margin = new Thickness(0, 0, 10, 10), Cursor = System.Windows.Input.Cursors.Hand, Template = WallTemplate(), Content = thumb, ToolTip = Path.GetFileNameWithoutExtension(file).Replace("norrsken-", "") };
            b.Click += (_, _) =>
            {
                if (App.TestMode) { SystemLook.TestLog.Add("wallpaper → " + Path.GetFileName(file)); return; }
                App.Settings.MatchWallpaper = false;
                MatchWallpaperToggle.IsChecked = false;
                App.Settings.Save();
                Files.SetWallpaper(file);
            };
            WallpaperList.Children.Add(b);
            _ = Task.Run(() =>
            {
                var img = new System.Windows.Media.Imaging.BitmapImage();
                img.BeginInit(); img.UriSource = new Uri(file); img.DecodePixelWidth = 264; img.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad; img.EndInit(); img.Freeze();
                return img;
            }).ContinueWith(t => { if (t.Status == TaskStatus.RanToCompletion) thumb.Background = new ImageBrush(t.Result) { Stretch = Stretch.UniformToFill }; }, TaskScheduler.FromCurrentSynchronizationContext());
        }
    }

    static ControlTemplate WallTemplate()
    {
        var t = new ControlTemplate(typeof(Button));
        var bd = new FrameworkElementFactory(typeof(Border)) { Name = "Bd" };
        bd.SetValue(Border.CornerRadiusProperty, Theme.R(12));
        bd.SetValue(Border.BorderThicknessProperty, new Thickness(2));
        bd.SetValue(Border.BorderBrushProperty, Brushes.Transparent);
        bd.SetValue(Border.PaddingProperty, new Thickness(2));
        bd.AppendChild(new FrameworkElementFactory(typeof(ContentPresenter)));
        t.VisualTree = bd;
        var hover = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
        hover.Setters.Add(new Setter(Border.BorderBrushProperty, new DynamicResourceExtension("TextDim"), "Bd"));
        t.Triggers.Add(hover);
        return t;
    }

    /// <summary>A miniature desktop: the theme's dock floating over its window colour.</summary>
    internal static FrameworkElement PreviewTile(string id)
    {
        var (dock, surface, text) = Theme.Preview(id);
        var backdrop = new LinearGradientBrush(Shade(surface, 0.10), Shade(surface, 0.24), 90);
        var tile = new Grid { Width = 118, Height = 78 };
        tile.Children.Add(new Border
        {
            CornerRadius = Theme.R(12), Background = backdrop,
            BorderBrush = new SolidColorBrush(Color.FromArgb(48, 128, 128, 128)), BorderThickness = new Thickness(1),
        });
        var dots = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        for (int i = 0; i < 4; i++)
            dots.Children.Add(new Ellipse
            {
                Width = 7, Height = 7, Margin = new Thickness(3, 0, 3, 0),
                Fill = new SolidColorBrush(Color.FromArgb((byte)(i == 0 ? 255 : 120), text.R, text.G, text.B)),
            });
        tile.Children.Add(new Border
        {
            Width = 74, Height = 18, CornerRadius = Theme.R(9), Background = new SolidColorBrush(dock),
            BorderBrush = new SolidColorBrush(Color.FromArgb(40, text.R, text.G, text.B)), BorderThickness = new Thickness(1),
            VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 0, 9), Child = dots,
        });
        return tile;
    }

    static Color Shade(Color c, double towardGrey)
    {
        byte Mix(byte v) => (byte)(v + (128 - v) * towardGrey);
        return Color.FromRgb(Mix(c.R), Mix(c.G), Mix(c.B));
    }

    void AddKey(string action, string keys)
    {
        if (KeyList.Children.Count > 0) KeyList.Children.Add(new Border { Style = (Style)FindResource("Hairline") });
        var row = new DockPanel { Margin = new Thickness(0, 11, 0, 11) };
        var chips = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var k in keys.Split('+'))
            chips.Children.Add(new Border { Style = (Style)FindResource("Kbd"), Child = new TextBlock { Text = k, FontSize = 11.5 } });
        DockPanel.SetDock(chips, Dock.Right);
        row.Children.Add(chips);
        row.Children.Add(new TextBlock { Text = action, FontSize = 13.5, VerticalAlignment = VerticalAlignment.Center });
        KeyList.Children.Add(row);
    }

    void Theme_Checked(object sender, RoutedEventArgs e)
    {
        if (_loading || sender is not RadioButton { Uid: { Length: > 0 } id }) return;
        App.SetTheme(id);
    }

    void Toggle_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        var s = App.Settings;
        bool matchTurnedOn = !s.MatchWallpaper && MatchWallpaperToggle.IsChecked == true;
        s.MatchWallpaper = MatchWallpaperToggle.IsChecked == true;
        if (matchTurnedOn) _ = SystemLook.ApplyAsync(Theme.Current);
        s.Magnify = MagnifyToggle.IsChecked == true;
        s.WindowPreviews = PreviewsToggle.IsChecked == true;
        s.ReserveSpace = ReserveToggle.IsChecked == true;
        s.AutoHide = AutoHideToggle.IsChecked == true;
        s.ShowClock = ClockToggle.IsChecked == true;
        s.ShowBinInDock = BinToggle.IsChecked == true;
        s.Clock24h = Clock24Toggle.IsChecked == true;
        s.ShowSeconds = SecondsToggle.IsChecked == true;
        s.ReduceMotion = ReduceMotionToggle.IsChecked == true;
        s.HideWindowsTaskbar = HideTaskbarToggle.IsChecked == true;
        bool frames = FramesToggle.IsChecked == true, cursors = CursorsToggle.IsChecked == true, replace = ReplaceToggle.IsChecked == true;
        bool framesChanged = frames != s.WindowFrames, cursorsChanged = cursors != s.CustomCursors, replaceChanged = replace != s.ReplaceSystemApps;
        s.WindowFrames = frames; s.CustomCursors = cursors; s.ReplaceSystemApps = replace;
        s.Save();
        if (framesChanged) FrameStyler.Refresh();
        if (TitleBarsToggle.IsChecked != s.NorrskenTitleBars) { s.NorrskenTitleBars = TitleBarsToggle.IsChecked == true; s.Save(); TitleBars.Refresh(); }
        if (cursorsChanged) NorrskenCursors.Apply();
        if (replaceChanged)
        {
            if (replace) { if (s.ReplacedAppsRetired) AppRetirement.RegisterHandlers(Environment.ProcessPath!); }
            else AppRetirement.UnregisterHandlers();
            _ = App.LoadAppsAsync();
        }
        _dock?.ApplySettings();
    }

    void InstallApps_Click(object sender, RoutedEventArgs e) => new WelcomeWindow(appsOnly: true).Show();

    void Autostart_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        try { Autostart.Set(AutostartToggle.IsChecked == true); }
        catch (Exception ex)
        {
            Log.Write("Autostart change failed: " + ex.Message);
            _loading = true;
            AutostartToggle.IsChecked = !AutostartToggle.IsChecked;
            _loading = false;
        }
    }
}
