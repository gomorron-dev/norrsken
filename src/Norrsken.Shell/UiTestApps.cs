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
}
