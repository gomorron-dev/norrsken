using System.Diagnostics;
using System.Text.Json;
using Norrsken.Shell.Interop;

namespace Norrsken.Shell.Core;

/// <summary>
/// "Install Norrsken on this PC" from the USB stick, inside the existing Windows: nothing is erased.
/// Files, apps, settings and activation stay; Norrsken is copied in and becomes the desktop.
/// Optionally Windows Setup then refreshes Windows itself while keeping files and apps.
/// </summary>
public static class InPlaceSetup
{
    public sealed record Plan(string Source, string Assets, string Target, string WallpaperDir, string UserSettings,
        bool RefreshWindows, string IsoRoot, bool Test, bool Reinstall = false);

    public static readonly (string Id, string Label)[] Steps =
    [
        ("stop", "Remove the Norrsken that's installed now"),
        ("copy", "Copy Norrsken"),
        ("autostart", "Start instantly at sign-in; Windows tools open Norrsken's"),
        ("identity", "Wallpapers and Norrsken identity"),
        ("settings", "Your Norrsken settings"),
        ("defaults", "Flickerbox and Notes as default apps"),
        ("bootmenu", "Boot menu: Norrsken OS"),
        ("refresh", "Refresh Windows (keeps files and apps)"),
    ];

    static string? Env(string name) => Environment.GetEnvironmentVariable(name) is { Length: > 0 } v ? v : null;

