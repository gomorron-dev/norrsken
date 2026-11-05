using System.Management;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Norrsken.Shell.Interop;
using Windows.Media.Control;
using Windows.Networking.Connectivity;
using Windows.Storage.Streams;

namespace Norrsken.Shell.Core;

/// <summary>Live system state for the dock's status glyphs and the quick-settings panel.</summary>
public static class SystemStatus
{
    static AudioEndpoint? _audio;

    /// <summary>Raised on the UI thread when volume/mute or network state change.</summary>
    public static event Action? Changed;

    public static MediaController Media { get; } = new();

    public static void Start()
    {
        ReopenAudio();
        NetworkInformation.NetworkStatusChanged += _ => Raise();
        _ = Media.StartAsync();
    }

    /// <summary>The default output device can change (headphones plugged in); reopen on demand.</summary>
    public static void ReopenAudio()
    {
        _audio?.Dispose();
        _audio = AudioEndpoint.OpenDefault();
        if (_audio != null) _audio.Changed += Raise;
    }

    static void Raise() => Application.Current?.Dispatcher.BeginInvoke(() => Changed?.Invoke());

    public static bool HasAudio => _audio != null;
    public static double Volume { get => (_audio?.Volume ?? 0) * 100; set { if (_audio != null) _audio.Volume = (float)(value / 100); } }
    public static bool Muted { get => _audio?.Muted ?? false; set { if (_audio != null) _audio.Muted = value; } }

    public static string VolumeGlyph => Muted || Volume < 0.5 ? "volume-mute" : Volume < 40 ? "volume-low" : "volume";

    public readonly record struct NetworkState(string Glyph, string Label, bool Online);

    public static NetworkState Network()
    {
        try
        {
            var p = NetworkInformation.GetInternetConnectionProfile();
            if (p == null) return new("offline", "Offline", false);
            var level = p.GetNetworkConnectivityLevel();
            bool wlan = p.IsWlanConnectionProfile;
            string name = (wlan ? p.WlanConnectionProfileDetails?.GetConnectedSsid() : null) ?? p.ProfileName ?? "Connected";
            return level switch
            {
                NetworkConnectivityLevel.InternetAccess => new(wlan ? "wifi" : "ethernet", name, true),
                NetworkConnectivityLevel.ConstrainedInternetAccess => new("offline", "Limited", false),
                _ => new("offline", "No internet", false),
            };
        }
        catch (Exception ex)
        {
            Log.Write("Network status unavailable: " + ex.Message);
            return new("ethernet", "Network", true);
        }
    }

    // Brightness is only controllable on built-in (laptop) panels; external monitors report nothing.
    public static int? GetBrightness()
    {
        try
        {
            using var s = new ManagementObjectSearcher(@"root\WMI", "SELECT CurrentBrightness FROM WmiMonitorBrightness");
            foreach (ManagementObject o in s.Get()) return Convert.ToInt32(o["CurrentBrightness"]);
        }
        catch (ManagementException) { }
        catch (System.Runtime.InteropServices.COMException) { }
        return null;
    }

    public static void SetBrightness(int percent)
    {
        try
        {
            using var s = new ManagementObjectSearcher(@"root\WMI", "SELECT * FROM WmiMonitorBrightnessMethods");
            foreach (ManagementObject o in s.Get()) o.InvokeMethod("WmiSetBrightness", [1u, (byte)Math.Clamp(percent, 0, 100)]);
        }
        catch (Exception ex) { Log.Write("Brightness change failed: " + ex.Message); }
    }
}

/// <summary>Whatever is playing system-wide (Spotify, browsers, Media Player...), via Windows' media session API.</summary>
public sealed class MediaController
{
    GlobalSystemMediaTransportControlsSessionManager? _manager;
    GlobalSystemMediaTransportControlsSession? _session;

    public sealed record State(string Title, string Artist, string SourceAppId, bool Playing, ImageSource? Art);

    static readonly Dictionary<string, string> KnownSources = new(StringComparer.OrdinalIgnoreCase)
    {
        ["chrome"] = "Google Chrome", ["msedge"] = "Microsoft Edge", ["308046B0AF4A39CB"] = "Firefox", ["firefox"] = "Firefox",
        ["brave"] = "Brave", ["opera"] = "Opera", ["vivaldi"] = "Vivaldi", ["spotify"] = "Spotify", ["discord"] = "Discord",
        ["vlc"] = "VLC", ["Microsoft.ZuneMusic"] = "Media Player", ["Microsoft.ZuneVideo"] = "Films & TV", ["flickerbox"] = "Flickerbox",
    };

