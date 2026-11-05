using System.Diagnostics;
using Microsoft.Win32;

namespace Norrsken.Shell.Core;

/// <summary>
/// How Norrsken plugs into Windows, and how it unplugs again (all admin-only, all reversible):
/// <list type="bullet">
/// <item>a scheduled task that starts Norrsken elevated the instant anyone signs in (no UAC prompt,
///   before Windows' delayed startup apps);</item>
/// <item>"Image File Execution Options" redirects, the documented hook tools like Process Explorer use to
///   replace Task Manager: when anything starts Task Manager, Notepad, Snipping Tool, Windows Terminal or
///   Windows Settings, Norrsken's own app opens instead;</item>
/// <item>an entry in Apps so Norrsken can be uninstalled like any program.</item>
/// </list>
/// </summary>
public static class SystemIntegration
{
    public const string TaskName = "Norrsken";
    const string Ifeo = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Image File Execution Options\";
    const string UninstallKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Norrsken";

    /// <summary>Windows program → the Norrsken app that replaces it.</summary>
    public static readonly (string Exe, string App)[] Redirects =
    [
        ("taskmgr.exe", "taskmgr"), ("notepad.exe", "notes"), ("SnippingTool.exe", "capture"),
        ("WindowsTerminal.exe", "terminal"), ("SystemSettings.exe", "settings"),
    ];

