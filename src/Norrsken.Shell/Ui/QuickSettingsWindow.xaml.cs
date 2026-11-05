using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using Norrsken.Shell.Core;

namespace Norrsken.Shell.Ui;

/// <summary>Volume, now playing, brightness, network and shortcuts, anchored above the dock's clock.</summary>
public partial class QuickSettingsWindow : Window
{
    public DateTime LastHidden { get; private set; }
    bool _hiding, _updating;
    double _bottom = double.NaN;
    readonly DispatcherTimer _brightnessDebounce = new() { Interval = TimeSpan.FromMilliseconds(90) };
    readonly DispatcherTimer _clock = new() { Interval = TimeSpan.FromSeconds(1) };

    public QuickSettingsWindow()
    {
        InitializeComponent();
        SystemStatus.Changed += () => { if (IsVisible) RefreshStatus(); };
        SystemStatus.Media.Changed += () => { if (IsVisible) _ = RefreshMediaAsync(); };
        _brightnessDebounce.Tick += (_, _) =>
        {
            _brightnessDebounce.Stop();
            int v = (int)Math.Round(BrightnessSlider.Value);
            Task.Run(() => SystemStatus.SetBrightness(v));
        };
        _clock.Tick += (_, _) => { UpdateTime(); UpdateFocusTile(); };
        // Now playing and the radios load after the panel is placed and make it taller: grow upwards, never into the dock.
        SizeChanged += (_, _) => { if (IsVisible && !_hiding) KeepAboveDock(); };
    }

    /// <summary>Open with the card's bottom-right corner just above <paramref name="anchor"/> (DIPs).</summary>
    public void ShowAbove(Point anchor)
    {
        _hiding = false;
        SystemStatus.ReopenAudio(); // the default device may have changed since last time
        RefreshStatus();
        _ = RefreshMediaAsync();
        _ = RefreshBrightnessAsync();
        _ = RefreshRadiosAsync();
        UpdateTime();
        _clock.Start();

        Show();
        UpdateLayout();
        var area = SystemParameters.WorkArea;
        Left = Math.Clamp(anchor.X - ActualWidth + 24, area.Left, area.Right - ActualWidth);
        _bottom = anchor.Y + 24 - 12; // card's bottom edge 12 DIP above the dock (24 is the shadow margin)
        KeepAboveDock();
        Activate();

        Motion.From(Card, OpacityProperty, 0, 1, 160);
        Motion.From(CardScale, System.Windows.Media.ScaleTransform.ScaleXProperty, 0.94, 1, 320, Motion.SoftSpring);
        Motion.From(CardScale, System.Windows.Media.ScaleTransform.ScaleYProperty, 0.94, 1, 320, Motion.SoftSpring);
        Motion.From(CardShift, TranslateTransform.YProperty, 14, 0, 320, Motion.Out);
        Motion.Stagger(Tiles.Children.Cast<UIElement>(), stepMs: 24, max: 6, rise: 8, startMs: 60);
    }

    void KeepAboveDock()
    {
        if (double.IsNaN(_bottom)) return;
        Top = Math.Max(SystemParameters.WorkArea.Top, _bottom - ActualHeight);
    }

    /// <summary>Self-test: fill every section with live data without showing the window.</summary>
    internal void PrepareForPreview(MediaController.State? demo = null)
    {
        RefreshStatus();
        UpdateTime();
        // Fetch off the UI thread (blocking on WinRT awaits here would deadlock), apply on it.
        ApplyMedia(demo ?? Task.Run(SystemStatus.Media.GetAsync).GetAwaiter().GetResult());
        if (demo != null)
        {
            _updating = true;
            VolumeSlider.Value = 62;
            VolumeText.Text = "62";
            VolumeGlyph.Glyph = "volume";
            _updating = false;
        }
        ApplyBrightness(Task.Run(SystemStatus.GetBrightness).GetAwaiter().GetResult());
    }

    public void HidePanel()
    {
        if (!IsVisible || _hiding) return;
        _hiding = true;
        _clock.Stop();
        LastHidden = DateTime.UtcNow;
        Motion.To(CardShift, TranslateTransform.YProperty, 8, 130);
        Motion.To(Card, OpacityProperty, 0, 130, done: () =>
        {
            if (!_hiding) return;
            Hide();
            _hiding = false;
        });
    }

