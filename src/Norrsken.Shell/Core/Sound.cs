using System.Windows.Media;

namespace Norrsken.Shell.Core;

/// <summary>
/// Optional interface sounds. Off until turned on in Settings › Sound, and then each event can be muted
/// on its own. A sound pack is a folder of files named after the events (<c>launcher.wav</c>, <c>trash.wav</c>…);
/// Norrsken ships one in <c>sfx\</c> and you can point it at your own.
/// </summary>
public static class Sound
{
    /// <summary>Every event, its label in Settings, and whether it plays once sounds are on.</summary>
    public static readonly (string Id, string Label)[] Events =
    [
        ("startup", "Norrsken starts"), ("launcher", "The launcher opens"), ("click", "Opening apps from the dock"),
        ("toggle", "Switches"), ("notify", "Notifications"), ("capture", "Screenshots"), ("trash", "Deleting files"),
        ("empty", "Emptying the Bin"), ("restore", "Putting things back"), ("error", "Something went wrong"),
        ("hover", "Hovering over the dock"), ("volume", "Volume changes"),
    ];

    /// <summary>Events that would play constantly; they stay off until switched on.</summary>
    public static readonly string[] QuietByDefault = ["hover", "volume"];

    public static string Folder => App.Settings.SoundPack is { Length: > 0 } p && Directory.Exists(p) ? p : Path.Combine(AppContext.BaseDirectory, "sfx");

    public static string? FileFor(string id)
    {
        foreach (var ext in new[] { ".wav", ".mp3", ".wma" })
        {
            string f = Path.Combine(Folder, id + ext);
            if (File.Exists(f)) return f;
        }
        return null;
    }

    public static bool HasPack => Events.Any(e => FileFor(e.Id) != null);

    public static bool IsOn(string id) => !App.Settings.MutedSounds.Contains(id);

    static readonly HashSet<MediaPlayer> Playing = [];
    static readonly Dictionary<string, DateTime> Last = [];

    /// <summary>Plays the event's sound when sounds are on and that event isn't muted.</summary>
    public static void Play(string id)
    {
        if (!App.Settings.Sounds || App.TestMode || !IsOn(id)) return;
        Preview(id);
    }

    /// <summary>Plays regardless of the switches (Settings' preview buttons).</summary>
    public static void Preview(string id)
    {
        try
        {
            if (FileFor(id) is not { } file) return;
            var now = DateTime.UtcNow;
            if (Last.TryGetValue(id, out var t) && (now - t).TotalMilliseconds < 70) return; // a burst of the same event is one sound
            Last[id] = now;
            var dispatcher = System.Windows.Application.Current?.Dispatcher;
            if (dispatcher == null) return;
            dispatcher.BeginInvoke(() =>
            {
                var p = new MediaPlayer { Volume = Math.Clamp(App.Settings.SoundVolume, 0, 1) };
                Playing.Add(p);
                void Done() { Playing.Remove(p); p.Close(); }
                p.MediaEnded += (_, _) => Done();
                p.MediaFailed += (_, _) => Done();
                p.Open(new Uri(file));
                p.Play();
            });
        }
        catch (Exception ex) { Log.Write($"Sound {id}: {ex.Message}"); }
    }

    /// <summary>A switch the user just flipped (not one being set up as a page opens).</summary>
    public static void RegisterSwitchSounds() =>
        System.Windows.EventManager.RegisterClassHandler(typeof(System.Windows.Controls.CheckBox), System.Windows.Controls.Primitives.ToggleButton.ClickEvent,
            new System.Windows.RoutedEventHandler((_, _) => Play("toggle")));
}
