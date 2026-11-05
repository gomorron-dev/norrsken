using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Norrsken.Shell.Core;
using Norrsken.Shell.Ui;

namespace Norrsken.Shell;

/// <summary>Norrsken's own apps, driven for real on the hidden test desktop with throwaway files.</summary>
internal static partial class UiTest
{
    static async Task NorrskenAppsFlow(DockWindow dock)
    {
        string root = Path.Combine(_out, "apps");
        if (Directory.Exists(root)) Directory.Delete(root, true);
        Directory.CreateDirectory(root);

        // ── Notes ──
        string txt = Path.Combine(root, "note.txt");
        File.WriteAllText(txt, "alpha beta\nalpha gamma\n");
        var notes = new NotesWindow(txt);
        notes.Show();
        await Wait(500);
        Check("notes.open", notes.EditorBox.Text.Contains("gamma") && !notes.Dirty && notes.Title.StartsWith("note.txt"), $"title '{notes.Title}', {notes.EditorBox.Text.Length} chars");
        notes.EditorBox.AppendText("delta\n");
        bool dirty = notes.Dirty && notes.Title.StartsWith("•");
        var findBox = (TextBox)notes.FindName("FindBox");
        var replaceBox = (TextBox)notes.FindName("ReplaceBox");
        findBox.Text = "alpha";
        replaceBox.Text = "omega";
        notes.ReplaceAll_Click(notes, new RoutedEventArgs());
        bool saved = notes.SaveTo(txt);
        string onDisk = File.ReadAllText(txt);
        Check("notes.edit-replace-save", dirty && saved && !notes.Dirty && onDisk == "omega beta\nomega gamma\ndelta\n",
            $"dirty marker {dirty}; saved {saved}; file now {onDisk.Replace("\n", "⏎")} (LF kept)");
        Snap(notes, "ui-30-notes");
        notes.Close();

        // ── Files ──
        File.WriteAllText(Path.Combine(root, "a.txt"), "hello");
        Directory.CreateDirectory(Path.Combine(root, "Sub"));
        var files = new FilesWindow(root);
        files.Show();
        await Wait(700);
        Check("files.list", files.Items.Count == 3 && files.Items[0].IsDir, $"{files.Items.Count} items: {string.Join(", ", files.Items.Select(i => i.Name))} (folders first)");

        files.NewFolder_Click(files, new RoutedEventArgs());
        files.NameInput.Text = "Made";
        files.NameOk_Click(files, new RoutedEventArgs());
        await Wait(200);
        bool made = Directory.Exists(Path.Combine(root, "Made"));
        var list = (ListBox)files.FindName("ItemsList");
        list.SelectedItem = files.Items.First(i => i.Name == "a.txt");
        files.Rename();
        files.NameInput.Text = "b.txt";
        files.NameOk_Click(files, new RoutedEventArgs());
        await Wait(200);
        bool renamed = File.Exists(Path.Combine(root, "b.txt")) && !File.Exists(Path.Combine(root, "a.txt"));
        files.Undo();
        await Wait(300);
        bool undone = File.Exists(Path.Combine(root, "a.txt"));
        Check("files.new-rename-undo", made && renamed && undone, $"new folder {made}; renamed a→b {renamed}; Ctrl+Z back to a.txt {undone}");

        list.SelectedItem = files.Items.First(i => i.Name == "a.txt");
        files.UpdatePreview();
        var preview = (StackPanel)files.FindName("PreviewContent");
        Check("files.preview", preview.Children.Count >= 4, $"preview pane shows {preview.Children.Count} parts for a.txt");
        files.ShowQuickLook();
        await Wait(300);
        Snap(files, "ui-31-files-quicklook");
        bool ql = files.QuickLookOpen;
        files.OpenTab(Path.Combine(root, "Sub"));
        await Wait(300);
        bool tabbed = files.TabCount == 2 && files.CurrentPath.EndsWith("Sub");
        Snap(files, "ui-32-files-tabs");
        files.CloseCurrentTab();
        await Wait(200);
        Check("files.quicklook-tabs", ql && tabbed && files.TabCount == 1 && files.CurrentPath == root, $"Quick Look opened {ql}; second tab on Sub {tabbed}; closed back to {Path.GetFileName(files.CurrentPath)}");

        files.Navigate(FilesWindow.ThisPC);
        await Wait(300);
        Check("files.thispc", files.Items.Count > 0 && files.Items.All(i => i.Used != null), $"{files.Items.Count} drives: {string.Join(", ", files.Items.Select(i => i.Name))}");
        files.Navigate(root);
        await files.DeepSearch("a.t");
        Check("files.deep-search", files.Items.Any(i => i.Name == "a.txt"), $"{files.Items.Count} result(s) for 'a.t'");
        files.Close();

        // ── Settings: every page builds ──
        var settings = new SettingsWindow(null);
        settings.Show();
        await Wait(400);
        var failed = new List<string>();
        foreach (var id in settings.PageIds.ToList())
        {
            settings.ShowPage(id);
            await Wait(150);
            if (settings.CurrentPage is not { } page || AllText(page).Any(t => t.Contains("couldn't load"))) failed.Add(id);
            if (id is "apps" or "about") Snap(settings, "ui-33-settings-" + id);
            if (id == "customise") { await Wait(500); Snap(settings, "ui-33-settings-customise"); }
        }
        Check("settings.pages", failed.Count == 0, failed.Count == 0 ? $"{settings.PageIds.Count()} pages built" : "failed: " + string.Join(", ", failed));
        settings.Close();

        // ── Screenshot ──
        var src = new RenderTargetBitmap(400, 300, 96, 96, PixelFormats.Pbgra32);
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen()) { dc.DrawRectangle(Brushes.DimGray, null, new Rect(0, 0, 400, 300)); dc.DrawRectangle(Brushes.White, null, new Rect(100, 50, 120, 80)); }
        src.Render(dv);
        src.Freeze();
        CaptureOverlay.TestDir = Path.Combine(root, "shots");
        CaptureOverlay.Start(testImage: src);
        await Wait(600);
        var overlay = CaptureOverlay.Open;
        Snap(overlay!, "ui-34-capture");
        double fx = 400 / overlay!.ActualWidth;
        overlay.TestCapture(new Rect(100 / fx, 50 / fx, 120 / fx, 80 / fx));
        await Wait(500);
        var shot = CaptureOverlay.LastSaved is { } s && File.Exists(s) ? BitmapFrame.Create(new Uri(s), BitmapCreateOptions.None, BitmapCacheOption.OnLoad) : null;
        Check("capture.rectangle", shot != null && Math.Abs(shot.PixelWidth - 120) <= 2 && Math.Abs(shot.PixelHeight - 80) <= 2 && CaptureToast.Current != null,
            shot == null ? "no file" : $"saved {Path.GetFileName(CaptureOverlay.LastSaved)} {shot.PixelWidth}×{shot.PixelHeight}; card shown {CaptureToast.Current != null}");
        if (CaptureToast.Current is { } toast) { Snap(toast, "ui-35-capture-card"); toast.Close(); }