    protected override void OnDeactivated(EventArgs e)
    {
        base.OnDeactivated(e);
        HidePanel();
    }

    protected override void OnKeyDown(System.Windows.Input.KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Key == System.Windows.Input.Key.Escape) HidePanel();
    }

    void UpdateTime()
    {
        var now = DateTime.Now;
        TimeText.Text = now.ToString(App.Settings.Clock24h ? "HH:mm" : "h:mm", CultureInfo.CurrentCulture);
        DateText.Text = now.ToString("dddd d MMMM yyyy", CultureInfo.CurrentCulture);
    }

    void RefreshStatus()
    {
        _updating = true;
        VolumeRow.IsEnabled = SystemStatus.HasAudio;
        VolumeSlider.Value = SystemStatus.Volume;
        VolumeText.Text = SystemStatus.HasAudio ? $"{Math.Round(SystemStatus.Volume)}" : "—";
        VolumeGlyph.Glyph = SystemStatus.VolumeGlyph;
        MuteButton.ToolTip = SystemStatus.Muted ? "Unmute" : "Mute";
        _updating = false;

        var net = SystemStatus.Network();
        NetworkGlyph.Glyph = net.Glyph;
        NetworkText.Text = net.Label;
        NetworkTile.Tag = net.Online ? "on" : null;

        bool light = Theme.CurrentIsLight;
        ThemeTile.Tag = light ? "on" : null;
        ThemeText.Text = light ? "On" : "Off";

        var battery = Power.Battery();
        BatteryRow.Visibility = battery == null ? Visibility.Collapsed : Visibility.Visible;
        if (battery is { } b) BatteryText.Text = $"{b.Percent}%" + (b.Charging ? " · charging" : "");
    }

    async Task RefreshMediaAsync() => ApplyMedia(await SystemStatus.Media.GetAsync());

    void ApplyMedia(MediaController.State? state)
    {
        MediaCard.Visibility = state == null ? Visibility.Collapsed : Visibility.Visible;
        if (state == null) return;
        MediaTitle.Text = state.Title;
        MediaArtist.Text = state.Artist;
        MediaSource.Text = MediaController.SourceName(state.SourceAppId);
        PlayGlyph.Glyph = state.Playing ? "pause" : "play-solid";
        PlayGlyph.Filled = !state.Playing;
        if (state.Art != null)
        {
            ArtFrame.Background = new ImageBrush(state.Art) { Stretch = Stretch.UniformToFill };
            ArtGlyph.Visibility = Visibility.Collapsed;
        }
        else
        {
            ArtFrame.SetResourceReference(System.Windows.Controls.Border.BackgroundProperty, "Pressed");
            ArtGlyph.Visibility = Visibility.Visible;
        }
    }

    async Task RefreshBrightnessAsync() => ApplyBrightness(await Task.Run(SystemStatus.GetBrightness));

    void ApplyBrightness(int? level)
    {
        BrightnessRow.Visibility = level == null ? Visibility.Collapsed : Visibility.Visible;
        if (level is not int v) return;
        _updating = true;
        BrightnessSlider.Value = v;
        BrightnessText.Text = $"{v}";
        _updating = false;
    }

    void Volume_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        VolumeText.Text = $"{Math.Round(e.NewValue)}";
        if (!_updating) Sound.Play("volume");
        if (_updating) return;
        SystemStatus.Volume = e.NewValue;
        if (SystemStatus.Muted && e.NewValue > 0) SystemStatus.Muted = false;
    }

    void Brightness_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        BrightnessText.Text = $"{Math.Round(e.NewValue)}";
        if (_updating) return;
        _brightnessDebounce.Stop();
        _brightnessDebounce.Start();
    }

    void Mute_Click(object sender, RoutedEventArgs e) => SystemStatus.Muted = !SystemStatus.Muted;

    async void PlayPause_Click(object sender, RoutedEventArgs e) => await SystemStatus.Media.PlayPauseAsync();
    async void Next_Click(object sender, RoutedEventArgs e) => await SystemStatus.Media.NextAsync();
    async void Previous_Click(object sender, RoutedEventArgs e) => await SystemStatus.Media.PreviousAsync();

    void Theme_Click(object sender, RoutedEventArgs e)
    {
        App.SetTheme(Theme.CurrentIsLight ? "graphite" : "paper");
        RefreshStatus();
    }

    void Open(string uri)
    {
        HidePanel();
        if (App.TestMode) { App.TestLaunches.Add(uri); return; }
        try { Unelevated.Start(uri); }
        catch (Exception ex) { Log.Write($"{uri} failed: {ex.Message}"); }
    }

    // ───────────────────────── radios, focus, Today ─────────────────────────

    static async Task<Windows.Devices.Radios.Radio?> RadioAsync(Windows.Devices.Radios.RadioKind kind)
    {
        try
        {
            if (await Windows.Devices.Radios.Radio.RequestAccessAsync() != Windows.Devices.Radios.RadioAccessStatus.Allowed) return null;
            return (await Windows.Devices.Radios.Radio.GetRadiosAsync()).FirstOrDefault(r => r.Kind == kind);
        }
        catch (Exception) { return null; }
    }

    async Task RefreshRadiosAsync()
    {
        var bt = await RadioAsync(Windows.Devices.Radios.RadioKind.Bluetooth);
        BluetoothTile.Tag = bt?.State == Windows.Devices.Radios.RadioState.On ? "on" : null;
        BluetoothText.Text = bt == null ? "Devices" : bt.State == Windows.Devices.Radios.RadioState.On ? "On" : "Off";
        UpdateFocusTile();
        AlertsText.Text = Notifications.Items.Count > 0 ? $"{Notifications.Items.Count} new" : "Today";
    }

    void UpdateFocusTile()
    {
        string? left = NotificationCentre.FocusRemaining;
        FocusTile.Tag = left != null ? "on" : null;
        FocusText.Text = left ?? "25 min";
    }

    /// <summary>Wi-Fi tile: switches the Wi-Fi radio (Ethernet-only PCs: opens network settings).</summary>
    async void Network_Click(object sender, RoutedEventArgs e)
    {
        var wifi = await RadioAsync(Windows.Devices.Radios.RadioKind.WiFi);
        if (wifi == null) { OpenNorrskenSettings("network"); return; }
        if (App.TestMode) { App.TestLaunches.Add("wifi toggle"); return; }
        await wifi.SetStateAsync(wifi.State == Windows.Devices.Radios.RadioState.On ? Windows.Devices.Radios.RadioState.Off : Windows.Devices.Radios.RadioState.On);
        await Task.Delay(400);
        RefreshStatus();
    }

    async void Bluetooth_Click(object sender, RoutedEventArgs e)
    {
        var bt = await RadioAsync(Windows.Devices.Radios.RadioKind.Bluetooth);
        if (bt == null) { OpenNorrskenSettings("bluetooth"); return; }
        if (App.TestMode) { App.TestLaunches.Add("bluetooth toggle"); return; }
        await bt.SetStateAsync(bt.State == Windows.Devices.Radios.RadioState.On ? Windows.Devices.Radios.RadioState.Off : Windows.Devices.Radios.RadioState.On);
        await RefreshRadiosAsync();
    }

    void NetworkMore_Click(object sender, System.Windows.Input.MouseButtonEventArgs e) { OpenNorrskenSettings("network"); e.Handled = true; }
    void BluetoothMore_Click(object sender, System.Windows.Input.MouseButtonEventArgs e) { OpenNorrskenSettings("bluetooth"); e.Handled = true; }
    void NightLight_Click(object sender, RoutedEventArgs e) => Open("ms-settings:nightlight");

    void Focus_Click(object sender, RoutedEventArgs e)
    {
        if (NotificationCentre.FocusRemaining != null) NotificationCentre.StopFocus(); else NotificationCentre.StartFocus(25);
        UpdateFocusTile();
    }

    void Notifications_Click(object sender, RoutedEventArgs e) { HidePanel(); NotificationCentre.Toggle(); }
    void AllSettings_Click(object sender, RoutedEventArgs e) => OpenNorrskenSettings(null);

    void OpenNorrskenSettings(string? page)
    {
        HidePanel();
        App.MainDock?.OpenSettings(page);
    }

    void Settings_Click(object sender, RoutedEventArgs e)
    {
        HidePanel();
        App.MainDock?.OpenSettings();
    }
}
