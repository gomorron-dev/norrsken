using System.Windows.Media;

namespace Norrsken.Shell.Core;

/// <summary>
/// Norrsken's own icon set for built-in Windows apps: single-weight line glyphs on a 24-unit grid,
/// drawn in the theme's foreground on a neutral tile. Third-party apps keep their own icons.
/// </summary>
public static class IconPack
{
    // 24×24 grid, 2-unit safe margin, drawn as 1.7-unit round strokes.
    static readonly Dictionary<string, string> Glyphs = new()
    {
        ["files"] = "M3,7.5 C3,6.4 3.9,5.5 5,5.5 L9.2,5.5 L11,7.5 L19,7.5 C20.1,7.5 21,8.4 21,9.5 L21,17.5 C21,18.6 20.1,19.5 19,19.5 L5,19.5 C3.9,19.5 3,18.6 3,17.5 Z M3,10.5 H21",
        ["terminal"] = "M5,4.5 H19 A2.5,2.5 0 0 1 21.5,7 V17 A2.5,2.5 0 0 1 19,19.5 H5 A2.5,2.5 0 0 1 2.5,17 V7 A2.5,2.5 0 0 1 5,4.5 Z M7,9.5 L10,12 L7,14.5 M12.5,14.5 H16.5",
        ["prompt"] = "M3,5 H21 V19 H3 Z M3,8.5 H21 M6.5,12 L9,14 L6.5,16 M11,16 H14.5",
        ["powershell"] = "M7,5 H21.5 L17,19 H2.5 Z M8,9.5 L11.5,12 L7,14.5 M12,15 H15",
        ["settings"] = "M4,8 H7 M11,8 H20 M4,16 H13 M17,16 H20 M9,6 A2,2 0 1 1 8.99,6 Z M15,14 A2,2 0 1 1 14.99,14 Z",
        ["activity"] = "M3,12.5 H7 L9.5,6 L14,18.5 L16.5,12.5 H21",
        ["panel"] = "M4,4 H10 V10 H4 Z M14,4 H20 V10 H14 Z M4,14 H10 V20 H4 Z M14,14 H20 V20 H14 Z",
        ["note"] = "M6,3.5 H14.5 L18.5,7.5 V20.5 H6 Z M14.5,3.5 V7.5 H18.5 M8.5,11.5 H15.5 M8.5,14.5 H15.5 M8.5,17.5 H12.5",
        ["calculator"] = "M7,3 H17 A2,2 0 0 1 19,5 V19 A2,2 0 0 1 17,21 H7 A2,2 0 0 1 5,19 V5 A2,2 0 0 1 7,3 Z M8.5,6.5 H15.5 V9.5 H8.5 Z M9,13 H9.1 M12,13 H12.1 M15,13 H15.1 M9,17 H9.1 M12,17 H12.1 M15,17 H15.1",
        ["paint"] = "M14.5,4 L20,9.5 L11.5,18 L6,12.5 Z M6,12.5 C3.5,14.5 4.5,18 3.5,20.5 C6,19.5 9.5,20.5 11.5,18",
        ["snip"] = "M7,3 V17 H21 M3,7 H17 V21",
        ["photos"] = "M4,5 H20 V19 H4 Z M4,16 L9,11 L13,15 L15.5,12.5 L20,17 M15.5,7.5 A1.5,1.5 0 1 1 15.49,7.5 Z",
        ["camera"] = "M3.5,8 H7.5 L9.5,5.5 H14.5 L16.5,8 H20.5 V18.5 H3.5 Z M12,10 A3,3 0 1 1 11.99,10 Z",
        ["clock"] = "M12,3.5 A8.5,8.5 0 1 1 11.99,3.5 Z M12,7.5 V12 L15,14",
        ["calendar"] = "M4.5,6 H19.5 V19.5 H4.5 Z M4.5,10 H19.5 M8.5,3.5 V7.5 M15.5,3.5 V7.5",
        ["mail"] = "M3.5,6 H20.5 V18 H3.5 Z M3.5,6.5 L12,13 L20.5,6.5",
        ["store"] = "M5,8 H19 L18,20 H6 Z M9,8 V6.5 A3,3 0 0 1 15,6.5 V8",
        ["play"] = "M12,3.5 A8.5,8.5 0 1 1 11.99,3.5 Z M10,8.5 L15.5,12 L10,15.5 Z",
        ["mic"] = "M12,3.5 A2.5,2.5 0 0 1 14.5,6 V11 A2.5,2.5 0 0 1 9.5,11 V6 A2.5,2.5 0 0 1 12,3.5 Z M6.5,11 A5.5,5.5 0 0 0 17.5,11 M12,16.5 V20.5 M9,20.5 H15",
        ["shield"] = "M12,3 L19.5,6 V11.5 C19.5,16 16.3,19.3 12,21 C7.7,19.3 4.5,16 4.5,11.5 V6 Z M9,12 L11.2,14.2 L15.5,9.8",
        ["run"] = "M4.5,12 H18 M13,7 L18,12 L13,17",
        ["registry"] = "M4,4 H10 V10 H4 Z M14,14 H20 V20 H14 Z M4,14 H10 V20 H4 Z M10,7 H17 V14",
        ["glyphs"] = "M6,19 L12,5 L18,19 M8.5,14 H15.5",
        ["weather"] = "M8,17 H17 A3.5,3.5 0 0 0 16.4,10.1 A5,5 0 0 0 7,11.5 A2.8,2.8 0 0 0 8,17 Z",
        ["map"] = "M3.5,6 L9,4 L15,6 L20.5,4 V18 L15,20 L9,18 L3.5,20 Z M9,4 V18 M15,6 V20",
        ["gamepad"] = "M7,8.5 H17 A4,4 0 0 1 21,12.5 V14.5 A2.5,2.5 0 0 1 16.8,16.3 L15,14.5 H9 L7.2,16.3 A2.5,2.5 0 0 1 3,14.5 V12.5 A4,4 0 0 1 7,8.5 Z M8,10.5 V13.5 M6.5,12 H9.5 M15.5,11.5 H15.6 M17.5,13 H17.6",
        ["display"] = "M3.5,5 H20.5 V16 H3.5 Z M9,20 H15 M12,16 V20",
        ["cleanup"] = "M4,20 H20 M7,20 L8,11 H16 L17,20 M10,11 V4 H14 V11",
        ["trash"] = "M4.5,6.5 H19.5 M9.5,6.5 V4.5 H14.5 V6.5 M6.5,6.5 L7.5,20 H16.5 L17.5,6.5 M10.2,10 V16.5 M13.8,10 V16.5",
        ["trash-full"] = "M4.5,6.5 H19.5 M9.5,6.5 V4.5 H14.5 V6.5 M6.5,6.5 L7.5,20 H16.5 L17.5,6.5 M9,13 L11,10.5 L13,12.5 L15,10 M9.5,16.5 H14.5",
        ["trash-open"] = "M5,8.5 L18.5,4.5 M9.8,7 L9.3,5.3 L13.6,4 L14.1,5.7 M6.5,9.5 L7.5,20 H16.5 L17.5,9.5",
        ["restore"] = "M5.5,12 A6.5,6.5 0 1 0 7.4,7.4 M5.5,4.5 V9 H10",
        ["shred"] = "M6.5,4 H17.5 V11 H6.5 Z M4.5,11 H19.5 M8,14 V20 M11,14 V18 M14,14 V20 M17,14 V17",
        ["pin"] = "M9,4 H15 M10,4 V10 L7,14 H17 L14,10 V4 M12,14 V20",
        ["reveal"] = "M3.5,7 V18 H20.5 V9 H11.5 L9.5,7 Z M12,12 V16 M10,14 H14",
        ["people"] = "M9,11 A3,3 0 1 1 8.99,11 Z M3.5,20 C3.5,16.5 6,14.5 9,14.5 C12,14.5 14.5,16.5 14.5,20 M16,5.5 A2.5,2.5 0 1 1 15.99,5.5 M17,13 C19.5,13.3 21,15.3 21,18",
        ["help"] = "M12,3.5 A8.5,8.5 0 1 1 11.99,3.5 Z M9.5,9.5 A2.5,2.5 0 1 1 13.2,11.7 C12.4,12.2 12,12.8 12,13.8 M12,17 H12.1",

        // System glyphs (quick settings, dock status) — same grid and weight as the app set.
        ["volume"] = "M4,9.5 H7.5 L12,5.5 V18.5 L7.5,14.5 H4 Z M15.5,9 A4,4 0 0 1 15.5,15 M18,6.5 A7.5,7.5 0 0 1 18,17.5",
        ["volume-low"] = "M4,9.5 H7.5 L12,5.5 V18.5 L7.5,14.5 H4 Z M15.5,9 A4,4 0 0 1 15.5,15",
        ["volume-mute"] = "M4,9.5 H7.5 L12,5.5 V18.5 L7.5,14.5 H4 Z M15.5,9.5 L20,14 M20,9.5 L15.5,14",
        ["wifi"] = "M3,9.5 A13,13 0 0 1 21,9.5 M6,12.8 A8.5,8.5 0 0 1 18,12.8 M9,16 A4.2,4.2 0 0 1 15,16 M12,19.2 H12.1",
        ["ethernet"] = "M7,5 H17 V14 H7 Z M12,14 V18 M7,18 H17 M10,8 V11 M12,8 V11 M14,8 V11",
        ["offline"] = "M3,9.5 A13,13 0 0 1 21,9.5 M6,12.8 A8.5,8.5 0 0 1 18,12.8 M9,16 A4.2,4.2 0 0 1 15,16 M12,19.2 H12.1 M4,4 L20,20",
        ["bluetooth"] = "M7,7.5 L17,16.5 L12,21 V3 L17,7.5 L7,16.5",
        ["sun"] = "M12,8 A4,4 0 1 1 11.99,8 Z M12,2.5 V4.5 M12,19.5 V21.5 M2.5,12 H4.5 M19.5,12 H21.5 M5.3,5.3 L6.7,6.7 M17.3,17.3 L18.7,18.7 M5.3,18.7 L6.7,17.3 M17.3,6.7 L18.7,5.3",
        ["moon"] = "M19.5,14.5 A8,8 0 1 1 9.5,4.5 A6.5,6.5 0 0 0 19.5,14.5 Z",
        ["contrast"] = "M12,3.5 A8.5,8.5 0 1 1 11.99,3.5 Z M12,3.5 V20.5 M12,7 H16 M12,11 H19.5 M12,15 H19 M12,19 H15",
        ["bell"] = "M6,16.5 V11 A6,6 0 0 1 18,11 V16.5 L19.5,18 H4.5 Z M10,20.5 H14",
        ["focus"] = "M12,3.5 A8.5,8.5 0 1 1 11.99,3.5 Z M12,8.5 A3.5,3.5 0 1 1 11.99,8.5 Z",
        ["battery"] = "M3.5,8 H18 V16 H3.5 Z M20.5,10.5 V13.5 M6,10.5 V13.5",
        ["prev"] = "M6.5,6 V18 M18,6 L9.5,12 L18,18 Z",
        ["next"] = "M17.5,6 V18 M6,6 L14.5,12 L6,18 Z",
        ["play-solid"] = "M8,5.5 L18.5,12 L8,18.5 Z",
        ["pause"] = "M8.5,5.5 V18.5 M15.5,5.5 V18.5",
        ["check"] = "M5,12.5 L10,17 L19,7.5",
        ["cross"] = "M7,7 L17,17 M17,7 L7,17",
        ["spinner"] = "M12,4 A8,8 0 0 1 20,12",
        ["download"] = "M12,4 V15 M7.5,10.5 L12,15 L16.5,10.5 M5,19.5 H19",
        ["music"] = "M9,17.5 A2.5,2.5 0 1 1 8.99,17.5 Z M11.5,17.5 V5 L19,3.5 V15 M19,15 A2.5,2.5 0 1 1 18.99,15 Z",
    };