    /// <summary>
    /// The playing app's name as people know it: "Google Chrome", not "Chrome.UserData"; "Spotify", not
    /// "SpotifyAB.SpotifyMusic_zpdnekdrzrea0!Spotify".
    /// </summary>
    public static string SourceName(string id)
    {
        if (string.IsNullOrWhiteSpace(id)) return "";
        if (App.Apps.FirstOrDefault(a => a.ParsingName.Equals(id, StringComparison.OrdinalIgnoreCase)) is { } exact) return exact.Name;
        string bare = id.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? id[..^4] : id;
        string family = bare.Split('!')[0].Split('_')[0];      // packaged apps: Publisher.Name_hash!App
        string first = bare.Split('.', '!', '_')[0];           // Win32: Chrome.UserData, Chrome._crx_…
        foreach (var key in new[] { family, first, family.Split('.').Last() })
            if (KnownSources.TryGetValue(key, out var known)) return known;
        if (App.Apps.FirstOrDefault(a => a.ParsingName.StartsWith(family + "_", StringComparison.OrdinalIgnoreCase)
                                         || Path.GetFileNameWithoutExtension(a.TargetPath ?? "").Equals(first, StringComparison.OrdinalIgnoreCase)) is { } app)
            return app.Name;
        string name = family.Contains('.') ? family.Split('.').Last() : first;
        return name.Length > 0 ? char.ToUpperInvariant(name[0]) + name[1..] : id;
    }

    /// <summary>Raised on the UI thread when the session, track or play state changes.</summary>
    public event Action? Changed;

    public async Task StartAsync()
    {
        try
        {
            _manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
            _manager.CurrentSessionChanged += (_, _) => Attach();
            Attach();
        }
        catch (Exception ex) { Log.Write("Media sessions unavailable: " + ex.Message); }
    }

    void Attach()
    {
        if (_session != null)
        {
            _session.MediaPropertiesChanged -= OnProperties;
            _session.PlaybackInfoChanged -= OnPlayback;
        }
        _session = _manager?.GetCurrentSession();
        if (_session != null)
        {
            _session.MediaPropertiesChanged += OnProperties;
            _session.PlaybackInfoChanged += OnPlayback;
        }
        Raise();
    }

    void OnProperties(GlobalSystemMediaTransportControlsSession s, MediaPropertiesChangedEventArgs e) => Raise();
    void OnPlayback(GlobalSystemMediaTransportControlsSession s, PlaybackInfoChangedEventArgs e) => Raise();
    void Raise() => Application.Current?.Dispatcher.BeginInvoke(() => Changed?.Invoke());

    public async Task<State?> GetAsync()
    {
        var s = _session;
        if (s == null) return null;
        try
        {
            var props = await s.TryGetMediaPropertiesAsync();
            bool playing = s.GetPlaybackInfo()?.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
            ImageSource? art = props.Thumbnail != null ? await LoadArt(props.Thumbnail) : null;
            if (string.IsNullOrWhiteSpace(props.Title)) return null;
            return new(props.Title, props.Artist ?? "", s.SourceAppUserModelId ?? "", playing, art);
        }
        catch (Exception ex)
        {
            Log.Write("Media state unavailable: " + ex.Message);
            return null;
        }
    }

    static async Task<ImageSource?> LoadArt(IRandomAccessStreamReference reference)
    {
        try
        {
            using var ras = await reference.OpenReadAsync();
            using var stream = ras.AsStreamForRead();
            var ms = new MemoryStream();
            await stream.CopyToAsync(ms);
            ms.Position = 0;
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.DecodePixelWidth = 128;
            bmp.StreamSource = ms;
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        catch (Exception) { return null; }
    }

    public async Task PlayPauseAsync() { if (_session is { } s) await s.TryTogglePlayPauseAsync(); }
    public async Task NextAsync() { if (_session is { } s) await s.TrySkipNextAsync(); }
    public async Task PreviousAsync() { if (_session is { } s) await s.TrySkipPreviousAsync(); }
}