        // ── Cursors and app icons render ──
        var arrow = NorrskenCursors.Render("Arrow", 48);
        var px = new byte[48 * 48 * 4];
        arrow.CopyPixels(px, 48 * 4, 0);
        int opaque = Enumerable.Range(0, 48 * 48).Count(i => px[i * 4 + 3] > 200);
        string ico = Path.Combine(root, "files.ico");
        NorrskenApps.WriteIcon(ico, "files");
        Check("skin.cursor-icon", opaque > 150 && new FileInfo(ico).Length > 1000, $"arrow cursor {opaque} opaque px at 48 px; icon {new FileInfo(ico).Length} bytes");
        // ── Alt+Tab ──
        AltTab.Begin(+1);
        await Wait(400);
        bool shown = AltTab.IsOpen; int first = AltTab.Index;
        AltTab.Step(+1);
        int second = AltTab.Index;
        AltTab.Cancel();
        Check("alttab.switcher", shown && first == 1 && second != first && !AltTab.IsOpen, $"switcher shown {shown}; starts on the previous window (index {first}); Tab moves to {second}; Esc closes");

        // ── Launcher search: apps + settings + files ──
        dock.ToggleLauncher(forceOpen: true);
        await Wait(400);
        var launcher = dock.Launcher!;
        ((TextBox)launcher.FindName("SearchBox")).Text = "storage";
        await Wait(1500);
        var kinds = launcher.Results.Select(r => r.Kind + ":" + r.Title).ToList();
        Snap(launcher, "ui-36-launcher-search");
        Check("launcher.search", kinds.Any(k => k.StartsWith("Setting:Storage")) && launcher.Results.Any(r => r.Title.StartsWith("Search for")), string.Join(" | ", kinds));
        launcher.HideLauncher();

