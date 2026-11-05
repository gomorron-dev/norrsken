using System.Diagnostics;
using Microsoft.Win32;
using Norrsken.Shell.Ui;

namespace Norrsken.Shell.Core;

/// <summary>
/// Norrsken's screenshot tool on its own, for a normal Windows PC without the rest of Norrsken.
/// `--install-capture` copies Norrsken to %LOCALAPPDATA%\Programs\Norrsken Capture and starts it at sign-in
/// as `--capture-agent`: a keyboard hook that takes PrtScn, Win+Shift+S, Win+PrtScn and Alt+PrtScn and
/// nothing else. `--uninstall-capture` (also in Settings › Apps) puts Windows' own tool back.
/// </summary>
public static class CaptureAgent
{
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string RunName = "Norrsken Capture";
    const string StateKey = @"Software\Norrsken\Capture";
    const string KeyboardKey = @"Control Panel\Keyboard";
    const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\NorrskenCapture";
    public const string MutexName = @"Local\Norrsken.Capture";

    public static string Dir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Norrsken Capture");
    public static string Exe => Path.Combine(Dir, "Norrsken.Shell.exe");

    /// <summary>The agent itself: no windows until a shortcut is pressed.</summary>
    public static bool Run(App app)
    {
        var mutex = new Mutex(true, MutexName, out bool created);
        if (!created) return false;
        GC.KeepAlive(mutex);
        KeyboardHook.CaptureOnly = true;
        KeyboardHook.Pressed += action =>
        {
            try
            {
                switch (action)
                {
                    case "capture": CaptureOverlay.Start(); break;
                    case "capture-screen": CaptureOverlay.SaveWholeScreen(); break;
                    case "capture-window": CaptureOverlay.CaptureActiveWindow(); break;
                }
            }
            catch (Exception ex) { Log.Write("Capture failed: " + ex.Message); }
        };
        KeyboardHook.Start();
        NorrskenApps.FollowThemeChanges(); // sounds switched on in Settings reach the capture tool too
        var quit = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\Norrsken.Capture.Quit");
        new Thread(() => { quit.WaitOne(); app.Dispatcher.BeginInvoke(() => { KeyboardHook.Stop(); app.Shutdown(); }); }) { IsBackground = true }.Start();
        Log.Write("Norrsken Capture running: PrtScn, Win+Shift+S, Win+PrtScn, Alt+PrtScn");
        return true;
    }

    public static bool Installed => File.Exists(Exe) && Registry.CurrentUser.OpenSubKey(RunKey)?.GetValue(RunName) != null;

    /// <summary>Copies this Norrsken (without the bundled apps) in, takes over the keys, starts the agent.</summary>
    public static string Install()
    {
        Stop();
        string source = AppContext.BaseDirectory.TrimEnd('\\');
        if (!string.Equals(source, Dir, StringComparison.OrdinalIgnoreCase))
        {
            Directory.CreateDirectory(Dir);
            using var rc = Process.Start(new ProcessStartInfo("robocopy", $"\"{source}\" \"{Dir}\" /MIR /XD apps identity /R:1 /W:1 /NP /NFL /NDL /NJH /NJS")
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true })!;
            rc.StandardOutput.ReadToEnd();
            rc.WaitForExit();
            if (rc.ExitCode >= 8 || !File.Exists(Exe)) throw new IOException($"Copying failed (robocopy {rc.ExitCode}).");
        }

        using (var state = Registry.CurrentUser.CreateSubKey(StateKey))
        using (var kb = Registry.CurrentUser.CreateSubKey(KeyboardKey))
        {
            // Remember Windows' own PrtScn setting once, so uninstalling restores exactly that.
            if (state.GetValue("PrintScreenWas") == null)
                state.SetValue("PrintScreenWas", kb.GetValue("PrintScreenKeyForSnippingEnabled")?.ToString() ?? "absent");
            kb.SetValue("PrintScreenKeyForSnippingEnabled", 0, RegistryValueKind.DWord);
        }
        using (var run = Registry.CurrentUser.CreateSubKey(RunKey)) run.SetValue(RunName, $"\"{Exe}\" --capture-agent");
        using (var un = Registry.CurrentUser.CreateSubKey(UninstallKey))
        {
            un.SetValue("DisplayName", "Norrsken Capture");
            un.SetValue("Publisher", "Norrsken");
            un.SetValue("DisplayVersion", App.Version);
            un.SetValue("DisplayIcon", Exe + ",0");
            un.SetValue("InstallLocation", Dir);
            un.SetValue("UninstallString", $"\"{Exe}\" --uninstall-capture");
            un.SetValue("NoModify", 1, RegistryValueKind.DWord);
            un.SetValue("NoRepair", 1, RegistryValueKind.DWord);
        }
        Process.Start(new ProcessStartInfo(Exe, "--capture-agent") { UseShellExecute = false, WorkingDirectory = Dir });
        return Dir;
    }

    /// <summary>Everything back as it was: Windows' PrtScn setting, no autostart, no files.</summary>
    public static void Uninstall()
    {
        Stop();
        using (var run = Registry.CurrentUser.CreateSubKey(RunKey)) run.DeleteValue(RunName, false);
        using (var state = Registry.CurrentUser.OpenSubKey(StateKey))
        using (var kb = Registry.CurrentUser.CreateSubKey(KeyboardKey))
        {
            string was = state?.GetValue("PrintScreenWas") as string ?? "absent";
            if (was == "absent") kb.DeleteValue("PrintScreenKeyForSnippingEnabled", false);
            else if (int.TryParse(was, out int v)) kb.SetValue("PrintScreenKeyForSnippingEnabled", v, RegistryValueKind.DWord);
        }
        Registry.CurrentUser.DeleteSubKeyTree(StateKey, false);
        Registry.CurrentUser.DeleteSubKeyTree(UninstallKey, false);
        // The running exe can't delete its own folder: a detached cmd does it once this process has gone.
        if (Directory.Exists(Dir))
        {
            string cmd = $"/c ping 127.0.0.1 -n 3 >nul & rmdir /s /q \"{Dir}\"";
            Process.Start(new ProcessStartInfo("cmd.exe", cmd) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = Path.GetTempPath() });
        }
    }

    static void Stop()
    {
        if (EventWaitHandle.TryOpenExisting(@"Local\Norrsken.Capture.Quit", out var quit)) { quit.Set(); Thread.Sleep(600); }
        foreach (var p in Process.GetProcessesByName("Norrsken.Shell"))
        {
            try
            {
                if (p.Id != Environment.ProcessId && Interop.Native.GetProcessPath((uint)p.Id)?.StartsWith(Dir, StringComparison.OrdinalIgnoreCase) == true)
                { p.Kill(); p.WaitForExit(3000); }
            }
            catch (Exception) { }
        }
    }
}
