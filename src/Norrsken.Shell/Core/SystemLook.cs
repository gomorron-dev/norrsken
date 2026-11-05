using System.Diagnostics;
using System.Runtime.InteropServices;
using Windows.Storage;
using Windows.System.UserProfile;

namespace Norrsken.Shell.Core;

/// <summary>
/// The parts of the system look outside Norrsken's own windows: the desktop wallpaper and the
/// lock/sign-in screen follow the Norrsken theme, and the boot menu entry is named "Norrsken OS".
/// Only documented APIs and bcdedit; no system files are patched, Secure Boot is untouched.
/// </summary>
public static class SystemLook
{
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern bool SystemParametersInfoW(uint action, uint param, string value, uint flags);
    const uint SPI_SETDESKWALLPAPER = 0x0014, SPIF_UPDATEINIFILE = 0x01, SPIF_SENDCHANGE = 0x02;

    /// <summary>UI tests record what would change instead of touching the real wallpaper or lock screen.</summary>
    public static List<string> TestLog { get; } = new();

    public static string WallpaperDir => Environment.GetEnvironmentVariable("NORRSKEN_WALLPAPER_DIR") is { Length: > 0 } d ? d
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), @"Web\Wallpaper\Norrsken");

    /// <summary>The theme's wallpaper, or null when this isn't a Norrsken OS install (no art on disk).</summary>
    public static string? WallpaperFor(string theme)
    {
        // Each theme has its own wallpaper; older installs only have graphite/paper/ink.
        string name = Theme.Normalize(theme) is "high-contrast" ? "ink" : Theme.Normalize(theme);
        foreach (var n in new[] { name, Theme.IsLight(name) ? "paper" : name is "ink" ? "ink" : "graphite" })
        {
            string file = Path.Combine(WallpaperDir, $"norrsken-{n}.png");
            if (File.Exists(file)) return file;
        }
        return null;
    }

    public static async Task ApplyAsync(string theme)
    {
        if (!App.Settings.MatchWallpaper || WallpaperFor(theme) is not { } file) return;
        if (App.TestMode) { TestLog.Add($"wallpaper and lock screen → {Path.GetFileName(file)}"); return; }
        SystemParametersInfoW(SPI_SETDESKWALLPAPER, 0, file, SPIF_UPDATEINIFILE | SPIF_SENDCHANGE);
        try
        {
            // The sign-in screen shows the lock screen picture (blurred), so this covers both.
            if (UserProfilePersonalizationSettings.IsSupported())
            {
                var image = await StorageFile.GetFileFromPathAsync(file);
                await UserProfilePersonalizationSettings.Current.TrySetLockScreenImageAsync(image);
            }
        }
        catch (Exception ex) { Log.Write("Lock screen image not set: " + ex.Message); }
    }

    static int Run(string exe, string args)
    {
        using var p = Process.Start(new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, exe), args)
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true })!;
        p.StandardOutput.ReadToEnd();
        p.StandardError.ReadToEnd();
        p.WaitForExit();
        return p.ExitCode;
    }

    /// <summary>Names the running system's boot entry "Norrsken OS" (Windows Boot Manager). Needs admin.</summary>
    public static (InstallState, string) NameBootEntry()
    {
        try
        {
            int code = Run("bcdedit.exe", "/set {current} description \"Norrsken OS\"");
            return code == 0 ? (InstallState.Done, "Boot menu shows “Norrsken OS”") : (InstallState.Failed, $"bcdedit exited with {code}");
        }
        catch (Exception ex) { return (InstallState.Failed, ex.Message); }
    }

    /// <summary>
    /// First-sign-in admin step on Norrsken OS ("Norrsken system setup"): boot menu name, the
    /// "Norrsken OS" identity in Settings › About, and autostart moved from the all-users Startup
    /// shortcut to a registry Run entry (starts sooner). The shortcut is removed so it never starts twice.
    /// </summary>
    public static (InstallState, string) ApplySystemIdentity()
    {
        try
        {
            var (boot, bootDetail) = NameBootEntry();
            try { PackageIdentity.TrustCertificate(); } catch (Exception ex) { Log.Write("Identity certificate not trusted: " + ex.Message); }
            const string oem = @"HKLM\Software\Microsoft\Windows\CurrentVersion\OEMInformation";
            int failures = 0;
            failures += Run("reg.exe", $"add \"{oem}\" /v Manufacturer /t REG_SZ /d \"Norrsken\" /f") == 0 ? 0 : 1;
            failures += Run("reg.exe", $"add \"{oem}\" /v Model /t REG_SZ /d \"Norrsken OS {App.Version}\" /f") == 0 ? 0 : 1;
            string exe = Environment.ProcessPath!;
            if (SystemIntegration.Install(exe).Item1 != InstallState.Done) failures++;
            if (failures == 0)
            {
                string lnk = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup), "Norrsken.lnk");
                if (File.Exists(lnk)) File.Delete(lnk);
            }
            if (boot != InstallState.Done || failures > 0)
                return (InstallState.Failed, boot != InstallState.Done ? bootDetail : $"{failures} registry setting(s) couldn't be written");
            return (InstallState.Done, "Boot menu, About page and autostart set to Norrsken OS");
        }
        catch (Exception ex) { return (InstallState.Failed, ex.Message); }
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern IntPtr SendMessageTimeoutW(IntPtr hwnd, uint msg, IntPtr w, string l, uint flags, uint timeout, out IntPtr result);

    /// <summary>
    /// Norrsken OS, first sign-in of each account: Windows dark/light mode to match the theme, file
    /// extensions shown, and (via ApplyAsync) the wallpaper and lock screen. Runs once per account.
    /// </summary>
    public static async Task ApplyUserLookOnceAsync()
    {
        var s = App.Settings;
        if (!s.ApplyUserLook || s.UserLookApplied) return;
        int light = Theme.CurrentIsLight ? 1 : 0;
        if (App.TestMode) TestLog.Add($"user look → light={light}, file extensions shown");
        else
        {
            using (var personalize = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
            {
                personalize.SetValue("AppsUseLightTheme", light, Microsoft.Win32.RegistryValueKind.DWord);
                personalize.SetValue("SystemUsesLightTheme", light, Microsoft.Win32.RegistryValueKind.DWord);
            }
            // Windows normally holds startup apps back for a few seconds after sign-in; Norrsken wants none of that.
            using (var serialize = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\Serialize"))
                serialize.SetValue("StartupDelayInMSec", 0, Microsoft.Win32.RegistryValueKind.DWord);
            using (var advanced = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced"))
                advanced.SetValue("HideFileExt", 0, Microsoft.Win32.RegistryValueKind.DWord);
            // Tell Explorer and open apps to pick up the new colour mode.
            SendMessageTimeoutW((IntPtr)0xFFFF, 0x001A /* WM_SETTINGCHANGE */, IntPtr.Zero, "ImmersiveColorSet", 0x0002, 1000, out _);
        }
        await ApplyAsync(Theme.Current);
        s.UserLookApplied = true;
        s.Save();
    }
}