        Info("icons.unframed", $"{Interop.ShellIcons.Unframed} app icons came back in a white frame and were replaced by the real icon");

        // ── Terminal: a real shell on ConPTY ──
        var term = new TerminalWindow(root);
        term.Show();
        await Wait(2500);
        term.Active?.Pty?.Write("echo norrsken-$((6*7))\r");
        string screenText = "";
        for (int i = 0; i < 40 && !screenText.Contains("norrsken-42"); i++)
        {
            await Wait(200);
            if (term.Active is { } tv) screenText = string.Join("\n", Enumerable.Range(0, tv.Screen.Rows).Select(r => TerminalScreen.TextOf(tv.Screen.Line(r), 0, tv.Screen.Cols)));
        }
        Snap(term, "ui-40-terminal");
        Check("terminal.shell", screenText.Contains("norrsken-42"), $"{term.Active?.Profile} ran `echo norrsken-$((6*7))` → output {(screenText.Contains("norrsken-42") ? "seen" : "missing")}");
        term.Close();

        // ── Task Manager ──
        var tm = new TaskManagerWindow();
        tm.Show();
        await Wait(3500);
        Snap(tm, "ui-41-taskmgr");
        int procGroups = tm.GroupCount;
        tm.Show("performance");
        await Wait(1600);
        Snap(tm, "ui-42-taskmgr-perf");
        tm.Show("startup");
        await Wait(300);
        Snap(tm, "ui-43-taskmgr-startup");
        Check("taskmgr.lists", procGroups > 10, $"{procGroups} programs listed with live CPU and memory");
        tm.Close();

        // ── Bin (a fake bin in the test folder: the real Recycle Bin is never touched) ──
        await BinFlow();
        await FlickerboxFlow();
        // ── Run, clipboard, overview, power, display mode ──
        RunWindow.Toggle();
        await Wait(500);
        foreach (Window w in Application.Current.Windows) if (w is RunWindow rw) Snap(rw, "ui-44-run");
        foreach (Window w in Application.Current.Windows.Cast<Window>().ToList()) if (w is RunWindow rw2) rw2.Close();
        ClipboardHistory.TestAdd("git commit -m \"Norrsken\"");
        ClipboardHistory.TestAdd("https://claude.ai");
        ClipboardWindow.Toggle();
        await Wait(500);
        if (ClipboardWindow.Open is { } cw) { Snap(cw, "ui-45-clipboard"); Check("clipboard.history", cw.Count >= 2, $"{cw.Count} entries shown"); cw.Close(); }
        AltTab.ShowOverview();
        await Wait(600);
        bool overview = AltTab.IsOpen && KeyboardHook.OverviewOpen;
        foreach (Window w in Application.Current.Windows) if (w is AltTab at) Snap(at, "ui-46-overview");
        AltTab.Cancel();
        Check("overview.open", overview && !AltTab.IsOpen && !KeyboardHook.OverviewOpen, $"Win+Tab overview opened {overview}; Esc closed it");
        PowerDialog.Open();
        await Wait(400);
        foreach (Window w in Application.Current.Windows.Cast<Window>().ToList()) if (w is PowerDialog pd) { Snap(pd, "ui-47-power"); pd.Close(); }
        ProjectWindow.Toggle();
        await Wait(400);
        foreach (Window w in Application.Current.Windows.Cast<Window>().ToList()) if (w is ProjectWindow pw) { Snap(pw, "ui-48-project"); pw.Close(); }

        // ── Now playing in the dock ──
        dock.ApplyMedia(new MediaController.State("How Are Saw Teeth Made?", "Alec Steele", "Chrome", true, null));
        await Wait(500);
        bool mediaOn = dock.MediaShown;
        Snap(dock, "ui-38-dock-media");
        dock.ApplyMedia(null);
        await Wait(200);
        Check("dock.media", mediaOn && !dock.MediaShown, $"now playing shown with a track {mediaOn}; gone when nothing plays {!dock.MediaShown}");

