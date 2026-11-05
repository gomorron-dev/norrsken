using System.Text.Json;

namespace Norrsken.Shell.Core;

public sealed class ShellSettings
{
    public string Theme { get; set; } = "graphite";
    public bool Clock24h { get; set; } = true;
    public bool ShowSeconds { get; set; }
    public bool ShowClock { get; set; } = true;
    public bool Magnify { get; set; } = true;
    public bool WindowPreviews { get; set; } = true;
    /// <summary>Reserve the dock's strip so maximised windows stop above it.</summary>
    public bool ReserveSpace { get; set; } = true;
    /// <summary>Slide the dock away when not in use; reveal it at the bottom edge.</summary>
    public bool AutoHide { get; set; }
    public bool ReduceMotion { get; set; }
    /// <summary>Desktop wallpaper and lock screen follow the theme (on by default in the OS image).</summary>
    public bool MatchWallpaper { get; set; }
    /// <summary>Norrsken OS: set this account's Windows look (dark mode, file extensions, wallpaper) once.</summary>
    public bool ApplyUserLook { get; set; }
    public bool UserLookApplied { get; set; }
    /// <summary>The first-boot Welcome (look + app picker) has been completed or skipped.</summary>
    public bool WelcomeDone { get; set; }
    /// <summary>Hide the Windows taskbar while the dock runs (restored on exit, guarded against crashes).</summary>
    public bool HideWindowsTaskbar { get; set; }
    /// <summary>PrtScn, Win+Shift+S, Win+E and Win+I open Norrsken's apps; the Windows apps they replace are hidden from the launcher.</summary>
    public bool ReplaceSystemApps { get; set; } = true;
    /// <summary>Norrsken OS: remove Notepad and Snipping Tool for this account once (Norrsken Notes and Screenshot replace them).</summary>
    public bool RetireReplacedApps { get; set; }
    /// <summary>A tap of the Windows key opens Norrsken's launcher instead of Windows' Start menu.</summary>
    public bool WinKeyOpensLauncher { get; set; } = true;
    /// <summary>Norrsken (and Flickerbox) install new GitHub releases on their own.</summary>
    public bool AutoUpdate { get; set; } = true;
    // ── Look: accent, shape, dock ──
    /// <summary>Accent colour for on/selected states: "" keeps Norrsken monochrome; an id from Theme.Accents or "#RRGGBB".</summary>
    public string Accent { get; set; } = "";
    /// <summary>Pointer style: "dark", "light" or "accent".</summary>
    public string CursorStyle { get; set; } = "dark";
    /// <summary>Corner roundness: 0 square, 0.5 subtle, 1 Norrsken, 1.5 extra round.</summary>
    public double CornerScale { get; set; } = 1.0;
    /// <summary>Dock size: 0.8 compact … 1.4 large (icons, pill and clock together).</summary>
    public double DockScale { get; set; } = 1.0;
    /// <summary>How much icons grow under the pointer (0.55 = 55 %).</summary>
    public double MagnifyAmount { get; set; } = 0.55;
    /// <summary>Dock background opacity, 0.4 (glassy) … 1 (solid).</summary>
    public double DockOpacity { get; set; } = 1.0;

    // ── Motion ──
    /// <summary>Animation duration multiplier: 0.6 brisk … 1 normal … 1.5 relaxed.</summary>
    public double AnimationScale { get; set; } = 1.0;
    /// <summary>"natural", "springy", "smooth" or "snappy".</summary>
    public string MotionStyle { get; set; } = "natural";

    // ── Sound ──
    /// <summary>Interface sounds. Off until turned on in Settings › Sound.</summary>
    public bool Sounds { get; set; } = false;
    public double SoundVolume { get; set; } = 0.5;
    /// <summary>Folder of the sound pack ("" = the one that ships with Norrsken).</summary>
    public string SoundPack { get; set; } = "";
    /// <summary>Everything about the look and sound, in one string: app windows compare it to follow changes live.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string LookKey => string.Join("|", Theme, Accent, CornerScale, DockScale, MagnifyAmount, DockOpacity, AnimationScale, MotionStyle, Sounds, SoundVolume, SoundPack, string.Join(",", MutedSounds), ReduceMotion);

