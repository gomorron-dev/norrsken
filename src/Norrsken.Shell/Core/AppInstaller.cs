using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Windows;

namespace Norrsken.Shell.Core;

public enum InstallState { Queued, Installing, Done, Failed }

public sealed class InstallItem(CatalogApp app) : INotifyPropertyChanged
{
    public CatalogApp App { get; } = app;
    public string Name => App.Name;

    InstallState _state;
    public InstallState State { get => _state; set => Set(ref _state, value); }

    string _detail = "";
    public string Detail { get => _detail; set => Set(ref _detail, value); }

    public event PropertyChangedEventHandler? PropertyChanged;

    void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}

/// <summary>
/// Installs catalogue apps with winget, one at a time (installers fight over the MSI lock otherwise).
/// Machine-wide apps run in one elevated helper process (a single admin prompt for all of them);
/// per-user apps run unelevated, because some (Spotify) refuse to install as administrator.
/// </summary>
public sealed class AppInstaller
{
    // winget "already installed / no applicable upgrade" results count as success.
    static readonly HashSet<int> AlreadyThere = [unchecked((int)0x8A15002B), unchecked((int)0x8A150061)];

    public ObservableCollection<InstallItem> Items { get; } = new();

    /// <summary>UI tests: walk through the real state machine without running any installer.</summary>
    public bool Simulate { get; init; }

    public bool Running { get; private set; }
    public int DoneCount => Items.Count(i => i.State == InstallState.Done);
    public int FailedCount => Items.Count(i => i.State == InstallState.Failed);

    public event Action? Finished;

    // No --scope: forcing one fails for packages that only ship the other installer type.
    // Elevation is decided by which process runs the install (see RunAsync).
    public static string WingetArgs(CatalogApp app) =>
        $"install --id {app.Id} -e --source winget --silent --disable-interactivity " +
        "--accept-package-agreements --accept-source-agreements";

    public async Task RunAsync(IEnumerable<CatalogApp> apps)
    {
        Running = true;
        Items.Clear();
        foreach (var a in AppCatalog.Essentials.Concat(apps).DistinctBy(a => a.Id)) Items.Add(new InstallItem(a));

        if (!Simulate && !await EnsureWingetAsync())
        {
            foreach (var i in Items) { i.State = InstallState.Failed; i.Detail = "The Windows package manager isn't ready yet — try again from Settings in a few minutes."; }
        }
        else
        {
            var machine = Items.Where(i => i.App.Scope == InstallScope.Machine).ToList();
            var user = Items.Where(i => i.App.Scope == InstallScope.User).ToList();
            if (machine.Count > 0) await RunElevatedAsync(machine);
            foreach (var item in user) await RunOneAsync(item);
        }
        Running = false;
        Finished?.Invoke();
    }

    /// <summary>Retry only what failed.</summary>
    public async Task RetryFailedAsync()
    {
        var failed = Items.Where(i => i.State == InstallState.Failed).ToList();
        if (failed.Count == 0) return;
        Running = true;
        foreach (var i in failed) { i.State = InstallState.Queued; i.Detail = ""; }
        var machine = failed.Where(i => i.App.Scope == InstallScope.Machine).ToList();
        if (machine.Count > 0) await RunElevatedAsync(machine);
        foreach (var item in failed.Where(i => i.App.Scope == InstallScope.User)) await RunOneAsync(item);
        Running = false;
        Finished?.Invoke();
    }

    async Task RunOneAsync(InstallItem item)
    {
        item.State = InstallState.Installing;
        if (Simulate)
        {
            await Task.Delay(120);
            item.State = InstallState.Done;
            return;
        }
        var (state, detail) = await RunWingetAsync(item.App);
        item.State = state;
        item.Detail = detail;
    }

    static async Task<(InstallState, string)> RunWingetAsync(CatalogApp app)
    {
        if (app.Id == AppCatalog.BootMenuId) return SystemLook.ApplySystemIdentity(); // runs in the elevated helper
        if (app.Id == AppCatalog.FlickerboxId) return await InstallFlickerboxAsync();
        try
        {
            using var p = Process.Start(new ProcessStartInfo("winget", WingetArgs(app))
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
            })!;
            _ = p.StandardOutput.ReadToEndAsync();
            _ = p.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(40));
            await p.WaitForExitAsync(timeout.Token);
            return p.ExitCode == 0 ? (InstallState.Done, "")
                : AlreadyThere.Contains(p.ExitCode) ? (InstallState.Done, "Already installed")
                : (InstallState.Failed, $"Installer reported error 0x{p.ExitCode:X8}");
        }
        catch (OperationCanceledException) { return (InstallState.Failed, "Timed out after 40 minutes"); }
        catch (Exception ex) { return (InstallState.Failed, ex.Message); }
    }

    /// <summary>One admin prompt, then the elevated helper reports progress through a small log file.</summary>
    async Task RunElevatedAsync(List<InstallItem> items)
    {
        if (Simulate)
        {
            foreach (var i in items) await RunOneAsync(i);
            return;
        }
        string log = Elevated.NewLogPath("install");
        string list = Path.ChangeExtension(log, ".json");
        File.WriteAllText(list, JsonSerializer.Serialize(items.Select(i => i.App.Id)));
        await Elevated.RunAsync($"--install-apps \"{list}\" \"{log}\"", log, items);
    }

    public static string FlickerboxExe => MediaDefaults.Exe;

    /// <summary>Flickerbox ships inside Norrsken (`apps\Flickerbox`); Norrsken installs and registers it itself.</summary>
    static Task<(InstallState, string)> InstallFlickerboxAsync() => Task.Run(MediaDefaults.Apply);
    /// <summary>Elevated helper body (`--install-apps list log`): install each id, append one JSON line per change.</summary>
    public static int RunHelper(string listFile, string logFile)
    {
        var ids = JsonSerializer.Deserialize<List<string>>(File.ReadAllText(listFile)) ?? [];
        void Write(string id, InstallState s, string? d = null) => Elevated.Report(logFile, id, s, d);
        foreach (var id in ids)
        {
            if (AppCatalog.Find(id) is not { } app) { Write(id, InstallState.Failed, "Not in the Norrsken catalogue"); continue; }
            Write(id, InstallState.Installing);
            var (state, detail) = RunWingetAsync(app).GetAwaiter().GetResult();
            Write(id, state, detail);
        }
        return 0;
    }

    /// <summary>On a fresh install winget can need registering once; Microsoft's documented fix.</summary>
    static async Task<bool> EnsureWingetAsync()
    {
        for (int attempt = 0; attempt < 4; attempt++)
        {
            if (await ExitCodeAsync("winget", "--version") == 0) return true;
            if (attempt == 0)
                await ExitCodeAsync("powershell", "-NoProfile -Command \"Add-AppxPackage -RegisterByFamilyName -MainPackage Microsoft.DesktopAppInstaller_8wekyb3d8bbwe\"");
            await Task.Delay(TimeSpan.FromSeconds(8));
        }
        return false;
    }

    static async Task<int> ExitCodeAsync(string file, string args)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo(file, args) { UseShellExecute = false, CreateNoWindow = true })!;
            await p.WaitForExitAsync();
            return p.ExitCode;
        }
        catch (Exception) { return -1; }
    }
}