        // ── Notification centre ──
        Notifications.TestAdd("Discord", "Lisa", "are you on tonight?", 3);
        Notifications.TestAdd("Discord", "Erik", "gg", 1);
        Notifications.TestAdd("Steam", "Download complete", "Counter-Strike 2 is ready to play", 20);
        NotificationCentre.Toggle();
        await Wait(600);
        var centre = NotificationCentre.Current;
        int groups = centre?.GroupCount ?? -1;
        NotificationCentre.StartFocus(25);
        string? remaining = NotificationCentre.FocusRemaining;
        NotificationCentre.StopFocus();
        centre?.OpenMonth();
        NotificationCentre.StartFocus(25);
        await Wait(200);
        if (centre != null) Snap(centre, "ui-37-notifications");
        NotificationCentre.StopFocus();
        Check("notifications.centre", centre != null && groups == 2 && remaining is { Length: > 0 } && NotificationCentre.FocusRemaining == null,
            $"open {centre != null}; {groups} app groups for 3 notifications; focus timer showed {remaining} then stopped");
        Notifications.ClearAll();
        centre?.Close();
        Info("defaults.formats", $"{MediaDefaults.FormatCount} file types assigned (Flickerbox media + Notes text)");

        FrameStyler.Start();
        Info("skin.frames", string.Join("; ", FrameStyler.TestLog));
    }

    static IEnumerable<string> AllText(DependencyObject root)
    {
        if (root is TextBlock tb) yield return tb.Text;
        int n = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < n; i++)
            foreach (var t in AllText(VisualTreeHelper.GetChild(root, i))) yield return t;
    }

    static async Task BinFlow()
    {
        string bin = Path.Combine(_out, "fakebin"), home = Path.Combine(_out, "binhome");
        Directory.CreateDirectory(bin);
        Directory.CreateDirectory(home);
        int n = 0;
        void Make(string original, string content, TimeSpan ago, bool folder = false)
        {
            string id = $"NRS{++n:D3}" + (folder ? "" : Path.GetExtension(original));
            string data = Path.Combine(bin, "$R" + id), info = Path.Combine(bin, "$I" + id);
            if (folder) { Directory.CreateDirectory(data); File.WriteAllText(Path.Combine(data, "inside.txt"), content); }
            else File.WriteAllText(data, content);
            using var w = new BinaryWriter(File.Create(info));
            w.Write(2L); w.Write((long)content.Length); w.Write((DateTime.Now - ago).ToFileTime());
            w.Write(original.Length + 1); w.Write(System.Text.Encoding.Unicode.GetBytes(original + "\0"));
        }
        Make(Path.Combine(home, "Docs", "report.txt"), "quarterly", TimeSpan.FromHours(30));      // its folder is gone
        Make(Path.Combine(home, "notes.md"), "# notes", TimeSpan.FromSeconds(20));                   // these two were one delete…
        Make(Path.Combine(home, "todo.txt"), "milk", TimeSpan.FromSeconds(19));                      // …so undo brings both
        Make(Path.Combine(home, "old.log"), "ancient", TimeSpan.FromDays(40));
        Make(Path.Combine(home, "Project"), "secret plans", TimeSpan.FromDays(2), folder: true);
        Bin.TestRoot = bin;
        int keepDays = App.Settings.BinAutoEmptyDays;
        try
        {
            var items = Bin.List();
            var win = new BinWindow();
            win.Show();
            await Wait(700);
            win.TestSelectFirst();
            await Wait(250);
            Snap(win, "ui-49-bin");
            Check("bin.list", items.Count == 5 && win.RowCount == 5, $"{items.Count} items read from $I files; {win.RowCount} rows grouped by day");

            var report = items.First(i => i.Name == "report.txt");
            string back = Bin.Restore(report);
            Check("bin.restore-rebuilds-folder", File.ReadAllText(back) == "quarterly" && back == Path.Combine(home, "Docs", "report.txt") && !File.Exists(report.InfoPath),
                $"put back at {Path.GetRelativePath(_out, back)}; the missing Docs folder was rebuilt");

            var undone = Bin.UndoLastDelete();
            Check("bin.undo-last-delete", undone.Count == 2 && File.Exists(Path.Combine(home, "notes.md")) && File.Exists(Path.Combine(home, "todo.txt")),
                $"undo brought back {undone.Count} items deleted together: {string.Join(", ", undone.Select(Path.GetFileName))}");

            var old = Bin.List().First(i => i.Name == "old.log");
            Bin.SetKept(old, true);
            App.Settings.BinAutoEmptyDays = 30;
            int keptRun = Bin.AutoEmpty();
            Bin.SetKept(old, false);
            int freeRun = Bin.AutoEmpty();
            Check("bin.keep-and-auto-empty", keptRun == 0 && freeRun == 1 && !File.Exists(old.DataPath),
                $"kept item survived the 30-day clean-up ({keptRun} removed); once released it went ({freeRun} removed)");

            var project = Bin.List().First(i => i.IsFolder);
            Bin.DeleteForever(project, shred: true);
            Check("bin.shred", !Directory.Exists(project.DataPath) && !File.Exists(project.InfoPath) && Bin.List().Count == 0,
                "folder shredded (overwritten, then removed); the Bin is empty");
            await Wait(500);
            Snap(win, "ui-50-bin-empty");
            win.Close();
        }
        finally
        {
            App.Settings.BinAutoEmptyDays = keepDays;
            Bin.TestRoot = null;
        }
    }

    // ── Flickerbox: starts, opens its window and draws (on this hidden desktop, like everything here) ──
    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool EnumWindows(EnumProc cb, IntPtr l);
    delegate bool EnumProc(IntPtr h, IntPtr l);
    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out RECT r);
    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool PrintWindow(IntPtr h, IntPtr hdc, uint flags);
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)] struct RECT { public int L, T, R, B; }

    static async Task FlickerboxFlow()
    {
        string bundled = Path.Combine(MediaDefaults.Bundled, "Flickerbox.exe");
        string exe = File.Exists(bundled) ? bundled : MediaDefaults.Exe;
        if (!File.Exists(exe)) { Info("flickerbox.run", "no Flickerbox build here to start"); return; }
        Check("flickerbox.webview2", MediaDefaults.WebView2Installed(), "the WebView2 runtime Flickerbox draws with is installed");
        var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(exe) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(exe)! })!;
        try
        {
            IntPtr win = IntPtr.Zero;
            for (int i = 0; i < 40 && win == IntPtr.Zero && !p.HasExited; i++)
            {
                await Wait(250);
                EnumWindows((h, _) =>
                {
                    GetWindowThreadProcessId(h, out uint pid);
                    if (pid == p.Id && IsWindowVisible(h) && GetWindowRect(h, out var r) && r.R - r.L > 300) { win = h; return false; }
                    return true;
                }, IntPtr.Zero);
            }
            await Wait(2500); // WebView2 and the interface load
            bool alive = !p.HasExited;
            int painted = 0;
            if (win != IntPtr.Zero && GetWindowRect(win, out var rc))
            {
                int w = rc.R - rc.L, h = rc.B - rc.T;
                using var bmp = new System.Drawing.Bitmap(w, h);
                using (var g = System.Drawing.Graphics.FromImage(bmp)) { var hdc = g.GetHdc(); PrintWindow(win, hdc, 2 /* PW_RENDERFULLCONTENT */); g.ReleaseHdc(hdc); }
                bmp.Save(Path.Combine(_out, "ui-51-flickerbox.png"), System.Drawing.Imaging.ImageFormat.Png);
                // Count distinct-looking pixels on a grid: a blank or black window has almost none.
                var seen = new HashSet<int>();
                for (int y = 4; y < h; y += Math.Max(1, h / 40)) for (int x = 4; x < w; x += Math.Max(1, w / 40)) seen.Add(bmp.GetPixel(x, y).ToArgb() & 0xF0F0F0);
                painted = seen.Count;
            }
            Check("flickerbox.run", alive && win != IntPtr.Zero && painted > 4,
                $"{(exe == bundled ? "bundled" : "installed")} Flickerbox running {alive}; window found {win != IntPtr.Zero}; {painted} distinct colours drawn");
        }
        finally { try { if (!p.HasExited) p.Kill(true); } catch (Exception) { } }
    }}
