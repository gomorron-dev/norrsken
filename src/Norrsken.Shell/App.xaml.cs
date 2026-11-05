using System.Collections.ObjectModel;
using System.Windows;
using Norrsken.Shell.Core;
using Norrsken.Shell.Ui;

namespace Norrsken.Shell;

public partial class App : Application
{
    public static ShellSettings Settings { get; private set; } = new();
    public static WindowTracker Tracker { get; internal set; } = null!;
    public static DockModel Dock { get; internal set; } = null!;
    public static ObservableCollection<AppEntry> Apps { get; } = new();
    public static ObservableCollection<AppEntry> Pinned { get; } = new();
    public static string Version => typeof(App).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
    public static DockWindow? MainDock => Current.MainWindow as DockWindow;

    // Matched against shell:AppsFolder parsing names first, then display names, so it works on any UI language.
    static readonly string[] DefaultPins =
    [
        "Norrsken.Files", "Norrsken.Terminal", "Claude",
        "Chrome", "Steam", "Norrsken.Notes", "Norrsken.Settings",
    ];

    const string QuitEventName = @"Local\Norrsken.Shell.Quit";
    const string ShowEventName = @"Local\Norrsken.Shell.Show";
    Mutex? _singleInstance;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        FrameRate.Apply(); // before any animation exists: everything moves at the display's refresh rate
        Ui.Switches.Register();

        int st = Array.IndexOf(e.Args, "--selftest");
        if (st >= 0)
        {
            string outDir = e.Args.Length > st + 1 && !e.Args[st + 1].StartsWith("--") ? e.Args[st + 1] : "selftest-out";
            Shutdown(SelfTest.Run(outDir, e.Args.Contains("--launch-test")));
            return;
        }

        string? ArgAfter(string flag)
        {
            int i = Array.IndexOf(e.Args, flag);
            return i >= 0 && e.Args.Length > i + 1 ? e.Args[i + 1] : null;
        }

        // Interaction tests run on a private hidden desktop: never on the user's screen.
        if (ArgAfter("--uitest-isolated") is { } isolatedOut)
        {
            string full = Path.GetFullPath(isolatedOut);
            Directory.CreateDirectory(full);
            int code = Interop.IsolatedDesktop.Run($"--uitest \"{full}\"", 240_000);
            UiTest.KillLeftovers(full);
            Shutdown(code);
            return;
        }
        if (ArgAfter("--uitest") is { } uiOut)
        {
            if (Interop.IsolatedDesktop.OnUserDesktop) { Shutdown(3); return; } // refuse: only on a hidden desktop
            StartTestSession(uiOut);
            return;
        }
        if (ArgAfter("--fullscreen-probe") is { } seconds)
        {
            if (Interop.IsolatedDesktop.OnUserDesktop) { Shutdown(3); return; } // never black out the real screen
            UiTest.ShowFullscreenProbe(int.TryParse(seconds, out int s) ? s : 4);
            return;
        }

        if (ArgAfter("--render-showcase") is { } showcaseDir)
        {
            Shutdown(Showcase.Run(Path.GetFullPath(showcaseDir)));
            return;
        }
        if (ArgAfter("--render-branding") is { } brandingDir)
        {
            Shutdown(Branding.Render(Path.GetFullPath(brandingDir)));
            return;
        }

        // "Install Norrsken" from the USB stick inside an existing Windows (keeps everything).
        if (e.Args.Contains("--setup"))
        {
            Theme.Apply("graphite");
            var setup = new SetupWindow();
            setup.Closed += (_, _) => Shutdown();
            setup.Show();
            return;
        }
        int apply = Array.IndexOf(e.Args, "--setup-apply");
        if (apply >= 0 && e.Args.Length > apply + 2)
        {
            Shutdown(InPlaceSetup.RunHelper(e.Args[apply + 1], e.Args[apply + 2]));
            return;
        }

        // Elevated file-restore helper (old profiles in Windows.old belong to the previous account).
        int restore = Array.IndexOf(e.Args, "--restore-files");
        if (restore >= 0 && e.Args.Length > restore + 2)
        {
            Shutdown(WindowsOld.RunHelper(e.Args[restore + 1], e.Args[restore + 2]));
            return;
        }

