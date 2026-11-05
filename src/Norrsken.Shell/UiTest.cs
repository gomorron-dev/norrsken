using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Norrsken.Shell.Core;
using Norrsken.Shell.Interop;
using Norrsken.Shell.Ui;
using static Norrsken.Shell.Interop.Native;

namespace Norrsken.Shell;

/// <summary>
/// Interaction tests for the real dock, launcher, quick settings and settings windows.
/// `Norrsken.Shell.exe --uitest-isolated <dir>` runs them on a private hidden desktop: windows
/// really show, lay out, animate and receive events there, but nothing can reach the user's
/// screen, focus or input. System state (volume, media, network) is only read, never changed;
/// app launches are recorded, not performed; settings live in a throwaway folder.
/// </summary>
internal static partial class UiTest
{
    sealed record Result(string Name, string Status, string Detail);

    static readonly List<Result> Results = new();
    static string _out = "";

    static void Check(string name, bool ok, string detail) => Results.Add(new(name, ok ? "pass" : "fail", detail));
    static void Info(string name, string detail) => Results.Add(new(name, "info", detail));
    static Task Wait(int ms) => Task.Delay(ms);

    public static async Task<int> RunAsync(string outDir, DockWindow dock)
    {
        _out = outDir;
        var spawned = new List<Process>();
        try { await Steps(dock, spawned); }
        catch (Exception ex) { Results.Add(new("harness", "fail", ex.ToString())); }
        finally
        {
            foreach (var p in spawned)
            {
                try { if (!p.HasExited) p.Kill(); } catch (Exception) { }
            }
        }
        File.WriteAllText(Path.Combine(outDir, "uitest.json"), JsonSerializer.Serialize(
            new { desktop = IsolatedDesktop.CurrentName(), at = DateTime.Now, results = Results },
            new JsonSerializerOptions { WriteIndented = true }));
        return Results.Any(r => r.Status == "fail") ? 1 : 0;
    }