    /// <summary>Copies the look and sound settings from another copy (a newer save from the dock).</summary>
    public void TakeLook(ShellSettings s)
    {
        Theme = s.Theme; Accent = s.Accent; CornerScale = s.CornerScale; DockScale = s.DockScale; MagnifyAmount = s.MagnifyAmount; DockOpacity = s.DockOpacity;
        AnimationScale = s.AnimationScale; MotionStyle = s.MotionStyle; Sounds = s.Sounds; SoundVolume = s.SoundVolume; SoundPack = s.SoundPack;
        MutedSounds = [.. s.MutedSounds]; ReduceMotion = s.ReduceMotion;
    }

    /// <summary>Events whose sound you've switched off.</summary>
    public List<string> MutedSounds { get; set; } = ["hover", "volume"];
    /// <summary>The Bin in the dock (drop files on it, click to open).</summary>
    public bool ShowBinInDock { get; set; } = true;
    /// <summary>Bin items older than this many days are removed for good (0: never).</summary>
    public int BinAutoEmptyDays { get; set; } = 0;
    /// <summary>Deleting forever overwrites the data first.</summary>
    public bool BinShred { get; set; } = false;
    /// <summary>Bin items you've kept: auto-empty never touches them.</summary>
    public List<string> BinKept { get; set; } = [];
    public string BinFreedMonth { get; set; } = "";
    public long BinFreedBytes { get; set; }    /// <summary>Alt+Tab shows Norrsken's switcher instead of Windows'.</summary>
    public bool NorrskenAltTab { get; set; } = true;
    public bool ReplacedAppsRetired { get; set; }
    public bool MediaAppsRetired { get; set; }
    /// <summary>Times this account tried to register Norrsken's package identity (capped, see PackageIdentity).</summary>
    public int IdentityAttempts { get; set; }
    /// <summary>Title bars and borders of every app window follow the Norrsken theme.</summary>
    public bool WindowFrames { get; set; } = true;
    /// <summary>Norrsken's monochrome pointer set.</summary>
    public bool CustomCursors { get; set; } = true;
    public bool NotesWrap { get; set; } = true;
    public string FilesView { get; set; } = "grid";
    public bool FilesShowHidden { get; set; }
    public bool FilesPreview { get; set; }
    public int FilesIconSize { get; set; } = 64;
    public List<string> FilesPins { get; set; } = new();
    public List<string> RunHistory { get; set; } = new();
    public bool NightLight { get; set; }
    /// <summary>Norrsken title bars (centred title, pebble buttons) on other apps' windows.</summary>
    public bool NorrskenTitleBars { get; set; } = true;
    /// <summary>Norrsken's right-click menu on the desktop.</summary>
    public bool DesktopMenu { get; set; } = true;
    public bool NightLightScheduled { get; set; } = true;
    public double NightLightWarmth { get; set; } = 0.5;
    public List<string>? Pinned { get; set; }
    /// <summary>Apps pinned to the top of the launcher (null: Norrsken's defaults plus your most used).</summary>
    public List<string>? StartPins { get; set; }
    /// <summary>Launch counts by shell:AppsFolder parsing name, for the launcher's "Frequent" row.</summary>
    public Dictionary<string, int> Launches { get; set; } = new();

    static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    /// <summary>NORRSKEN_SETTINGS_DIR lets tests run without touching the user's real settings.</summary>
    public static string Directory =>
        Environment.GetEnvironmentVariable("NORRSKEN_SETTINGS_DIR") is { Length: > 0 } dir
            ? dir
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Norrsken");

    public static string FilePath => Path.Combine(Directory, "settings.json");

    public static ShellSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<ShellSettings>(File.ReadAllText(FilePath)) ?? new();
        }
        catch (Exception ex) { Log.Write($"Settings unreadable, using defaults: {ex.Message}"); }
        return new();
    }

    public void Save()
    {
        System.IO.Directory.CreateDirectory(Directory);
        string tmp = FilePath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(this, Json));
        File.Move(tmp, FilePath, overwrite: true);
    }
}

public static class Log
{
    static readonly object Gate = new();

    public static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Norrsken", "logs", "shell.log");

    public static void Write(string message)
    {
        try
        {
            lock (Gate)
            {
                System.IO.Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                File.AppendAllText(FilePath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {message}{Environment.NewLine}");
            }
        }
        catch (IOException) { }
    }
}