    /// <summary>Running from <c>USB:\Norrsken\Norrsken.Shell.exe</c>; the stick's root holds Windows Setup.</summary>
    public static bool AlreadyInstalled => File.Exists(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Norrsken", "Norrsken.Shell.exe"));

    public static Plan MakePlan(bool refresh, bool reinstall = false)
    {
        string source = AppContext.BaseDirectory.TrimEnd('\\');
        string isoRoot = Env("NORRSKEN_ISO_ROOT") ?? Path.GetDirectoryName(source)!;
        string target = Env("NORRSKEN_INSTALL_TARGET")
                        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Norrsken");
        string wallpapers = App.TestMode ? Path.Combine(target, "Wallpapers") : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), @"Web\Wallpaper\Norrsken");
        string settings = App.TestMode ? Path.Combine(target, "user-settings.json")
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Norrsken", "settings.json");
        string assets = Env("NORRSKEN_ASSETS") ?? Path.Combine(source, "assets");
        return new(source, assets, target, wallpapers, settings, refresh, isoRoot, App.TestMode, reinstall);
    }

    public static string SetupExe(Plan p) => Path.Combine(p.IsoRoot, "setup.exe");

    /// <summary>Windows Setup switches for an unattended in-place upgrade that keeps files and apps.</summary>
    public const string RefreshArgs = "/auto upgrade /eula accept /dynamicupdate enable";

    /// <summary>Elevated helper body (`--setup-apply plan log`).</summary>
    public static int RunHelper(string planFile, string log)
    {
        var p = JsonSerializer.Deserialize<Plan>(File.ReadAllText(planFile))!;
        void Step(string id, Func<string> work)
        {
            Elevated.Report(log, id, InstallState.Installing);
            try { Elevated.Report(log, id, InstallState.Done, work()); }
            catch (Exception ex) { Elevated.Report(log, id, InstallState.Failed, ex.Message); }
        }
        // In tests, machine-wide registry writes are recorded instead of applied.
        void Reg(params string[] args)
        {
            if (p.Test) { File.AppendAllText(Path.Combine(p.Target, "registry.txt"), "reg " + string.Join(' ', args) + Environment.NewLine); return; }
            using var r = Process.Start(new ProcessStartInfo("reg.exe", string.Join(' ', args)) { UseShellExecute = false, CreateNoWindow = true })!;
            r.WaitForExit();
            if (r.ExitCode != 0) throw new InvalidOperationException($"reg {args[0]} failed ({r.ExitCode})");
        }

        if (p.Reinstall)
        {
            Step("stop", () =>
            {
                if (p.Test) { File.WriteAllText(Path.Combine(p.Target, "stopped.txt"), "quit"); return "Recorded (test)"; }
                // Ask the running dock to quit (it puts the Windows taskbar back), then make sure nothing is left.
                if (EventWaitHandle.TryOpenExisting(@"Local\Norrsken.Shell.Quit", out var quit)) { quit.Set(); Thread.Sleep(2500); }
                foreach (var proc in Process.GetProcessesByName("Norrsken.Shell"))
                    try { if (Native.GetProcessPath((uint)proc.Id)?.StartsWith(p.Target, StringComparison.OrdinalIgnoreCase) == true && proc.Id != Environment.ProcessId) proc.Kill(); } catch (Exception) { }
                return "The old version is stopped; its files are replaced next";
            });
        }
        Step("copy", () =>
        {
            // A reinstall mirrors the folder: files the new version doesn't have are removed.
            using var rc = Process.Start(new ProcessStartInfo("robocopy", $"\"{p.Source}\" \"{p.Target}\" {(p.Reinstall ? "/MIR" : "/E")} /R:1 /W:1 /NP /NFL /NDL /NJH /NJS")
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true })!;
            rc.StandardOutput.ReadToEnd();
            rc.WaitForExit();
            if (rc.ExitCode >= 8 || !File.Exists(Path.Combine(p.Target, "Norrsken.Shell.exe")))
                throw new IOException($"Copying failed (robocopy {rc.ExitCode}).");
            // Last two folders are enough to recognise where it went ("Program Files\Norrsken").
            var parts = p.Target.TrimEnd('\\').Split('\\');
            return "Into " + string.Join('\\', parts.TakeLast(2));
        });
        string exe = Path.Combine(p.Target, "Norrsken.Shell.exe");
        Step("autostart", () =>
        {
            if (p.Test) { File.WriteAllText(Path.Combine(p.Target, "integrate.txt"), $"\"{exe}\" --integrate"); return "Recorded (test)"; }
            using var i = Process.Start(new ProcessStartInfo(exe, "--integrate") { UseShellExecute = false, CreateNoWindow = true })!;
            i.WaitForExit();
            if (i.ExitCode != 0) throw new InvalidOperationException("Part of the integration failed (see the Norrsken log).");
            return "Starts instantly for every account; Task Manager, Notepad and Settings open Norrsken's";
        });
        Step("identity", () =>
        {
            Directory.CreateDirectory(p.WallpaperDir);
            foreach (var f in Directory.EnumerateFiles(p.Assets, "norrsken-*.png")) File.Copy(f, Path.Combine(p.WallpaperDir, Path.GetFileName(f)), true);
            const string oem = @"HKLM\Software\Microsoft\Windows\CurrentVersion\OEMInformation";
            Reg("add", oem, "/v", "Manufacturer", "/t", "REG_SZ", "/d", "Norrsken", "/f");
            Reg("add", oem, "/v", "Model", "/t", "REG_SZ", "/d", $"\"Norrsken OS {App.Version}\"", "/f");
            string cer = Path.Combine(p.Target, "identity", "norrsken.cer");
            if (File.Exists(cer)) { if (p.Test) File.WriteAllText(Path.Combine(p.Target, "certutil.txt"), cer); else { using var cu = Process.Start(new ProcessStartInfo("certutil.exe", $"-addstore TrustedPeople \"{cer}\"") { UseShellExecute = false, CreateNoWindow = true })!; cu.WaitForExit(); } }
            return "Wallpapers added; About shows Norrsken OS";
        });
        Step("settings", () =>
        {
            if (File.Exists(p.UserSettings)) return "Kept your existing Norrsken settings";
            Directory.CreateDirectory(Path.GetDirectoryName(p.UserSettings)!);
            File.WriteAllText(p.UserSettings, "{ \"Theme\": \"graphite\", \"HideWindowsTaskbar\": true, \"MatchWallpaper\": true, \"ApplyUserLook\": true, \"RetireReplacedApps\": true, \"WelcomeDone\": false }");
            return "Dock-first, Windows taskbar hidden";
        });
        Step("defaults", () =>
        {
            if (p.Test) { File.WriteAllText(Path.Combine(p.Target, "defaults.txt"), $"\"{exe}\" --register-defaults"); return "Recorded (test)"; }
            using var d = Process.Start(new ProcessStartInfo(exe, "--register-defaults") { UseShellExecute = false, CreateNoWindow = true })!;
            d.WaitForExit();
            if (d.ExitCode != 0) throw new InvalidOperationException("Registering the default apps failed (see the Norrsken log).");
            return "Photos, video and music in Flickerbox, text in Notes";
        });
        Step("bootmenu", () =>
        {
            if (p.Test) { File.WriteAllText(Path.Combine(p.Target, "bcdedit.txt"), "bcdedit /set {current} description \"Norrsken OS\""); return "Recorded (test)"; }
            var (state, detail) = SystemLook.NameBootEntry();
            return state == InstallState.Done ? detail : throw new InvalidOperationException(detail);
        });
        if (p.RefreshWindows)
        {
            Step("refresh", () =>
            {
                string setup = SetupExe(p);
                if (!File.Exists(setup)) throw new FileNotFoundException("Windows Setup wasn't found next to Norrsken on this stick.", setup);
                if (p.Test) { File.WriteAllText(Path.Combine(p.Target, "setup-command.txt"), $"\"{setup}\" {RefreshArgs}"); return "Windows Setup would start now (test)"; }
                Process.Start(new ProcessStartInfo(setup, RefreshArgs) { UseShellExecute = true });
                return "Windows Setup started — your PC will restart a few times";
            });
        }
        return 0;
    }
}