    static int Run(string exe, string args)
    {
        using var p = Process.Start(new ProcessStartInfo(exe, args) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true })!;
        p.StandardOutput.ReadToEnd();
        p.StandardError.ReadToEnd();
        p.WaitForExit(15000);
        return p.ExitCode;
    }

    /// <summary>`--integrate` (admin/SYSTEM): everything above for the Norrsken at <paramref name="exe"/>.</summary>
    public static (InstallState, string) Install(string exe)
    {
        var problems = new List<string>();
        try { CreateTask(exe); } catch (Exception ex) { problems.Add("startup task: " + ex.Message); }
        try
        {
            // The older ways of starting (Run key, Startup shortcut) would start a second copy.
            using (var run = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", true)) run?.DeleteValue("Norrsken", false);
            string lnk = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup), "Norrsken.lnk");
            if (File.Exists(lnk)) File.Delete(lnk);
        }
        catch (Exception ex) { problems.Add("old autostart: " + ex.Message); }
        try { SetRedirects(exe, true); } catch (Exception ex) { problems.Add("app redirects: " + ex.Message); }
        try
        {
            using var u = Registry.LocalMachine.CreateSubKey(UninstallKey);
            u.SetValue("DisplayName", "Norrsken");
            u.SetValue("Publisher", "Norrsken");
            u.SetValue("DisplayVersion", App.Version);
            u.SetValue("DisplayIcon", exe + ",0");
            u.SetValue("InstallLocation", Path.GetDirectoryName(exe)!);
            u.SetValue("UninstallString", $"\"{exe}\" --uninstall");
            u.SetValue("NoModify", 1, RegistryValueKind.DWord);
            u.SetValue("NoRepair", 1, RegistryValueKind.DWord);
        }
        catch (Exception ex) { problems.Add("uninstall entry: " + ex.Message); }
        try { PackageIdentity.TrustCertificate(); } catch (Exception ex) { problems.Add("notifications: " + ex.Message); }
        return problems.Count == 0
            ? (InstallState.Done, "Starts instantly at sign-in, elevated; Windows' tools open Norrsken's")
            : (InstallState.Failed, string.Join("; ", problems));
    }

    /// <summary>`--integration-selftest` (admin): proves the task XML and a redirect work, leaving nothing behind.</summary>
    public static int SelfTest(string report)
    {
        var lines = new List<string>();
        bool ok = true;
        try
        {
            CreateTask(Environment.ProcessPath!, "Norrsken-selftest");
            bool exists = Run("schtasks.exe", "/Query /TN \"Norrsken-selftest\"") == 0;
            lines.Add($"startup task created and found by Windows: {exists}");
            ok &= exists;
        }
        catch (Exception ex) { lines.Add("startup task failed: " + ex.Message); ok = false; }
        finally { Run("schtasks.exe", "/Delete /TN \"Norrsken-selftest\" /F"); lines.Add("test task deleted: " + (Run("schtasks.exe", "/Query /TN \"Norrsken-selftest\"") != 0)); }
        try
        {
            using (var k = Registry.LocalMachine.CreateSubKey(Ifeo + "norrsken-selftest.exe")) k.SetValue("Debugger", $"\"{Environment.ProcessPath}\" --ifeo notes");
            using (var k = Registry.LocalMachine.OpenSubKey(Ifeo + "norrsken-selftest.exe")) lines.Add("redirect written: " + (k?.GetValue("Debugger") is string));
        }
        catch (Exception ex) { lines.Add("redirect failed: " + ex.Message); ok = false; }
        finally { Registry.LocalMachine.DeleteSubKeyTree(Ifeo + "norrsken-selftest.exe", false); lines.Add("test redirect removed: " + (Registry.LocalMachine.OpenSubKey(Ifeo + "norrsken-selftest.exe") == null)); }
        File.WriteAllLines(report, lines);
        return ok ? 0 : 1;
    }

    static void CreateTask(string exe) => CreateTask(exe, TaskName);

    static void CreateTask(string exe, string name)
    {
        string xml = $"""
            <?xml version="1.0" encoding="UTF-16"?>
            <Task version="1.4" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
              <RegistrationInfo><Description>Starts the Norrsken desktop when you sign in.</Description></RegistrationInfo>
              <Triggers><LogonTrigger><Enabled>true</Enabled></LogonTrigger></Triggers>
              <Principals><Principal id="Users"><GroupId>S-1-5-32-545</GroupId><RunLevel>HighestAvailable</RunLevel></Principal></Principals>
              <Settings>
                <MultipleInstancesPolicy>Parallel</MultipleInstancesPolicy>
                <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
                <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
                <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
                <AllowHardTerminate>false</AllowHardTerminate>
                <Priority>4</Priority>
              </Settings>
              <Actions Context="Users"><Exec><Command>"{exe}"</Command></Exec></Actions>
            </Task>
            """;
        string file = Path.Combine(Path.GetTempPath(), "norrsken-task.xml");
        File.WriteAllText(file, xml, System.Text.Encoding.Unicode);
        int rc = Run("schtasks.exe", $"/Create /TN \"{name}\" /XML \"{file}\" /F");
        File.Delete(file);
        if (rc != 0) throw new InvalidOperationException($"schtasks exited with {rc}");
    }

    public static bool TaskExists() { try { return Run("schtasks.exe", $"/Query /TN \"{TaskName}\"") == 0; } catch (Exception) { return false; } }

    public static void SetRedirects(string exe, bool on)
    {
        foreach (var (image, app) in Redirects)
        {
            if (on)
            {
                using var k = Registry.LocalMachine.CreateSubKey(Ifeo + image);
                k.SetValue("Debugger", $"\"{exe}\" --ifeo {app}");
            }
            else
            {
                using var k = Registry.LocalMachine.OpenSubKey(Ifeo + image, true);
                if (k?.GetValue("Debugger") is string d && d.Contains("--ifeo", StringComparison.OrdinalIgnoreCase)) k.DeleteValue("Debugger");
            }
        }
    }

    /// <summary>
    /// `--uninstall` (admin): takes Norrsken out of Windows completely. The desktop goes back to the
    /// Windows taskbar and Start; your files, apps and Flickerbox stay.
    /// </summary>
    public static int Uninstall()
    {
        string dir = Path.GetDirectoryName(Environment.ProcessPath!)!;
        void Try(Action a) { try { a(); } catch (Exception ex) { Log.Write("Uninstall: " + ex.Message); } }

        Try(() => { if (EventWaitHandle.TryOpenExisting(@"Local\Norrsken.Shell.Quit", out var quit)) { quit.Set(); Thread.Sleep(2500); } });
        Try(() => { foreach (var p in Process.GetProcessesByName("Norrsken.Shell").Where(p => p.Id != Environment.ProcessId)) p.Kill(); });
        Try(TaskbarHider.Restore);
        Try(() => Run("schtasks.exe", $"/Delete /TN \"{TaskName}\" /F"));
        Try(() => SetRedirects(Environment.ProcessPath!, false));
        Try(() => { using var run = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", true); run?.DeleteValue("Norrsken", false); });
        Try(() => { using var run = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", true); run?.DeleteValue("Norrsken", false); });
        Try(() => { using var pk = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Policies\Microsoft\Windows\System", true); if ((pk?.GetValue("DefaultAssociationsConfiguration") as string)?.Contains("Norrsken") == true) pk!.DeleteValue("DefaultAssociationsConfiguration"); });
        Try(() => { using var oem = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\OEMInformation", true); if ((oem?.GetValue("Manufacturer") as string) == "Norrsken") { oem!.DeleteValue("Manufacturer", false); oem.DeleteValue("Model", false); oem.DeleteValue("Logo", false); } });
        Try(() => Run("bcdedit.exe", "/set {current} description \"Windows 11\""));
        Try(() => Registry.LocalMachine.DeleteSubKeyTree(UninstallKey, false));
        // This account's own changes.
        Try(() => AppRetirement.UnregisterHandlers());
        Try(() => { App.Settings.CustomCursors = false; NorrskenCursors.Restore(); });
        Try(() => { foreach (var a in NorrskenApps.All) { string l = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), a.Name + ".lnk"); if (File.Exists(l)) File.Delete(l); } });
        Try(() =>
        {
            var pm = new Windows.Management.Deployment.PackageManager();
            foreach (var p in pm.FindPackagesForUser("", "Norrsken.Shell_" + PublisherHash())) pm.RemovePackageAsync(p.Id.FullName).AsTask().Wait(10000);
        });
        // Finally the program folder itself, once this process has exited.
        if (dir.Contains(@"\Program Files", StringComparison.OrdinalIgnoreCase) && dir.EndsWith("Norrsken", StringComparison.OrdinalIgnoreCase))
            Try(() => Process.Start(new ProcessStartInfo("cmd.exe", $"/c timeout /t 3 /nobreak >nul & rmdir /s /q \"{dir}\"") { UseShellExecute = false, CreateNoWindow = true }));
        Log.Write("Norrsken uninstalled");
        return 0;
    }

    /// <summary>The package family suffix for publisher "CN=Norrsken" (computed by Windows; looked up rather than hard-coded).</summary>
    static string PublisherHash()
    {
        try
        {
            var p = new Windows.Management.Deployment.PackageManager().FindPackagesForUser("").FirstOrDefault(x => x.Id.Name == "Norrsken.Shell");
            return p?.Id.PublisherId ?? "";
        }
        catch (Exception) { return ""; }
    }
}