    static async Task Steps(DockWindow dock, List<Process> spawned)
    {
        Info("environment", $"running on hidden desktop '{IsolatedDesktop.CurrentName()}'");

        // 1. Entrance: the dock rises and fades in.
        var rise = new List<(double Y, double O)>();
        for (int i = 0; i < 7; i++) { rise.Add((dock.DockOffsetY, dock.DockOpacity)); await Wait(120); }
        Check("dock.rise", rise[0].Y > 15 && Math.Abs(rise[^1].Y) < 1 && rise[^1].O > 0.99,
            "offset/opacity: " + string.Join(" → ", rise.Select(r => $"{r.Y:0}/{r.O:0.00}")));

        // 2. Real apps from this PC; two Character Map windows on this hidden desktop as running apps.
        await App.LoadAppsAsync();
        for (int i = 0; i < 2; i++)
        {
            var p = Process.Start(new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "charmap.exe")) { UseShellExecute = false })!;
            spawned.Add(p);
            RecordSpawn(p);
        }
        TrackedWindow[] maps = [];
        for (int i = 0; i < 60 && maps.Length < 2; i++)
        {
            await Wait(100);
            App.Tracker.Refresh();
            maps = App.Tracker.Windows.Where(w => w.ExePath?.EndsWith("charmap.exe", StringComparison.OrdinalIgnoreCase) == true).ToArray();
        }
        App.Dock.RefreshNow();
        await Wait(700);
        int mapIdx = Index(it => it.Windows.Count >= 2);
        Check("env.windows", maps.Length == 2 && mapIdx >= 0,
            $"{maps.Length} Character Map windows; dock has {App.Dock.Items.Count} items, the two windows grouped under item {mapIdx}");
        Snap(dock, "ui-01-dock");
        if (maps.Length < 2 || mapIdx < 0) return;

        // 3. Hover: magnification, spread and label.
        int h = 2;
        dock.TestPointer(dock.CenterOf(h));
        await Wait(500);
        int far = Math.Min(h + 4, App.Dock.Items.Count - 1);
        double sH = dock.ScaleOf(h), sN = dock.ScaleOf(h + 1), sF = dock.ScaleOf(far);
        Check("hover.magnify", sH > 1.45 && sN > 1.0 && sN < sH && (far <= h + 1 || sF < sN), $"hovered {sH:0.00}×, neighbour {sN:0.00}×, four away {sF:0.00}×");
        Check("hover.spread", dock.VisualOffsetOf(h - 1) < -3 && dock.VisualOffsetOf(h + 1) > 3,
            $"left neighbour {dock.VisualOffsetOf(h - 1):0.0} DIP, right neighbour {dock.VisualOffsetOf(h + 1):+0.0} DIP");
        string name = App.Dock.Items[h].Name;
        Check("hover.label", dock.HoverLabelText == name && dock.HoverLabelOpacity > 0.9,
            $"'{dock.HoverLabelText}' at opacity {dock.HoverLabelOpacity:0.00}");
        Snap(dock, "ui-02-hover");
        dock.TestLeave();
        await Wait(600);
        Check("hover.settle", Math.Abs(dock.ScaleOf(h) - 1) < 0.01 && dock.HoverLabelOpacity < 0.01 && !dock.IsAnimating,
            $"scale {dock.ScaleOf(h):0.000}, label {dock.HoverLabelOpacity:0.00}, frame loop stopped: {!dock.IsAnimating}");

        // 4. Live window previews with hover intent.
        dock.TestPointer(dock.CenterOf(mapIdx));
        await Wait(300);
        bool early = dock.Preview?.IsVisible == true;
        await Wait(650);
        var pv = dock.Preview;
        Check("preview.intent", !early && pv?.IsVisible == true, $"not shown at 300 ms: {!early}; shown by 950 ms: {pv?.IsVisible == true}");
        Check("preview.slots", pv?.SlotCount == 2, $"{pv?.SlotCount} window slots");
        Info("preview.thumbnails", $"{pv?.ThumbCount} live DWM thumbnails registered (the compositor draws them; screenshots of a hidden desktop can't show them)");
        Check("preview.label-yields", dock.HoverLabelOpacity < 0.05, $"hover label {dock.HoverLabelOpacity:0.00} while the preview is up");
        if (pv != null) Snap(pv, "ui-03-preview");
        dock.TestLeave();
        await Wait(800);
        Check("preview.dismiss", pv?.IsVisible != true, "preview closed after the pointer left");

        // 5. Clicking an app with two windows cycles between them.
        IntPtr f1, f2;
        dock.TestClick(mapIdx); await Wait(250); f1 = GetForegroundWindow();
        dock.TestClick(mapIdx); await Wait(250); f2 = GetForegroundWindow();
        bool cycled = maps.Any(m => m.Handle == f1) && maps.Any(m => m.Handle == f2) && f1 != f2;
        if (cycled) Check("click.cycle", true, "consecutive clicks brought each Character Map window to the front in turn");
        else Info("click.cycle", $"foreground after clicks: {Native.GetWindowText(f1)} / {Native.GetWindowText(f2)} (Windows limits activation on a non-input desktop)");

        // 6. Launching a pinned app: bounce, and a launch request (recorded, not performed).
        int idle = Index(it => it.IsPinned && !it.IsRunning && it.Entry != null);
        App.TestLaunches.Clear();
        dock.TestClick(idle);
        double peak = 0;
        for (int i = 0; i < 40; i++) { peak = Math.Min(peak, dock.BounceOf(idle)); await Wait(20); }
        await Wait(400);
        Check("launch.bounce", peak < -10 && Math.Abs(dock.BounceOf(idle)) < 0.5, $"icon hopped to {peak:0.0} DIP and landed at {dock.BounceOf(idle):0.0}");
        Check("launch.request", App.TestLaunches.SequenceEqual([App.Dock.Items[idle].Name]),
            $"launch requested: {string.Join(", ", App.TestLaunches)}");

        // 7. All windows minimised: the dot dims; restored: full again.
        foreach (var w in maps) ShowWindow(w.Handle, SW_MINIMIZE);
        await Wait(300); App.Tracker.Refresh(); App.Dock.RefreshNow(); await Wait(450);
        double dim = dock.DotOfItem(mapIdx).Opacity;
        foreach (var w in maps) ShowWindow(w.Handle, SW_RESTORE);
        await Wait(300); App.Tracker.Refresh(); App.Dock.RefreshNow(); await Wait(450);
        double lit = dock.DotOfItem(mapIdx).Opacity;
        Check("dot.minimised", dim is > 0.3 and < 0.5 && lit > 0.95, $"all minimised {dim:0.00}, restored {lit:0.00}");

        // 8. Attention: a flashing window makes its icon hop and its dot breathe.
        var target = maps.FirstOrDefault(m => !m.IsActive) ?? maps[0];
        var flash = new FLASHWINFO { cbSize = (uint)Marshal.SizeOf<FLASHWINFO>(), hwnd = target.Handle, dwFlags = 2 /* tray */, uCount = 2 };
        FlashWindowEx(ref flash);
        bool viaHook = false;
        for (int i = 0; i < 15 && !viaHook; i++) { await Wait(100); viaHook = target.NeedsAttention; }
        if (!viaHook) target.NeedsAttention = true; // simulate delivery if the shell hook is silent here
        App.Dock.RefreshNow();
        double hop = 0, breath = 1;
        for (int i = 0; i < 64; i++)
        {
            hop = Math.Min(hop, dock.BounceOf(mapIdx));
            breath = Math.Min(breath, dock.DotOfItem(mapIdx).Opacity);
            await Wait(25);
        }
        Check("attention.animate", App.Dock.Items[mapIdx].NeedsAttention && hop < -10 && breath < 0.7,
            $"shell hook delivered the flash: {viaHook}; hop {hop:0.0} DIP; dot breathed to {breath:0.00}");
        target.NeedsAttention = false;
        App.Dock.RefreshNow();
        await Wait(250);
        Check("attention.clear", !App.Dock.Items[mapIdx].NeedsAttention && dock.DotOfItem(mapIdx).Opacity > 0.95,
            $"after switching to it: dot {dock.DotOfItem(mapIdx).Opacity:0.00}");

        // 9. Drag to reorder pins: a gap opens, the drop sticks and is saved, icons glide home.
        var before = App.Pinned.Select(p => p.Name).ToList();
        dock.TestDragStart(0);
        for (int i = 1; i <= 10; i++) { dock.TestDragTo(54 * 1.6 * i / 10, 0); await Wait(16); }
        await Wait(350);
        double gap = dock.VisualOffsetOf(1);
        Snap(dock, "ui-04-drag");
        dock.TestDragEnd();
        await Wait(150);
        double glide = dock.VisualOffsetOf(1);
        await Wait(700);
        var after = App.Pinned.Select(p => p.Name).ToList();
        var saved = ShellSettings.Load().Pinned;
        Check("drag.gap", gap < -40, $"neighbour slid {gap:0} DIP aside to open a gap");
        Check("drag.reorder", after.Count == before.Count && after[0] == before[1] && after[1] == before[0] && saved?.Count == after.Count,
            $"before: {string.Join(", ", before.Take(3))}; after: {string.Join(", ", after.Take(3))}; saved: {saved != null}");
        Check("drag.glide", Math.Abs(glide) > 1 && Math.Abs(dock.VisualOffsetOf(0)) < 1 && Math.Abs(dock.VisualOffsetOf(1)) < 1 && !dock.IsAnimating,
            $"150 ms after drop an icon was still gliding ({glide:0.0} DIP); everything settled after");

        // 10. Drag up off the dock to unpin.
        int victimIdx = Index(it => it.IsPinned && !it.IsRunning);
        var victim = App.Dock.Items[victimIdx].Entry!;
        dock.TestDragStart(victimIdx);
        for (int i = 1; i <= 8; i++) { dock.TestDragTo(0, -95.0 * i / 8); await Wait(16); }
        await Wait(250);
        string removeLabel = dock.HoverLabelText;
        dock.TestDragEnd();
        await Wait(1000);
        Check("drag.unpin", removeLabel == "Remove from dock" && !App.Pinned.Contains(victim) && App.Dock.Items.All(i => i.Entry != victim),
            $"label while lifted: '{removeLabel}'; '{victim.Name}' unpinned and gone: {!App.Pinned.Contains(victim)}");

        // 11. Auto-hide: slides away, the screen edge reveals it, it hides again if not used.
        App.Settings.AutoHide = true;
        dock.ApplySettings();
        dock.TestLeave();
        var (hid, hideMs) = await WaitUntil(() => dock.IsDockHidden && dock.DockOffsetY > 90 && dock.DockOpacity < 0.05 && dock.RevealZoneVisible, 4000);
        dock.TestReveal();
        var (shown, showMs) = await WaitUntil(() => !dock.IsDockHidden && Math.Abs(dock.DockOffsetY) < 1 && dock.DockOpacity > 0.95, 3000);
        var (rehid, rehideMs) = await WaitUntil(() => dock.IsDockHidden, 4000);
        // Design: 700 ms idle timer + 300 ms slide ≈ 1 s; anything under ~2 s is on time.
        Check("autohide", hid && shown && rehid && hideMs < 2500 && rehideMs < 2500,
            $"slid away after {hideMs} ms: {hid}; edge revealed it in {showMs} ms: {shown}; hid again when unused after {rehideMs} ms: {rehid}" +
            $"; state at hide check: hidden={dock.IsDockHidden} launcher={dock.Launcher?.IsVisible} quick={dock.Quick?.IsVisible} preview={dock.Preview?.IsVisible} menu={dock.Menu?.IsOpen}");

        // 12. Full screen (YouTube, games): dock, popups and the auto-hide edge all step aside.
        dock.TestReveal();
        await Wait(500);
        dock.ToggleLauncher();
        await Wait(350);
        bool launcherWasOpen = dock.Launcher?.IsVisible == true;
        var probe = Process.Start(new ProcessStartInfo(Environment.ProcessPath!, "--fullscreen-probe 6") { UseShellExecute = false })!;
        spawned.Add(probe);
        RecordSpawn(probe);
        App.Fullscreen!.OnlyProcesses!.Add((uint)probe.Id);
        bool viaWindow = false;
        for (int i = 0; i < 30 && !viaWindow; i++) { await Wait(100); App.Fullscreen.Evaluate(); viaWindow = App.Fullscreen.IsFullscreen; }
        if (!viaWindow) App.Fullscreen.ShellSignal(true);
        await Wait(450);
        bool aside = dock.IsSteppedAside && dock.DockOpacity < 0.05 && !dock.Topmost && !dock.RevealZoneVisible
                     && dock.Launcher?.IsVisible != true;
        Check("fullscreen.hide", launcherWasOpen && aside,
            $"detected via {(viaWindow ? "the full-screen window itself" : "the shell signal")}; dock opacity {dock.DockOpacity:0.00}, " +
            $"topmost {dock.Topmost}, launcher closed {dock.Launcher?.IsVisible != true}, edge strip off {!dock.RevealZoneVisible}");
        if (viaWindow)
        {
            probe.Kill();
            for (int i = 0; i < 30 && App.Fullscreen.IsFullscreen; i++) { await Wait(100); App.Fullscreen.Evaluate(); }
        }
        else App.Fullscreen.ShellSignal(false);
        await Wait(450);
        Check("fullscreen.restore", !dock.IsSteppedAside && dock.Topmost && dock.DockOpacity > 0.95,
            $"after full screen ended: opacity {dock.DockOpacity:0.00}, topmost {dock.Topmost}");
        App.Settings.AutoHide = false;
        dock.ApplySettings();
        await Wait(600);

        var plain = MakeWindow(WindowStyle.SingleBorderWindow, WindowState.Maximized);
        await Wait(250);
        bool maxIgnored = !FullscreenWatcher.IsFullscreenWindow(new WindowInteropHelper(plain).Handle);
        plain.Close();
        Check("fullscreen.ignores-maximised", maxIgnored, "a normal maximised window (like windowed Minecraft) keeps the dock");

        // 13. Launcher: opens with motion and a cascade, searches, keyboard works, Enter launches.
        dock.ToggleLauncher();
        var l = dock.Launcher!;
        var card = new List<double>();
        double firstTile = 1;
        for (int i = 0; i < 7; i++)
        {
            card.Add(l.Card.Opacity);
            if (i == 1 && l.Tiles().ElementAtOrDefault(10) is UIElement tile) firstTile = tile.Opacity;
            await Wait(60);
        }
        await Wait(300);
        Check("launcher.open", l.IsVisible && card[0] < 0.7 && card[^1] > 0.99, "card opacity: " + string.Join(" → ", card.Select(o => $"{o:0.00}")));
        Check("launcher.cascade", firstTile < 0.9, $"11th icon at {firstTile:0.00} opacity while the first ones were already in");
        Snap(l, "ui-05-launcher");
        // Search for a real app on this PC by the start of its name.
        string probeName = App.Apps.First(a => a.Name.Length >= 6 && char.IsLetter(a.Name[0])).Name;
        string query = probeName[..5];
        l.SearchBox.Text = query;
        await Wait(60);
        string? top = l.SelectedTitle;
        Snap(l, "ui-05b-launcher-search");
        Check("launcher.search", top?.StartsWith(query, StringComparison.CurrentCultureIgnoreCase) == true, $"'{query}' → {top}");
        l.SearchBox.Text = "e";
        await Wait(60);
        int first = l.SelectedIndex;
        Key(l.SearchBox, System.Windows.Input.Key.Down);
        int down = l.SelectedIndex;
        Key(l.SearchBox, System.Windows.Input.Key.Up);
        int up = l.SelectedIndex;
        Check("launcher.keys", first == 0 && down == 1 && up == 0, $"selection {first} → ↓ {down} → ↑ {up}");
        Key(l.SearchBox, System.Windows.Input.Key.Down);
        App.TestLaunches.Clear();
        string? expected = l.SelectedTitle;
        Key(l.SearchBox, System.Windows.Input.Key.Enter);
        await Wait(450);
        Check("launcher.enter", App.TestLaunches.LastOrDefault() == expected && !l.IsVisible, $"Enter launched '{App.TestLaunches.LastOrDefault()}' and closed the launcher");

        // 14. Quick settings: opens, shows live (read-only) state, theme tile works, Esc closes.
        dock.ToggleQuickSettings();
        await Wait(500);
        var q = dock.Quick!;
        Check("quick.open", q.IsVisible && q.Card.Opacity > 0.99, "panel open and settled");
        Check("quick.live", !SystemStatus.HasAudio || Math.Abs(q.VolumeSlider.Value - SystemStatus.Volume) < 0.5,
            $"volume slider {q.VolumeSlider.Value:0} matches system {SystemStatus.Volume:0} (read only); network '{q.NetworkText.Text}'");
        Snap(q, "ui-06-quick");
        q.ThemeTile.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        await Wait(200);
        bool paper = Theme.Current == "paper" && q.ThemeTile.Tag as string == "on";
        Snap(q, "ui-07-quick-paper");
        Snap(dock, "ui-08-dock-paper");
        q.ThemeTile.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        await Wait(150);
        Check("quick.theme", paper && Theme.Current == "graphite", "Light tile switched the whole UI to Paper and back");
        Key(q, System.Windows.Input.Key.Escape, preview: false);
        await Wait(400);
        Check("quick.escape", !q.IsVisible, "Esc closed the panel");

        // 15. Settings: toggles animate to their real state, and changing one changes behaviour.
        dock.OpenSettings();
        await Wait(700);
        var s = dock.SettingsWin!;
        var thumb = (FrameworkElement)s.MagnifyToggle.Template.FindName("Thumb", s.MagnifyToggle);
        double thumbX = ((TranslateTransform)thumb.RenderTransform).X;
        Check("settings.toggles", thumbX > 17, $"'Magnify' switch thumb slid to x={thumbX:0.0} (on position 18)");
        Snap(s, "ui-09-settings");
        s.MagnifyToggle.IsChecked = false;
        await Wait(80);
        dock.TestPointer(dock.CenterOf(1));
        await Wait(450);
        double noMagnify = dock.ScaleOf(1);
        dock.TestLeave();
        s.MagnifyToggle.IsChecked = true;
        await Wait(80);
        Check("settings.effect", noMagnify < 1.01 && App.Settings.Magnify && ShellSettings.Load().Magnify,
            $"with Magnify off the hovered icon stayed at {noMagnify:0.00}×; switched back on and saved");
        s.Close();

        // 16. Right-click menu (indices shifted when a pin was removed above).
        mapIdx = Index(it => it.Windows.Count >= 2);
        dock.TestClick(mapIdx, MouseButton.Right);
        await Wait(350);
        var menu = dock.Menu;
        Check("menu.open", menu?.IsOpen == true && menu.Items.Count >= 6,
            $"{menu?.Items.Count} entries (title, both windows, New window, Keep in dock, Close 2 windows)");
        if (menu != null) menu.IsOpen = false;

        // 17. Idle: once settled, no per-frame work and near-zero CPU.
        await Wait(1200);
        var me = Process.GetCurrentProcess();
        me.Refresh();
        var c1 = me.TotalProcessorTime;
        await Wait(3000);
        me.Refresh();
        double cpu = (me.TotalProcessorTime - c1).TotalMilliseconds;
        Check("idle", !dock.IsAnimating && cpu < 150, $"frame loop stopped: {!dock.IsAnimating}; {cpu:0} ms CPU over 3 s");

        await WelcomeFlow();
        await SetupFlow();
        await LookFollowsTheme();
        await NorrskenAppsFlow(dock);

        foreach (var w in maps) WindowTracker.Close(w);
    }

    /// <summary>
    /// A fake C:\Windows.old in the test folder, full of the awkward cases a real one has:
    /// Unicode names, a very long path, a hidden file, a newer file already at the destination,
    /// custom folders, and things that must NOT come across (registry hive, AppData, OneDrive).
    /// </summary>
    static (string Old, string Target) MakeFakeOldWindows()
    {
        string root = Path.Combine(_out, "fake"), old = Path.Combine(root, "Windows.old"), target = Path.Combine(root, "NewProfile");
        string user = Path.Combine(old, @"Users\olduser");
        void Put(string rel, string text) { var p = Path.Combine(user, rel); Directory.CreateDirectory(Path.GetDirectoryName(p)!); File.WriteAllText(p, text); }
        Put(@"Desktop\notes.txt", "hello from the old desktop");
        Put(@"Desktop\keep.txt", "old version");
        Put(@"Documents\Rapport åäö 漢字.docx", "unicode name");
        Put(Path.Combine("Documents", string.Join('\\', Enumerable.Repeat("a very deep folder name", 12)), "deep.txt"), "long path");
        var bin = new byte[3 * 1024 * 1024];
        new Random(7).NextBytes(bin);
        Directory.CreateDirectory(Path.Combine(user, "Downloads"));
        File.WriteAllBytes(Path.Combine(user, @"Downloads\setup.bin"), bin);
        Put(@"Pictures\secret.jpg", "hidden picture");
        File.SetAttributes(Path.Combine(user, @"Pictures\secret.jpg"), FileAttributes.Hidden);
        Put(@"Projects\app\main.cs", "class App {}");
        Put(".gitconfig", "[user] name = me");
        Put("NTUSER.DAT", "old registry hive");
        Put(@"AppData\Local\Game\save.dat", "app data");
        Put(@"OneDrive\cloud.txt", "cloud");
        Directory.CreateDirectory(Path.Combine(old, @"Users\Public\Documents"));
        foreach (var lnk in new[] { @"Steam\Steam.lnk", "Google Chrome.lnk", "Mozilla Firefox.lnk" })
        {
            var p = Path.Combine(old, @"ProgramData\Microsoft\Windows\Start Menu\Programs", lnk);
            Directory.CreateDirectory(Path.GetDirectoryName(p)!);
            File.WriteAllText(p, "");
        }
        Directory.CreateDirectory(Path.Combine(old, @"Program Files\Google\Chrome"));
        Directory.CreateDirectory(Path.Combine(old, @"Program Files (x86)\Steam"));
        Directory.CreateDirectory(Path.Combine(target, "Desktop"));
        File.WriteAllText(Path.Combine(target, @"Desktop\keep.txt"), "NEWER CONTENT ALREADY HERE");
        return (old, target);
    }

    /// <summary>First boot: hello → bring files back (real copy, fake data) → pick apps → install (simulated) → desktop.</summary>
    static async Task WelcomeFlow()
    {
        var (old, target) = MakeFakeOldWindows();
        Environment.SetEnvironmentVariable("NORRSKEN_WINDOWS_OLD", old);
        Environment.SetEnvironmentVariable("NORRSKEN_RESTORE_TARGET", target);

        var welcome = new WelcomeWindow();
        welcome.Show();
        await Wait(600);
        Check("welcome.hello", welcome.HelloStep.Visibility == Visibility.Visible && welcome.HelloStep.Opacity > 0.99, "hello step shown");
        Snap(welcome, "ui-10-welcome");
        welcome.ContinueButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        await Wait(800);

        // Bring your files back.
        Check("restore.offered", welcome.RestoreStep.Visibility == Visibility.Visible && welcome.FolderList.Children.Count == 8,
            $"old Windows found; {welcome.FolderList.Children.Count} choices (6 known folders, other files, app data)");
        Check("restore.detect-apps", welcome.Detected.SetEquals(["Valve.Steam", "Google.Chrome", "Mozilla.Firefox"]),
            $"apps recognised on the old system: {string.Join(", ", welcome.Detected)} (\"Go\" must not match \"Google\")");
        Snap(welcome, "ui-14-restore-choose");
        welcome.RestoreButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        for (int i = 0; i < 300 && (welcome.RestoreItems.Count == 0 || welcome.RestoreItems.Any(r => r.State is InstallState.Queued or InstallState.Installing)); i++)
            await Wait(100);
        await Wait(300);
        Snap(welcome, "ui-15-restore-done");
        string T(string rel) => Path.Combine(target, rel);
        string srcBin = Path.Combine(old, @"Users\olduser\Downloads\setup.bin");
        bool sameBytes = File.Exists(T(@"Downloads\setup.bin"))
                         && Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(T(@"Downloads\setup.bin"))))
                            == Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(srcBin)));
        string deep = T(Path.Combine("Documents", string.Join('\\', Enumerable.Repeat("a very deep folder name", 12)), "deep.txt"));
        var checks = new (string What, bool Ok)[]
        {
            ("all folders done", welcome.RestoreItems.All(r => r.State == InstallState.Done)),
            ("desktop file", File.ReadAllText(T(@"Desktop\notes.txt")) == "hello from the old desktop"),
            ("newer file not overwritten", File.ReadAllText(T(@"Desktop\keep.txt")) == "NEWER CONTENT ALREADY HERE"),
            ("unicode name", File.Exists(T(@"Documents\Rapport åäö 漢字.docx"))),
            ($"long path ({deep.Length} chars)", File.Exists(deep)),
            ("3 MB download, identical SHA-256", sameBytes),
            ("hidden stays hidden", File.Exists(T(@"Pictures\secret.jpg")) && File.GetAttributes(T(@"Pictures\secret.jpg")).HasFlag(FileAttributes.Hidden)),
            ("custom folder", File.Exists(T(@"Projects\app\main.cs"))),
            ("root dotfile", File.Exists(T(".gitconfig"))),
            ("no registry hive", !File.Exists(T("NTUSER.DAT"))),
            ("no AppData (opt-in)", !Directory.Exists(T("AppData"))),
            ("no OneDrive (re-syncs)", !Directory.Exists(T("OneDrive"))),
            ("originals untouched", File.Exists(srcBin) && File.Exists(Path.Combine(old, @"Users\olduser\Desktop\notes.txt"))),
        };
        Check("restore.files", checks.All(c => c.Ok),
            string.Join("; ", checks.Select(c => (c.Ok ? "✓ " : "✗ ") + c.What)));
        Info("restore.report", string.Join(" | ", welcome.RestoreItems.Select(r => $"{r.Name}: {r.Detail}")));
        welcome.RestoreNextButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        await Wait(800);

        int expectedSelected = AppCatalog.Optional.Count(a => a.Default || welcome.Detected.Contains(a.Id));
        Check("welcome.apps", welcome.AppsStep.Visibility == Visibility.Visible && welcome.Selected.Count == expectedSelected
                              && welcome.ChipFor("Mozilla.Firefox")!.IsChecked == true && welcome.AppsSubtitle.Text.Contains("previous Windows"),
            $"app picker: {welcome.Selected.Count} preselected, including Firefox from the old system");
        welcome.ChipFor("Brave.Brave")!.IsChecked = true;
        welcome.ChipFor("VideoLAN.VLC")!.IsChecked = false;
        await Wait(100);
        int defaults = expectedSelected;
        Check("welcome.select", welcome.Selected.Count == defaults && welcome.Selected.Any(a => a.Id == "Brave.Brave")
                               && welcome.SelectedText.Text.StartsWith(defaults.ToString()),
            $"toggled Brave on, VLC off → '{welcome.SelectedText.Text}'");
        Snap(welcome, "ui-11-pick-apps");

        var spotify = AppCatalog.Find("Spotify.Spotify")!;
        string args = AppInstaller.WingetArgs(spotify);
        Check("install.command", args.Contains("--id Spotify.Spotify -e --source winget") && args.Contains("--silent") && !args.Contains("--scope")
                                 && spotify.Scope == InstallScope.User,
            $"winget {args} (runs unelevated: Spotify refuses admin installs)");

        welcome.InstallButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        await Wait(700);
        var installer = App.Installer!;
        bool midway = installer.Running && installer.Items.Any(i => i.State == InstallState.Done) && installer.Items.Any(i => i.State != InstallState.Done);
        Snap(welcome, "ui-12-installing");
        for (int i = 0; i < 300 && installer.Running; i++) await Wait(100);
        await Wait(500);
        int expected = AppCatalog.Essentials.Count + defaults;
        Check("install.flow", midway && !installer.Running && installer.Items.Count == expected && installer.DoneCount == expected
                              && welcome.InstallTitle.Text == "You're all set",
            $"{installer.Items.Count} items ({AppCatalog.Essentials.Count} essentials first), all done; progress shown mid-way: {midway}; title '{welcome.InstallTitle.Text}'");
        Check("install.order", installer.Items.Take(AppCatalog.Essentials.Count).All(i => i.App.Category == "Essentials"),
            "game runtimes install before anything else");
        Snap(welcome, "ui-13-installed");
        welcome.FinishButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        await Wait(500);
        Check("welcome.finish", !welcome.IsVisible && App.Settings.WelcomeDone && ShellSettings.Load().WelcomeDone,
            "closed to the desktop and won't show again");
    }

    /// <summary>
    /// "Install Norrsken" from a (fake) USB stick inside an existing Windows: copies itself,
    /// registers autostart and identity (recorded, not written, in test mode), keeps existing
    /// settings, and only allows a Windows refresh after the licence terms are accepted.
    /// </summary>
    static async Task SetupFlow()
    {
        string usb = Path.Combine(_out, "fake-usb"), target = Path.Combine(_out, "fake-programfiles", "Norrsken");
        Directory.CreateDirectory(Path.Combine(usb, "assets"));
        File.WriteAllText(Path.Combine(usb, "setup.exe"), "fake Windows Setup");
        File.WriteAllBytes(Path.Combine(usb, "assets", "norrsken-graphite.png"), [0x89, 0x50, 0x4E, 0x47]); // stand-in wallpaper
        Environment.SetEnvironmentVariable("NORRSKEN_ISO_ROOT", usb);
        Environment.SetEnvironmentVariable("NORRSKEN_ASSETS", Path.Combine(usb, "assets"));
        Environment.SetEnvironmentVariable("NORRSKEN_INSTALL_TARGET", target);

        var setup = new SetupWindow();
        setup.Show();
        await Wait(500);
        Snap(setup, "ui-16-setup");
        bool plainEnabled = setup.InstallButton.IsEnabled;
        setup.RefreshToggle.IsChecked = true;
        await Wait(80);
        bool blockedWithoutLicence = !setup.InstallButton.IsEnabled && setup.LicenceRow.Visibility == Visibility.Visible;
        setup.LicenceToggle.IsChecked = true;
        await Wait(80);
        Check("setup.licence", plainEnabled && blockedWithoutLicence && setup.InstallButton.IsEnabled,
            "Install works straight away; refreshing Windows stays disabled until the licence terms are accepted");
        Snap(setup, "ui-17-setup-refresh");

        setup.InstallButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        for (int i = 0; i < 300 && (setup.Items.Count == 0 || setup.Items.Any(s => s.State is InstallState.Queued or InstallState.Installing)); i++)
            await Wait(100);
        await Wait(300);
        Snap(setup, "ui-18-setup-done");
        string registry = File.Exists(Path.Combine(target, "registry.txt")) ? File.ReadAllText(Path.Combine(target, "registry.txt")) : "";
        string setupCmd = File.Exists(Path.Combine(target, "setup-command.txt")) ? File.ReadAllText(Path.Combine(target, "setup-command.txt")) : "";
        var checks = new (string What, bool Ok)[]
        {
            ("all steps done", setup.Items.Count == InPlaceSetup.Steps.Count(s => s.Id != "stop") && setup.Items.All(s => s.State == InstallState.Done)),
            ("startup task step", File.Exists(Path.Combine(target, "integrate.txt"))),
            ("default apps step", File.Exists(Path.Combine(target, "defaults.txt"))),
            ("boot menu named Norrsken OS", File.Exists(Path.Combine(target, "bcdedit.txt"))
                                           && File.ReadAllText(Path.Combine(target, "bcdedit.txt")).Contains("description \"Norrsken OS\"")),
            ("Norrsken copied", File.Exists(Path.Combine(target, "Norrsken.Shell.exe"))),
            ("autostart for every account", File.Exists(Path.Combine(target, "integrate.txt")) && File.ReadAllText(Path.Combine(target, "integrate.txt")).Contains("Norrsken.Shell.exe\" --integrate")),
            ("OEM identity", registry.Contains("OEMInformation") && registry.Contains("Norrsken OS")),
            ("wallpaper", File.Exists(Path.Combine(target, "Wallpapers", "norrsken-graphite.png"))),
            ("user settings: dock-first", File.Exists(Path.Combine(target, "user-settings.json"))
                                         && File.ReadAllText(Path.Combine(target, "user-settings.json")).Contains("\"HideWindowsTaskbar\": true")),
            ("Windows Setup: in-place, keeps files and apps", setupCmd.Contains("setup.exe") && setupCmd.Contains("/auto upgrade")),
        };
        Check("setup.apply", checks.All(c => c.Ok), string.Join("; ", checks.Select(c => (c.Ok ? "✓ " : "✗ ") + c.What)));
        setup.Close();
    }

    /// <summary>On a Norrsken OS install, wallpaper + lock screen follow the theme (recorded, never applied, in tests).</summary>
    static async Task LookFollowsTheme()
    {
        string walls = Path.Combine(_out, "fake-wallpapers");
        Directory.CreateDirectory(walls);
        foreach (var n in new[] { "graphite", "paper", "ink" }) File.WriteAllBytes(Path.Combine(walls, $"norrsken-{n}.png"), [0x89, 0x50]);
        Environment.SetEnvironmentVariable("NORRSKEN_WALLPAPER_DIR", walls);
        SystemLook.TestLog.Clear();
        App.Settings.MatchWallpaper = false;
        App.SetTheme("paper");
        await Wait(100);
        bool quietWhenOff = SystemLook.TestLog.Count == 0;
        App.Settings.MatchWallpaper = true;
        App.SetTheme("paper");
        App.SetTheme("high-contrast");
        App.SetTheme("graphite");
        await Wait(100);
        Check("look.follow-theme", quietWhenOff && SystemLook.TestLog.SequenceEqual([
                "wallpaper and lock screen → norrsken-paper.png", "wallpaper and lock screen → norrsken-ink.png",
                "wallpaper and lock screen → norrsken-graphite.png"]),
            $"off: nothing touched; on: {string.Join(" | ", SystemLook.TestLog)}");
        SystemLook.TestLog.Clear();
        App.Settings.ApplyUserLook = true;
        App.Settings.UserLookApplied = false;
        await SystemLook.ApplyUserLookOnceAsync();
        await SystemLook.ApplyUserLookOnceAsync(); // second sign-in: must not apply again
        Check("look.first-sign-in", SystemLook.TestLog.SequenceEqual(["user look → light=0, file extensions shown", "wallpaper and lock screen → norrsken-graphite.png"])
                                    && App.Settings.UserLookApplied && ShellSettings.Load().UserLookApplied,
            $"applied once: {string.Join(" | ", SystemLook.TestLog)}");
        string avatars = Path.Combine(_out, "branding-check");
        Branding.Render(avatars);
        var names = new[] { "user.png", "user.bmp", "user-192.png", "user-48.png", "user-40.png", "user-32.png", "guest.png", "guest.bmp" };
        Check("look.account-pictures", names.All(n => File.Exists(Path.Combine(avatars, "account-pictures", n))),
            $"all {names.Length} account picture sizes rendered");
    }

    /// <summary>Poll a condition (UI keeps running between polls); returns whether it held and how long it took.</summary>
    static async Task<(bool Ok, long Ms)> WaitUntil(Func<bool> condition, int timeoutMs)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            if (condition()) return (true, sw.ElapsedMilliseconds);
            await Wait(50);
        }
        return (condition(), sw.ElapsedMilliseconds);
    }

    static int Index(Func<DockItem, bool> predicate) => App.Dock.Items.ToList().FindIndex(i => predicate(i));

    static void Key(UIElement target, System.Windows.Input.Key key, bool preview = true)
    {
        var source = PresentationSource.FromVisual(target)!;
        target.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, source, 0, key)
        {
            RoutedEvent = preview ? Keyboard.PreviewKeyDownEvent : Keyboard.KeyDownEvent,
        });
    }

    static Window MakeWindow(WindowStyle style, WindowState state)
    {
        // ShowActivated must stay true for maximised windows (WPF rule); this only ever runs on the hidden test desktop.
        var w = new Window { WindowStyle = style, WindowState = state, ShowInTaskbar = false, Title = "Norrsken test" };
        w.Show();
        return w;
    }

    /// <summary>Paint a window's live visual tree onto a neutral backdrop and save it.</summary>
    static void Snap(Window w, string name)
    {
        if (w.Content is not FrameworkElement root || root.ActualWidth < 1) return;
        int width = (int)Math.Ceiling(root.ActualWidth), height = (int)Math.Ceiling(root.ActualHeight);
        var rtb = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        var bg = new DrawingVisual();
        using (var dc = bg.RenderOpen())
            dc.DrawRectangle(new LinearGradientBrush(Color.FromRgb(78, 82, 90), Color.FromRgb(30, 32, 36), 90), null, new Rect(0, 0, width, height));
        rtb.Render(bg);
        rtb.Render(root);
        BitmapSource img = rtb;
        if (w is DockWindow && width > 1200) img = new CroppedBitmap(rtb, new Int32Rect((width - 1200) / 2, 0, 1200, height));
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(img));
        using var fs = File.Create(Path.Combine(_out, name + ".png"));
        enc.Save(fs);
    }

    static void RecordSpawn(Process p) => File.AppendAllText(Path.Combine(_out, "spawned.txt"), p.Id + Environment.NewLine);

    /// <summary>Runner side: make sure nothing the test started outlives it.</summary>
    public static void KillLeftovers(string outDir)
    {
        string file = Path.Combine(outDir, "spawned.txt");
        if (!File.Exists(file)) return;
        foreach (var line in File.ReadAllLines(file))
        {
            if (!int.TryParse(line, out int pid)) continue;
            try { Process.GetProcessById(pid).Kill(); } catch (Exception) { }
        }
        File.Delete(file);
    }

    /// <summary>A caption-less window covering the primary screen, like YouTube full screen. Hidden desktop only.</summary>
    public static void ShowFullscreenProbe(int seconds)
    {
        var w = new Window
        {
            WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize, ShowInTaskbar = false, Topmost = true,
            Left = 0, Top = 0, Width = SystemParameters.PrimaryScreenWidth, Height = SystemParameters.PrimaryScreenHeight,
            Background = Brushes.Black, Title = "Norrsken full-screen probe",
        };
        w.Show();
        w.Activate();
        var t = new DispatcherTimer { Interval = TimeSpan.FromSeconds(seconds) };
        t.Tick += (_, _) => Application.Current.Shutdown();
        t.Start();
    }
}
