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
    /// <summary>Alt+Tab shows Norrsken's switcher instead of Windows'.</summary>
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