        // Elevated app-install helper, started by the Welcome screen (one admin prompt for all machine-wide apps).
        int install = Array.IndexOf(e.Args, "--install-apps");
        if (install >= 0 && e.Args.Length > install + 2)
        {
            Shutdown(AppInstaller.RunHelper(e.Args[install + 1], e.Args[install + 2]));
            return;
        }

        // Live check of Norrsken title bars on the real desktop for a few seconds, then everything is removed.
        if (ArgAfter("--demo-titlebars") is { } secs && int.TryParse(secs, out int demo))
        {
            Settings = ShellSettings.Load();
            Settings.NorrskenTitleBars = true;
            Theme.Apply(Settings.Theme);
            TitleBars.Start();
            var t = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(Math.Clamp(demo, 1, 30)) };
            t.Tick += (_, _) => { t.Stop(); Log.Write($"Title bar demo: {TitleBars.Count} windows decorated"); TitleBars.Stop(); Shutdown(0); };
            t.Start();
            return;
        }
        if (ArgAfter("--integration-selftest") is { } report) { Shutdown(SystemIntegration.SelfTest(report)); return; }
        // A downloaded release installs itself over Program Files\Norrsken once the old dock has closed.
        int upd = Array.IndexOf(e.Args, "--apply-update");
        if (upd >= 0 && e.Args.Length > upd + 2 && int.TryParse(e.Args[upd + 2], out int oldPid))
        {
            Shutdown(Updater.Apply(e.Args[upd + 1], oldPid));
            return;
        }        // Norrsken Capture: the screenshot tool alone on a normal Windows PC (no dock, no other takeovers).
        if (e.Args.Contains("--capture-agent"))
        {
            Settings = ShellSettings.Load();
            Theme.Apply(Settings.Theme);
            if (!CaptureAgent.Run(this)) Shutdown();
            return;
        }
        if (e.Args.Contains("--install-capture"))
        {
            try { Log.Write("Norrsken Capture installed in " + CaptureAgent.Install()); Shutdown(0); }
            catch (Exception ex) { Log.Write("Norrsken Capture not installed: " + ex.Message); Shutdown(1); }
            return;
        }
        if (e.Args.Contains("--uninstall-capture"))
        {
            try { CaptureAgent.Uninstall(); Log.Write("Norrsken Capture uninstalled"); Shutdown(0); }
            catch (Exception ex) { Log.Write("Norrsken Capture uninstall failed: " + ex.Message); Shutdown(1); }
            return;
        }
        // Admin/SYSTEM: startup task, Windows-tool redirects, uninstall entry.
        if (e.Args.Contains("--integrate"))
        {
            var (state, detail) = SystemIntegration.Install(Environment.ProcessPath!);
            Log.Write($"Integration: {state} — {detail}");
            Shutdown(state == InstallState.Done ? 0 : 1);
            return;
        }
        // Apps › Uninstall (or `Norrsken.Shell.exe --uninstall`): takes Norrsken out of Windows.
        if (e.Args.Contains("--uninstall"))
        {
            if (!Unelevated.IsElevated)
            {
                try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Environment.ProcessPath!, "--uninstall") { UseShellExecute = true, Verb = "runas" }); } catch (Exception) { }
                Shutdown();
                return;
            }
            Settings = ShellSettings.Load();
            Theme.Apply(Settings.Theme);
            bool quiet = e.Args.Contains("--quiet");
            if (quiet || MessageBox.Show("Uninstall Norrsken? The Windows taskbar and Start come back; your files, apps and Flickerbox stay.", "Norrsken", MessageBoxButton.OKCancel, MessageBoxImage.Question) == MessageBoxResult.OK)
            {
                SystemIntegration.Uninstall();
                if (!quiet) MessageBox.Show("Norrsken is uninstalled. Sign out and back in to finish.", "Norrsken");
            }
            Shutdown();
            return;
        }
        // Image File Execution Options: something started a Windows tool Norrsken replaces.
        if (ArgAfter("--ifeo") is { } replaced)
        {
            int at = Array.IndexOf(e.Args, "--ifeo");
            // Windows passes the original program and its arguments after ours; a file to open is the first non-switch one.
            string? arg = e.Args.Skip(at + 3).FirstOrDefault(a => !a.StartsWith('/') && !a.StartsWith('-') && (File.Exists(a) || Directory.Exists(a)));
            NorrskenApps.RunApp(this, replaced, arg);
            return;
        }

        // Elevated / SYSTEM (SetupComplete, setup helpers): Flickerbox + Notes as default apps for every account.
        if (e.Args.Contains("--register-defaults"))
        {
            var (state, detail) = MediaDefaults.Apply();
            Log.Write($"Default apps: {state} — {detail}");
            Shutdown(state == InstallState.Done ? 0 : 1);
            return;
        }

        int guard = Array.IndexOf(e.Args, "--guard");
        if (guard >= 0 && e.Args.Length > guard + 1 && int.TryParse(e.Args[guard + 1], out int pid))
        {
            Shutdown(TaskbarHider.RunGuard(pid));
            return;
        }

        if (e.Args.Contains("--quit"))
        {
            // Graceful stop for scripts/uninstall: the dock removes its AppBar reservation on the way out.
            bool signalled = EventWaitHandle.TryOpenExisting(QuitEventName, out var quit) && quit.Set();
            Shutdown(signalled ? 0 : 2);
            return;
        }

        // Norrsken's own apps: Files, Notes, Screenshot, Settings.
        if (ArgAfter("--app") is { } appId)
        {
            int at = Array.IndexOf(e.Args, "--app");
            NorrskenApps.RunApp(this, appId, e.Args.Length > at + 2 ? e.Args[at + 2] : null);
            return;
        }

        // A typo'd or unsupported option must never fall through to launching the desktop.
        string[] knownForShell = ["--welcome", "--after"];
        if (e.Args.FirstOrDefault(a => a.StartsWith("--") && !knownForShell.Contains(a)) is { } unknown)
        {
            Log.Write($"Refusing to start: unknown option {unknown}");
            Shutdown(64);
            return;
        }

        // A restart (e.g. after registering the package identity): wait for the previous dock to exit.
        if (ArgAfter("--after") is { } prev && int.TryParse(prev, out int prevPid))
        {
            try { System.Diagnostics.Process.GetProcessById(prevPid).WaitForExit(10_000); } catch (ArgumentException) { }
        }

        _singleInstance = new Mutex(true, @"Local\Norrsken.Shell", out bool created);
        if (!created)
        {
            // Starting Norrsken again opens the launcher of the running instance.
            if (EventWaitHandle.TryOpenExisting(ShowEventName, out var show)) show.Set();
            Shutdown();
            return;
        }

        DispatcherUnhandledException += (_, ex) =>
        {
            Log.Write("Unhandled: " + ex.Exception);
            ex.Handled = true; // a shell must stay up; the error is logged for diagnosis
        };

        var quitEvent = new EventWaitHandle(false, EventResetMode.AutoReset, QuitEventName);
        new Thread(() => { quitEvent.WaitOne(); Dispatcher.BeginInvoke(Quit); }) { IsBackground = true }.Start();
        var showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
        new Thread(() =>
        {
            while (showEvent.WaitOne()) Dispatcher.BeginInvoke(() => MainDock?.ToggleLauncher(forceOpen: true));
        }) { IsBackground = true }.Start();

        Log.Write($"Norrsken {Version} starting");
        Settings = ShellSettings.Load();
        Theme.Apply(Settings.Theme);
        TaskbarHider.RecoverFromCrash();
        FrameRate.PreferFastGpu();

        Tracker = new WindowTracker();
        Tracker.Start();
        SystemStatus.Start();
        Dock = new DockModel();
        Dock.Attach();

        var dock = new DockWindow();
        MainWindow = dock;
        dock.Show();
        Fullscreen = new FullscreenWatcher();
        Fullscreen.Changed += dock.OnFullscreen;
        Fullscreen.Start();

        StartSystemSkin(dock);
        _ = PackageIdentity.EnsureAsync().ContinueWith(t => { if (t.Result) Dispatcher.BeginInvoke(PackageIdentity.Restart); });
        _ = LoadAppsAsync();
        _ = SystemLook.ApplyUserLookOnceAsync().ContinueWith(_ => Dispatcher.InvokeAsync(AppRetirement.RunOnceAsync));
        if (!Settings.WelcomeDone || e.Args.Contains("--welcome")) new WelcomeWindow().Show();
    }

    /// <summary>Norrsken apps, shortcuts, themed window frames and cursors: the parts that replace Windows' own.</summary>
    void StartSystemSkin(DockWindow dock)
    {
        NorrskenApps.EnsureShortcuts();
        KeyboardHook.Pressed += action =>
        {
            switch (action)
            {
                case "capture": CaptureOverlay.Start(); break;
                case "files": NorrskenApps.Launch("files"); break;
                case "settings": dock.OpenSettings(); break;
                case "launcher": dock.ToggleLauncher(); break;
                case "notifications": NotificationCentre.Toggle(); break;
                case "capture-screen": CaptureOverlay.SaveWholeScreen(); break;
                case "capture-window": CaptureOverlay.CaptureActiveWindow(); break;
                case "run": RunWindow.Toggle(); break;
                case "powermenu": PowerUserMenu.Show(dock.LauncherAnchor); break;
                case "quick": dock.ToggleQuickSettings(); break;
                case "search": dock.ToggleLauncher(forceOpen: true); break;
                case "clipboard": ClipboardWindow.Toggle(); break;
                case "project": ProjectWindow.Toggle(); break;
                case "dock": dock.RevealForShortcut(); break;
                case "about": dock.OpenSettings("about"); break;
                case "taskmgr": NorrskenApps.Launch("taskmgr"); break;
                case "power": PowerDialog.Open(); break;
                case "claude":
                    if (Apps.FirstOrDefault(a => a.Name.Equals("Claude", StringComparison.OrdinalIgnoreCase)) is { } claude) LaunchSafely(claude);
                    break;
                case var d when d.StartsWith("dock:"): dock.ActivateDockItem(d[5] - '1'); break;
            }
        };
        KeyboardHook.Start();
        ListenFor(NorrskenApps.CaptureEventName, () => CaptureOverlay.Start());
        ListenFor(NorrskenApps.SettingsEventName, dock.OpenSettings);
        FrameStyler.Start();
        ExplorerRedirect.Start();
        _ = Notifications.StartAsync();
        ClipboardHistory.Start();
        Updater.StartBackground();
        AltTab.Start();
        NorrskenCursors.Apply();
        DisplayControl.StartNightLight();
        MouseHook.DesktopRightClick += (x, y) => DesktopMenu.Show(dock, x, y);
        MouseHook.Start();
        TitleBars.Start();
        Exit += (_, _) => { KeyboardHook.Stop(); FrameStyler.Stop(); DisplayControl.ResetNightLight(); TitleBars.Stop(); };
    }

    void ListenFor(string name, Action action)
    {
        var ev = new EventWaitHandle(false, EventResetMode.AutoReset, name);
        new Thread(() => { while (ev.WaitOne()) Dispatcher.BeginInvoke(action); }) { IsBackground = true }.Start();
    }

    /// <summary>App processes (`--app`) read the same settings and theme as the dock.</summary>
    public static void LoadSettingsForApp()
    {
        Settings = ShellSettings.Load();
        Theme.Apply(Settings.Theme);
    }

    public static FullscreenWatcher? Fullscreen { get; private set; }

    /// <summary>The app installer started from Welcome; it keeps running after Welcome closes.</summary>
    public static AppInstaller? Installer { get; internal set; }

    /// <summary>The real dock, run in test mode with throwaway settings, driven by <see cref="UiTest"/>.</summary>
    void StartTestSession(string outDir)
    {
        TestMode = true;
        outDir = Path.GetFullPath(outDir);
        Directory.CreateDirectory(outDir);
        Environment.SetEnvironmentVariable("NORRSKEN_SETTINGS_DIR", Path.Combine(outDir, "settings"));
        Settings = new ShellSettings();
        Settings.Save();
        Theme.Apply(Settings.Theme);
        Tracker = new WindowTracker();
        Tracker.Start();
        SystemStatus.Start();
        Dock = new DockModel();
        Dock.Attach();
        var dock = new DockWindow();
        MainWindow = dock;
        dock.Show();
        Fullscreen = new FullscreenWatcher { OnlyProcesses = new() };
        Fullscreen.Changed += dock.OnFullscreen;
        Fullscreen.Start();
        Dispatcher.InvokeAsync(async () => Shutdown(await UiTest.RunAsync(outDir, dock)));
    }

    public static async Task LoadAppsAsync()
    {
        try
        {
            var list = await AppIndex.LoadAsync();
            if (Settings.ReplaceSystemApps)
                list.RemoveAll(a => NorrskenApps.ReplacedParsingNames.Contains(a.ParsingName, StringComparer.OrdinalIgnoreCase)
                                    || (a.TargetPath != null && NorrskenApps.ReplacedExecutables.Contains(Path.GetFileName(a.TargetPath), StringComparer.OrdinalIgnoreCase))
                                    || (File.Exists(AppInstaller.FlickerboxExe) && NorrskenApps.ReplacedByFlickerbox.Any(p => a.ParsingName.StartsWith(p, StringComparison.OrdinalIgnoreCase))));
            // Pinned icons first, so the dock never shows empty slots while the rest stream in.
            var wanted = (Settings.Pinned ?? DefaultPins.ToList()).ToHashSet(StringComparer.OrdinalIgnoreCase);
            await AppIndex.LoadIconsAsync(list.Where(a => wanted.Contains(a.ParsingName) || wanted.Contains(a.Name)).ToList());
            Apps.Clear();
            foreach (var a in list) Apps.Add(a);
            ResolvePinned();
            Log.Write($"Indexed {list.Count} apps");
            int icons = await AppIndex.LoadIconsAsync(list);
            Log.Write($"Loaded {icons} app icons");
        }
        catch (Exception ex) { Log.Write("App index failed: " + ex); }
    }

    public static void ResolvePinned()
    {
        var wanted = Settings.Pinned ?? DefaultPins.ToList();
        var resolved = new List<AppEntry>();
        foreach (var key in wanted)
        {
            var match = Apps.FirstOrDefault(a => a.ParsingName.Equals(key, StringComparison.OrdinalIgnoreCase))
                        ?? (Settings.Pinned == null
                            ? Apps.FirstOrDefault(a => a.Name.Equals(key, StringComparison.OrdinalIgnoreCase))
                            : null);
            if (match != null && !resolved.Contains(match)) resolved.Add(match);
        }
        Pinned.Clear();
        foreach (var p in resolved) Pinned.Add(p);
    }

    /// <summary>The one way to change theme: Norrsken's UI, saved settings, and (if enabled) wallpaper + lock screen.</summary>
    public static void SetTheme(string id)
    {
        Settings.Theme = id;
        Theme.Apply(id);
        Settings.Save();
        _ = SystemLook.ApplyAsync(id);
        FrameStyler.Refresh();
        NorrskenApps.BroadcastTheme();
    }

    public static void TogglePin(AppEntry app)
    {
        if (!Pinned.Remove(app)) Pinned.Add(app);
        SavePins();
    }

    public static void MovePin(AppEntry app, int index)
    {
        int from = Pinned.IndexOf(app);
        if (from < 0) return;
        index = Math.Clamp(index, 0, Pinned.Count - 1);
        if (from != index) Pinned.Move(from, index);
        SavePins();
    }

    static void SavePins()
    {
        Settings.Pinned = Pinned.Select(p => p.ParsingName).ToList();
        Settings.Save();
    }

    /// <summary>
    /// UI-test mode (only ever on an isolated, invisible desktop): no AppBar, hotkey, taskbar
    /// changes or app launches; launches are recorded instead.
    /// </summary>
    public static bool TestMode { get; internal set; }

    /// <summary>Showcase renders: the account name shown instead of the real one.</summary>
    public static string? DemoName { get; internal set; }
    public static List<string> TestLaunches { get; } = new();

    public static void LaunchSafely(AppEntry app)
    {
        if (TestMode) { TestLaunches.Add(app.Name); return; }
        try
        {
            AppIndex.Launch(app);
            Settings.Launches[app.ParsingName] = Settings.Launches.GetValueOrDefault(app.ParsingName) + 1;
            Settings.Save();
        }
        catch (Exception ex)
        {
            Log.Write($"Launch failed {app.ParsingName}: {ex}");
            MessageBox.Show($"{app.Name} couldn't be started.\n\n{ex.Message}", "Norrsken",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    public static void Quit() => Current.MainWindow?.Close();
}