    // shell:AppsFolder package names (the part before "_"), explicit AppUserModelIDs, and executables.
    static readonly Dictionary<string, string> ByPackage = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Microsoft.Windows.Explorer"] = "files",
        ["Microsoft.WindowsTerminal"] = "terminal", ["Microsoft.WindowsTerminalPreview"] = "terminal",
        ["windows.immersivecontrolpanel"] = "settings",
        ["Microsoft.Windows.ControlPanel"] = "panel",
        ["Microsoft.WindowsNotepad"] = "note",
        ["Microsoft.WindowsCalculator"] = "calculator",
        ["Microsoft.Paint"] = "paint",
        ["Microsoft.ScreenSketch"] = "snip",
        ["Microsoft.Windows.Photos"] = "photos",
        ["Microsoft.WindowsCamera"] = "camera",
        ["Microsoft.WindowsAlarms"] = "clock",
        ["microsoft.windowscommunicationsapps"] = "mail",
        ["Microsoft.OutlookForWindows"] = "mail",
        ["Microsoft.WindowsStore"] = "store",
        ["Microsoft.ZuneMusic"] = "play", ["Microsoft.ZuneVideo"] = "play",
        ["Microsoft.WindowsSoundRecorder"] = "mic",
        ["Microsoft.SecHealthUI"] = "shield",
        ["Microsoft.Windows.Shell.RunDialog"] = "run",
        ["Microsoft.BingWeather"] = "weather",
        ["Microsoft.WindowsMaps"] = "map",
        ["Microsoft.XboxGamingOverlay"] = "gamepad", ["Microsoft.GamingApp"] = "gamepad",
        ["Microsoft.People"] = "people",
        ["Microsoft.GetHelp"] = "help", ["Microsoft.Getstarted"] = "help",
    };

    static readonly Dictionary<string, string> ByExe = new(StringComparer.OrdinalIgnoreCase)
    {
        ["explorer.exe"] = "files", ["WindowsTerminal.exe"] = "terminal", ["SystemSettings.exe"] = "settings",
        ["Taskmgr.exe"] = "activity", ["control.exe"] = "panel", ["notepad.exe"] = "note",
        ["CalculatorApp.exe"] = "calculator", ["calc.exe"] = "calculator", ["mspaint.exe"] = "paint",
        ["SnippingTool.exe"] = "snip", ["Photos.exe"] = "photos", ["WindowsCamera.exe"] = "camera",
        ["Time.exe"] = "clock", ["WinStore.App.exe"] = "store", ["Microsoft.Media.Player.exe"] = "play",
        ["SoundRec.exe"] = "mic", ["cmd.exe"] = "prompt", ["powershell.exe"] = "powershell",
        ["powershell_ise.exe"] = "powershell", ["pwsh.exe"] = "powershell", ["regedit.exe"] = "registry",
        ["charmap.exe"] = "glyphs", ["mstsc.exe"] = "display", ["cleanmgr.exe"] = "cleanup",
        ["perfmon.exe"] = "activity", ["resmon.exe"] = "activity",
    };

    static readonly Dictionary<string, Geometry> Cache = new();

    public static IEnumerable<string> Names => Glyphs.Keys;

    public static Geometry? Named(string name)
    {
        if (!Glyphs.TryGetValue(name, out var data)) return null;
        lock (Cache)
        {
            if (!Cache.TryGetValue(name, out var g))
            {
                g = Geometry.Parse(data);
                g.Freeze();
                Cache[name] = g;
            }
            return g;
        }
    }

    /// <summary>Glyph for an installed app, from its shell:AppsFolder parsing name or link target.</summary>
    public static Geometry? ForApp(string parsingName, string? targetPath)
    {
        if (ByPackage.TryGetValue(parsingName, out var n) || ByPackage.TryGetValue(PackageName(parsingName), out n))
            return Named(n);
        return ForExe(targetPath ?? (Path.IsPathRooted(parsingName) || parsingName.StartsWith('{') ? parsingName : null));
    }

    public static Geometry? ForExe(string? exePath)
    {
        if (string.IsNullOrEmpty(exePath)) return null;
        return ByExe.TryGetValue(Path.GetFileName(exePath), out var n) ? Named(n) : null;
    }

    // "Microsoft.WindowsCalculator_8wekyb3d8bbwe!App" -> "Microsoft.WindowsCalculator"
    static string PackageName(string aumid)
    {
        int bang = aumid.IndexOf('!');
        string family = bang >= 0 ? aumid[..bang] : aumid;
        int us = family.LastIndexOf('_');
        return us > 0 ? family[..us] : family;
    }
}
