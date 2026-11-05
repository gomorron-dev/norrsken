using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Norrsken.Shell.Core;
using Norrsken.Shell.Interop;

namespace Norrsken.Shell.Ui;

/// <summary>
/// The Bin: everything you've deleted, grouped by when, with a preview of the selected item.
/// Restore puts things back (rebuilding a folder that's gone), Undo brings back the last delete in one go,
/// Keep protects an item from auto-emptying, and deleting forever can shred the data first.
/// Destructive buttons ask twice, inline, instead of popping a dialog.
/// </summary>
public sealed class BinWindow : Window
{
    readonly Border _root = new();
    readonly StackPanel _list = new();
    readonly ScrollViewer _scroll = new() { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Focusable = false };
    readonly ContentControl _preview = new();
    readonly TextBlock _summary = Txt("", 12.5, dim: true);
    readonly TextBlock _status = Txt("", 12.5, dim: true);
    readonly TextBox _search = new();
    readonly ComboBox _sort = new() { Width = 150, Margin = new Thickness(8, 0, 0, 0) };
    readonly Button _undo, _empty;
    readonly DispatcherTimer _disarm = new() { Interval = TimeSpan.FromSeconds(3) };
    readonly Dictionary<string, Border> _rows = new();
    List<Bin.Item> _items = [];
    readonly List<string> _selected = [];
    string? _anchor;
    Button? _armed;
    bool _busy;

    public static void Open(bool armEmpty = false)
    {
        var w = new BinWindow();
        w.Show();
        if (armEmpty) w.Arm(w._empty, "Click again to empty the Bin");
    }

    public BinWindow()
    {
        Title = "Bin";
        Width = 1000; Height = 660; MinWidth = 700; MinHeight = 420;
        WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.CanResize; WindowStartupLocation = WindowStartupLocation.CenterScreen;
        FontFamily = (FontFamily)FindResource("UiFont");
        SetResourceReference(BackgroundProperty, "SurfaceSolid");
        System.Windows.Shell.WindowChrome.SetWindowChrome(this, new System.Windows.Shell.WindowChrome { CaptionHeight = 56, ResizeBorderThickness = new Thickness(6), GlassFrameThickness = new Thickness(0), UseAeroCaptionButtons = false });

        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(56) });
        grid.RowDefinitions.Add(new RowDefinition());
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        // ── title bar: name and totals, search, sort, undo, empty
        var bar = new DockPanel { Margin = new Thickness(18, 0, 14, 0) };
        var pebbles = new WindowPebbles { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0) };
        DockPanel.SetDock(pebbles, Dock.Right);
        bar.Children.Add(pebbles);
        var tools = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        System.Windows.Shell.WindowChrome.SetIsHitTestVisibleInChrome(tools, true);
        tools.Children.Add(SearchBox());
        foreach (var s in new[] { "Newest first", "Largest first", "Name", "Where it was" }) _sort.Items.Add(s);
        _sort.SelectedIndex = 0;
        _sort.SelectionChanged += (_, _) => Render();
        tools.Children.Add(_sort);
        _undo = TextButton("Undo last delete", "restore");
        _undo.ToolTip = "Bring back everything you deleted last (Ctrl+Z)";
        _undo.Click += async (_, _) => await UndoLast();
        tools.Children.Add(_undo);
        _empty = new Button { Style = (Style)FindResource("PrimaryButton"), Content = "Empty Bin", FontSize = 12.5, Margin = new Thickness(8, 0, 0, 0) };
        _empty.Click += async (_, _) => { if (Confirmed(_empty, "Click again to empty the Bin")) await EmptyAll(); };
        tools.Children.Add(_empty);
        DockPanel.SetDock(tools, Dock.Right);
        bar.Children.Add(tools);
        var head = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        head.Children.Add(new GlyphIcon { Glyph = "trash", Width = 20, Height = 20, Margin = new Thickness(0, 0, 10, 0) });
        var title = Txt("Bin", 17, bold: true);
        title.FontFamily = (FontFamily)FindResource("DisplayFont");
        head.Children.Add(title);
        _summary.Margin = new Thickness(12, 2, 0, 0);
        head.Children.Add(_summary);
        bar.Children.Add(head);
        grid.Children.Add(bar);

        // ── body: the list and the preview
        var body = new Grid();
        Grid.SetRow(body, 1);
        body.ColumnDefinitions.Add(new ColumnDefinition());
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(300) });
        _list.Margin = new Thickness(12, 4, 12, 16);
        _scroll.Content = _list;
        body.Children.Add(_scroll);
        var side = new Border { BorderThickness = new Thickness(1, 0, 0, 0), Child = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Focusable = false, Content = new Border { Padding = new Thickness(20, 16, 20, 16), Child = _preview } } };
        side.SetResourceReference(Border.BorderBrushProperty, "Border");
        side.SetResourceReference(Border.BackgroundProperty, "SurfaceAlt");
        Grid.SetColumn(side, 1);
        body.Children.Add(side);
        grid.Children.Add(body);

        // ── footer: the house rules
        var foot = new Border { BorderThickness = new Thickness(0, 1, 0, 0), Padding = new Thickness(18, 0, 18, 0), Height = 50 };
        foot.SetResourceReference(Border.BorderBrushProperty, "Border");
        Grid.SetRow(foot, 2);
        var footDock = new DockPanel { VerticalAlignment = VerticalAlignment.Center };
        DockPanel.SetDock(_status, Dock.Right);
        footDock.Children.Add(_status);
        var rules = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        rules.Children.Add(Txt("Empty automatically", 12.5, dim: true));
        var auto = new ComboBox { Width = 150, Margin = new Thickness(10, 0, 24, 0) };
        var days = new[] { 0, 7, 30, 90 };
        foreach (var d in days) auto.Items.Add(d == 0 ? "Never" : $"After {d} days");
        auto.SelectedIndex = Math.Max(0, Array.IndexOf(days, App.Settings.BinAutoEmptyDays));
        auto.SelectionChanged += (_, _) => { App.Settings.BinAutoEmptyDays = days[auto.SelectedIndex]; App.Settings.Save(); UpdateTotals(); };
        rules.Children.Add(auto);
        var shred = new CheckBox { Style = (Style)FindResource("Toggle"), IsChecked = App.Settings.BinShred, Content = "Shred when deleting forever", Padding = new Thickness(0), VerticalAlignment = VerticalAlignment.Center };
        shred.ToolTip = "Overwrite the data with noise before it's removed, so recovery tools can't bring it back";
        shred.Checked += (_, _) => { App.Settings.BinShred = true; App.Settings.Save(); };
        shred.Unchecked += (_, _) => { App.Settings.BinShred = false; App.Settings.Save(); };
        rules.Children.Add(shred);
        footDock.Children.Add(rules);
        foot.Child = footDock;
        grid.Children.Add(foot);

        _root.Child = grid;
        _root.BorderThickness = new Thickness(1);
        _root.SetResourceReference(Border.BorderBrushProperty, "Border");
        _root.SetResourceReference(Border.BackgroundProperty, "SurfaceSolid");
        _root.SetResourceReference(TextElement.ForegroundProperty, "Text");
        Content = _root;

        _disarm.Tick += (_, _) => Disarm();
        PreviewKeyDown += OnKey;
        Bin.Changed += OnBinChanged;
        Closed += (_, _) => Bin.Changed -= OnBinChanged;
        Loaded += (_, _) => { Refresh(); _search.Focus(); };
        AppChrome.Attach(this, _root);
    }

    void OnBinChanged() { if (!_busy) Refresh(); }

    internal int RowCount => _rows.Count;
    internal void TestSelectFirst() { if (Visible().FirstOrDefault() is { } i) Select(i, ModifierKeys.None); }

    // ───────────────────────── data ─────────────────────────

    internal void Refresh()
    {
        _items = App.TestMode && Bin.TestRoot == null ? [] : Bin.List();
        _selected.RemoveAll(id => _items.All(i => i.Id != id));
        UpdateTotals();
        Render();
    }

    void UpdateTotals()
    {
        long bytes = _items.Sum(i => i.Size);
        _summary.Text = _items.Count == 0 ? "Empty" : $"{_items.Count} item{(_items.Count == 1 ? "" : "s")}  ·  {Bin.Size(bytes)}";
        int days = App.Settings.BinAutoEmptyDays;
        long freed = Bin.FreedThisMonth;
        _status.Text = freed > 0 ? $"You've freed {Bin.Size(freed)} this month" : days > 0 ? $"Items leave after {days} days unless you keep them" : "";
        _undo.IsEnabled = _empty.IsEnabled = _items.Count > 0;
    }

    IEnumerable<Bin.Item> Visible()
    {
        string q = _search.Text.Trim();
        var items = _items.Where(i => q.Length == 0 || i.Name.Contains(q, StringComparison.OrdinalIgnoreCase) || i.OriginalPath.Contains(q, StringComparison.OrdinalIgnoreCase));
        return _sort.SelectedIndex switch
        {
            1 => items.OrderByDescending(i => i.Size),
            2 => items.OrderBy(i => i.Name, StringComparer.CurrentCultureIgnoreCase),
            3 => items.OrderBy(i => i.FromFolder, StringComparer.OrdinalIgnoreCase).ThenBy(i => i.Name),
            _ => items.OrderByDescending(i => i.Deleted),
        };
    }

    static string Group(DateTime d)
    {
        var today = DateTime.Today;
        if (d >= today) return "Today";
        if (d >= today.AddDays(-1)) return "Yesterday";
        if (d >= today.AddDays(-7)) return "This week";
        if (d >= today.AddDays(-31)) return "This month";
        return "Earlier";
    }

    // ───────────────────────── list ─────────────────────────

    void Render()
    {
        _list.Children.Clear();
        _rows.Clear();
        var items = Visible().ToList();
        if (_items.Count == 0) { _list.Children.Add(EmptyState("The Bin is empty", "Things you delete wait here until you empty it.\nCtrl+Z in the Bin brings back whatever you deleted last.")); RenderPreview(); return; }
        if (items.Count == 0) { _list.Children.Add(EmptyState("No matches", $"Nothing in the Bin matches “{_search.Text.Trim()}”.")); RenderPreview(); return; }

        string? group = null;
        int shown = 0;
        foreach (var item in items)
        {
            if (shown == 400) { _list.Children.Add(Note($"…and {items.Count - 400} more. Search to find them.")); break; }
            if (_sort.SelectedIndex == 0 && Group(item.Deleted) is var g && g != group)
            {
                group = g;
                var h = Txt(g, 12, bold: true, dim: true);
                h.Margin = new Thickness(10, _list.Children.Count == 0 ? 6 : 18, 0, 6);
                _list.Children.Add(h);
            }
            _list.Children.Add(Row(item));
            shown++;
        }
        Motion.Stagger(_list.Children.Cast<UIElement>(), stepMs: 18, max: 14, rise: 6);
        RenderPreview();
    }

    Border Row(Bin.Item item)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(42) });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(84) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(96) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(112) });

        grid.Children.Add(IconFor(item, 28));
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 12, 0) };
        var nameRow = new StackPanel { Orientation = Orientation.Horizontal };
        nameRow.Children.Add(Txt(item.Name, 13.5));
        if (Bin.IsKept(item)) nameRow.Children.Add(new GlyphIcon { Glyph = "pin", Width = 13, Height = 13, Margin = new Thickness(6, 1, 0, 0), ToolTip = "Kept: emptying automatically leaves it alone" });
        text.Children.Add(nameRow);
        bool folderGone = !Directory.Exists(item.FromFolder);
        text.Children.Add(Txt(Pretty(item.FromFolder) + (folderGone ? "  ·  folder no longer there" : ""), 11.5, dim: true));
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);
        var size = Txt(item.IsFolder ? $"{Bin.Size(item.Size)}" : Bin.Size(item.Size), 12, dim: true);
        size.HorizontalAlignment = HorizontalAlignment.Right;
        size.Margin = new Thickness(0, 0, 14, 0);
        Grid.SetColumn(size, 2);
        grid.Children.Add(size);
        var when = Txt(Ago(item.Deleted), 12, dim: true);
        when.ToolTip = item.Deleted.ToString("f", CultureInfo.CurrentCulture);
        Grid.SetColumn(when, 3);
        grid.Children.Add(when);

        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Opacity = 0 };
        actions.Children.Add(Tiny("restore", "Put it back", async () => await RestoreItems([item], null)));
        actions.Children.Add(Tiny("pin", Bin.IsKept(item) ? "Stop keeping it" : "Keep it (emptying automatically leaves it alone)", () => { Bin.SetKept(item, !Bin.IsKept(item)); Render(); }));
        var forever = Tiny("cross", "Delete forever", () => { });
        forever.Click += async (_, _) => { if (Confirmed(forever, null)) await DeleteItems([item]); };
        actions.Children.Add(forever);
        Grid.SetColumn(actions, 4);
        grid.Children.Add(actions);

        var row = new Border { CornerRadius = Theme.R(10), Padding = new Thickness(8, 7, 8, 7), Margin = new Thickness(0, 1, 0, 1), Background = Brushes.Transparent, Child = grid, Tag = item };
        void Paint()
        {
            bool sel = _selected.Contains(item.Id);
            if (sel) row.SetResourceReference(Border.BackgroundProperty, "Selection");
            else if (row.IsMouseOver) row.SetResourceReference(Border.BackgroundProperty, "Hover");
            else row.Background = Brushes.Transparent;
            Motion.To(actions, OpacityProperty, sel || row.IsMouseOver ? 1 : 0, 140);
        }
        row.MouseEnter += (_, _) => Paint();
        row.MouseLeave += (_, _) => Paint();
        row.MouseLeftButtonDown += (_, e) =>
        {
            if (e.ClickCount == 2) { _ = RestoreItems([item], null); return; }
            Select(item, Keyboard.Modifiers);
        };
        row.MouseRightButtonUp += (_, _) => { if (!_selected.Contains(item.Id)) Select(item, ModifierKeys.None); RowMenu(); };
        _rows[item.Id] = row;
        row.Loaded += (_, _) => Paint();
        return row;
    }

    void Select(Bin.Item item, ModifierKeys mods)
    {
        var order = Visible().Select(i => i.Id).ToList();
        if (mods.HasFlag(ModifierKeys.Shift) && _anchor != null && order.Contains(_anchor))
        {
            int a = order.IndexOf(_anchor), b = order.IndexOf(item.Id);
            _selected.Clear();
            _selected.AddRange(order.Skip(Math.Min(a, b)).Take(Math.Abs(a - b) + 1));
        }
        else if (mods.HasFlag(ModifierKeys.Control))
        {
            if (!_selected.Remove(item.Id)) _selected.Add(item.Id);
            _anchor = item.Id;
        }
        else { _selected.Clear(); _selected.Add(item.Id); _anchor = item.Id; }
        foreach (var (id, row) in _rows)
        {
            bool sel = _selected.Contains(id);
            if (sel) row.SetResourceReference(Border.BackgroundProperty, "Selection");
            else if (row.IsMouseOver) row.SetResourceReference(Border.BackgroundProperty, "Hover");
            else row.Background = Brushes.Transparent;
            if (row.Child is Grid g && g.Children[^1] is UIElement actions) Motion.To(actions, OpacityProperty, sel || row.IsMouseOver ? 1 : 0, 140);
        }
        RenderPreview();
    }

    List<Bin.Item> Selection() => _items.Where(i => _selected.Contains(i.Id)).ToList();

    void RowMenu()
    {
        var sel = Selection();
        if (sel.Count == 0) return;
        var m = new ContextMenu();
        void Item(string h, Action a, bool enabled = true) { var mi = new MenuItem { Header = h, IsEnabled = enabled }; mi.Click += (_, _) => a(); m.Items.Add(mi); }
        Item(sel.Count == 1 ? "Put back" : $"Put back {sel.Count} items", () => _ = RestoreItems(sel, null));
        Item("Put back in…", () => _ = RestoreTo(sel));
        if (sel.Count == 1) Item("Show where it was", () => Reveal(sel[0].FromFolder), Directory.Exists(sel[0].FromFolder));
        m.Items.Add(new Separator());
        bool allKept = sel.All(Bin.IsKept);
        Item(allKept ? "Stop keeping" : "Keep (never emptied automatically)", () => { foreach (var i in sel) Bin.SetKept(i, !allKept); Render(); });
        m.Items.Add(new Separator());
        Item(App.Settings.BinShred ? "Shred forever" : "Delete forever", () => _ = DeleteItems(sel));
        m.IsOpen = true;
    }

    // ───────────────────────── preview ─────────────────────────

    void RenderPreview()
    {
        var sel = Selection();
        var stack = new StackPanel();
        if (sel.Count == 0)
        {
            long bytes = _items.Sum(i => i.Size);
            stack.Children.Add(new GlyphIcon { Glyph = _items.Count == 0 ? "trash" : "trash-full", Width = 64, Height = 64, Margin = new Thickness(0, 30, 0, 18), Opacity = 0.8 });
            var t = Txt(_items.Count == 0 ? "Nothing here" : $"{_items.Count} item{(_items.Count == 1 ? "" : "s")}", 18, bold: true);
            t.HorizontalAlignment = HorizontalAlignment.Center;
            stack.Children.Add(t);
            var s = Txt(_items.Count == 0 ? "" : $"{Bin.Size(bytes)} you can get back", 12.5, dim: true);
            s.HorizontalAlignment = HorizontalAlignment.Center;
            stack.Children.Add(s);
            if (_items.Count > 0)
            {
                var biggest = _items.OrderByDescending(i => i.Size).Take(3).ToList();
                var head = Txt("Biggest", 12, bold: true, dim: true);
                head.Margin = new Thickness(0, 28, 0, 8);
                stack.Children.Add(head);
                foreach (var b in biggest)
                {
                    var line = new DockPanel { Margin = new Thickness(0, 0, 0, 6) };
                    var sz = Txt(Bin.Size(b.Size), 12, dim: true);
                    DockPanel.SetDock(sz, Dock.Right);
                    line.Children.Add(sz);
                    line.Children.Add(Txt(b.Name, 12.5));
                    stack.Children.Add(line);
                }
                stack.Children.Add(Note("Select something to see it here. Double-click puts it back."));
            }
            _preview.Content = stack;
            Motion.From(stack, OpacityProperty, 0, 1, 180);
            return;
        }
        if (sel.Count > 1)
        {
            stack.Children.Add(new GlyphIcon { Glyph = "trash-full", Width = 56, Height = 56, Margin = new Thickness(0, 30, 0, 16) });
            var t = Txt($"{sel.Count} items", 18, bold: true);
            t.HorizontalAlignment = HorizontalAlignment.Center;
            stack.Children.Add(t);
            var s = Txt(Bin.Size(sel.Sum(i => i.Size)), 12.5, dim: true);
            s.HorizontalAlignment = HorizontalAlignment.Center;
            stack.Children.Add(s);
            stack.Children.Add(Actions(sel));
            _preview.Content = stack;
            return;
        }

        var item = sel[0];
        var art = new Border { Height = 150, CornerRadius = Theme.R(12), Margin = new Thickness(0, 4, 0, 16), ClipToBounds = true };
        art.SetResourceReference(Border.BackgroundProperty, "SurfaceSolid");
        ImageSource? thumb = item.IsFolder ? null : ShellIcons.Thumbnail(item.DataPath, 256);
        art.Child = thumb != null
            ? new Image { Source = thumb, Stretch = Stretch.Uniform, Margin = new Thickness(10) }
            : IconFor(item, 80);
        stack.Children.Add(art);
        var name = Txt(item.Name, 15, bold: true);
        name.TextWrapping = TextWrapping.Wrap;
        stack.Children.Add(name);
        stack.Children.Add(Fact("Was in", Pretty(item.FromFolder) + (Directory.Exists(item.FromFolder) ? "" : " (gone, it'll be rebuilt)")));
        stack.Children.Add(Fact("Size", Bin.Size(item.Size) + (item.IsFolder ? "  ·  folder" : "")));
        stack.Children.Add(Fact("Deleted", item.Deleted.ToString("dddd d MMMM, HH:mm", CultureInfo.CurrentCulture) + "  ·  " + Ago(item.Deleted)));
        int days = App.Settings.BinAutoEmptyDays;
        if (Bin.IsKept(item)) stack.Children.Add(Fact("Kept", "Emptying automatically leaves it alone"));
        else if (days > 0) stack.Children.Add(Fact("Leaves", Math.Max(0, days - (int)(DateTime.Now - item.Deleted).TotalDays) is var left && left == 0 ? "at the next clean-up" : $"in {left} day{(left == 1 ? "" : "s")}"));
        stack.Children.Add(Actions(sel));
        _preview.Content = stack;
        Motion.From(stack, OpacityProperty, 0, 1, 160);
    }

    StackPanel Actions(List<Bin.Item> sel)
    {
        var panel = new StackPanel { Margin = new Thickness(0, 18, 0, 0) };
        var restore = new Button { Style = (Style)FindResource("PrimaryButton"), Content = sel.Count == 1 ? "Put back" : $"Put back {sel.Count} items", Margin = new Thickness(0, 0, 0, 8), HorizontalAlignment = HorizontalAlignment.Stretch };
        restore.Click += async (_, _) => await RestoreItems(sel, null);
        panel.Children.Add(restore);
        var to = TextButton("Put back in…", "reveal");
        to.HorizontalAlignment = HorizontalAlignment.Stretch;
        to.Margin = new Thickness(0, 0, 0, 8);
        to.Click += async (_, _) => await RestoreTo(sel);
        panel.Children.Add(to);
        bool kept = sel.All(Bin.IsKept);
        var keep = TextButton(kept ? "Stop keeping" : "Keep", "pin");
        keep.HorizontalAlignment = HorizontalAlignment.Stretch;
        keep.Margin = new Thickness(0, 0, 0, 8);
        keep.ToolTip = "Kept items are never removed by emptying automatically";
        keep.Click += (_, _) => { foreach (var i in sel) Bin.SetKept(i, !kept); Render(); };
        panel.Children.Add(keep);
        var forever = TextButton(App.Settings.BinShred ? "Shred forever" : "Delete forever", App.Settings.BinShred ? "shred" : "cross");
        forever.HorizontalAlignment = HorizontalAlignment.Stretch;
        forever.Click += async (_, _) => { if (Confirmed(forever, sel.Count == 1 ? "Click again: it can't come back" : $"Click again: {sel.Count} items, gone for good")) await DeleteItems(sel); };
        panel.Children.Add(forever);
        return panel;
    }

    static StackPanel Fact(string label, string value)
    {
        var s = new StackPanel { Margin = new Thickness(0, 10, 0, 0) };
        s.Children.Add(Txt(label, 11.5, dim: true));
        var v = Txt(value, 12.5);
        v.TextWrapping = TextWrapping.Wrap;
        v.TextTrimming = TextTrimming.None;
        s.Children.Add(v);
        return s;
    }

    // ───────────────────────── actions ─────────────────────────

    async Task RestoreItems(List<Bin.Item> items, string? folder)
    {
        if (items.Count == 0) return;
        await Animate(items, toRight: false);
        _busy = true;
        var placed = await Task.Run(() =>
        {
            var done = new List<string>();
            foreach (var i in items) try { done.Add(Bin.Restore(i, folder)); } catch (Exception ex) { Log.Write($"Bin restore {i.Name}: {ex.Message}"); }
            return done;
        });
        _busy = false;
        _selected.Clear();
        Refresh();
        Toast(placed.Count == 0 ? "Couldn't put that back" : placed.Count == 1 ? $"Put back in {Pretty(Path.GetDirectoryName(placed[0]) ?? "")}" : $"Put back {placed.Count} items",
              placed.Count > 0 ? () => Reveal(Path.GetDirectoryName(placed[0])!) : null);
    }

    async Task RestoreTo(List<Bin.Item> items)
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog { Title = "Put back in…", InitialDirectory = items[0].FromFolder };
        if (dlg.ShowDialog(this) == true) await RestoreItems(items, dlg.FolderName);
    }

    async Task DeleteItems(List<Bin.Item> items)
    {
        await Animate(items, toRight: true);
        _busy = true;
        bool shred = App.Settings.BinShred;
        long bytes = items.Sum(i => i.Size);
        await Task.Run(() => { foreach (var i in items) try { Bin.DeleteForever(i, shred); } catch (Exception ex) { Log.Write($"Bin delete {i.Name}: {ex.Message}"); } });
        _busy = false;
        _selected.Clear();
        Refresh();
        Toast($"{(shred ? "Shredded" : "Deleted")} {items.Count} item{(items.Count == 1 ? "" : "s")} for good  ·  {Bin.Size(bytes)} freed", null);
    }

    async Task UndoLast()
    {
        var batch = Bin.LastBatch();
        if (batch.Count == 0) return;
        await RestoreItems(batch, null);
    }

    async Task EmptyAll()
    {
        long bytes = _items.Sum(i => i.Size);
        int n = _items.Count;
        foreach (var row in _rows.Values.Take(14)) Motion.To(row, OpacityProperty, 0, 220);
        _busy = true;
        bool shred = App.Settings.BinShred;
        await Task.Run(() => Bin.Empty(shred));
        _busy = false;
        _selected.Clear();
        Refresh();
        Toast($"Emptied: {n} item{(n == 1 ? "" : "s")}, {Bin.Size(bytes)} freed", null);
    }

    /// <summary>Rows leave the list before the work starts: left when they go home, right when they go for good.</summary>
    async Task Animate(List<Bin.Item> items, bool toRight)
    {
        if (Motion.Reduced) return;
        foreach (var i in items)
            if (_rows.TryGetValue(i.Id, out var row))
            {
                var shift = new TranslateTransform();
                row.RenderTransform = shift;
                Motion.To(shift, TranslateTransform.XProperty, toRight ? 40 : -40, 220, Motion.InOut);
                Motion.To(row, OpacityProperty, 0, 200);
            }
        await Task.Delay(200);
    }

    void Toast(string text, Action? show)
    {
        _status.Inlines.Clear();
        _status.Inlines.Add(new Run(text));
        if (show != null)
        {
            var link = new Hyperlink(new Run("Show")) { TextDecorations = null };
            link.SetResourceReference(TextElement.ForegroundProperty, "Text");
            link.Click += (_, _) => show();
            _status.Inlines.Add(new Run("   "));
            _status.Inlines.Add(link);
        }
        Motion.From(_status, OpacityProperty, 0, 1, 200);
    }

    static void Reveal(string folder)
    {
        if (Directory.Exists(folder)) NorrskenApps.Launch("files", folder);
    }

    // ───────────────────────── two-step buttons ─────────────────────────

    /// <summary>First click arms the button (its label asks again); a second click within three seconds goes ahead.</summary>
    bool Confirmed(Button b, string? ask)
    {
        if (_armed == b) { Disarm(); return true; }
        Arm(b, ask ?? "Click again");
        return false;
    }

    void Arm(Button b, string ask)
    {
        Disarm();
        _armed = b;
        b.Tag ??= b.Content;
        b.Content = b.Style == (Style)FindResource("PrimaryButton") ? ask : new TextBlock { Text = ask, FontSize = 12.5, Margin = new Thickness(10, 6, 10, 6) };
        Motion.Pop(b, 0.92);
        _disarm.Start();
    }

    void Disarm()
    {
        _disarm.Stop();
        if (_armed != null && _armed.Tag != null) _armed.Content = _armed.Tag;
        if (_armed != null) _armed.Tag = null;
        _armed = null;
    }

    // ───────────────────────── keys ─────────────────────────

    void OnKey(object sender, KeyEventArgs e)
    {
        bool ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        if (ctrl && e.Key == Key.Z) { _ = UndoLast(); e.Handled = true; }
        else if (ctrl && e.Key == Key.F) { _search.Focus(); _search.SelectAll(); e.Handled = true; }
        else if (ctrl && e.Key == Key.A && !_search.IsKeyboardFocused) { _selected.Clear(); _selected.AddRange(Visible().Select(i => i.Id)); Render(); e.Handled = true; }
        else if (e.Key == Key.Enter && _selected.Count > 0) { _ = RestoreItems(Selection(), null); e.Handled = true; }
        else if (e.Key == Key.Delete && _selected.Count > 0 && !_search.IsKeyboardFocused) { _ = DeleteItems(Selection()); e.Handled = true; }
        else if (e.Key == Key.Escape) { if (_selected.Count > 0) { _selected.Clear(); Render(); } else Close(); e.Handled = true; }
        else if (e.Key is Key.Down or Key.Up && !_search.IsKeyboardFocused || e.Key is Key.Down && _search.IsKeyboardFocused)
        {
            var order = Visible().ToList();
            if (order.Count == 0) return;
            int at = _anchor is { } a ? order.FindIndex(i => i.Id == a) : -1;
            int next = Math.Clamp(at + (e.Key == Key.Down ? 1 : -1), 0, order.Count - 1);
            Select(order[next], Keyboard.Modifiers & ModifierKeys.Shift);
            if (_rows.TryGetValue(order[next].Id, out var row)) row.BringIntoView();
            Keyboard.Focus(_scroll);
            e.Handled = true;
        }
    }

    // ───────────────────────── bits ─────────────────────────

    FrameworkElement SearchBox()
    {
        var box = new Border { CornerRadius = Theme.R(10), BorderThickness = new Thickness(1), Width = 220, Height = 32, Padding = new Thickness(10, 0, 10, 0) };
        box.SetResourceReference(Border.BackgroundProperty, "SurfaceAlt");
        box.SetResourceReference(Border.BorderBrushProperty, "Border");
        _search.Style = (Style)FindResource("PlainTextBox");
        _search.FontSize = 13;
        var hint = Txt("Search the Bin", 13, dim: true);
        hint.IsHitTestVisible = false;
        _search.TextChanged += (_, _) => { hint.Visibility = _search.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed; Render(); };
        var g = new Grid();
        g.Children.Add(hint);
        g.Children.Add(_search);
        box.Child = g;
        return box;
    }

    Button TextButton(string text, string glyph)
    {
        var content = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(10, 6, 12, 6) };
        content.Children.Add(new GlyphIcon { Glyph = glyph, Width = 15, Height = 15, Margin = new Thickness(0, 0, 7, 0) });
        content.Children.Add(new TextBlock { Text = text, FontSize = 12.5, VerticalAlignment = VerticalAlignment.Center });
        return new Button { Style = (Style)FindResource("IconButton"), Content = content, Margin = new Thickness(8, 0, 0, 0), HorizontalContentAlignment = HorizontalAlignment.Left };
    }

    Button Tiny(string glyph, string tip, Action click)
    {
        var b = new Button { Style = (Style)FindResource("IconButton"), Width = 30, Height = 30, ToolTip = tip, Margin = new Thickness(2, 0, 0, 0), Content = new GlyphIcon { Glyph = glyph, Width = 15, Height = 15 } };
        b.Click += (_, e) => { click(); e.Handled = true; };
        b.PreviewMouseLeftButtonDown += (_, e) => e.Handled = false;
        return b;
    }

    static FrameworkElement IconFor(Bin.Item item, double size)
    {
        if (item.IsFolder) return new GlyphIcon { Glyph = "files", Width = size * 0.8, Height = size * 0.8, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
        var src = ShellIcons.Get(item.DataPath, size > 48 ? ShellIcons.LargeSize : 64);
        return new Image { Source = src, Width = size, Height = size, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
    }

    static FrameworkElement EmptyState(string title, string text)
    {
        var s = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 110, 0, 0) };
        s.Children.Add(new GlyphIcon { Glyph = "trash", Width = 54, Height = 54, Opacity = 0.55, Margin = new Thickness(0, 0, 0, 16) });
        var t = Txt(title, 17, bold: true);
        t.HorizontalAlignment = HorizontalAlignment.Center;
        s.Children.Add(t);
        var d = Txt(text, 12.5, dim: true);
        d.TextAlignment = TextAlignment.Center;
        d.TextWrapping = TextWrapping.Wrap;
        d.TextTrimming = TextTrimming.None;
        d.Margin = new Thickness(0, 6, 0, 0);
        s.Children.Add(d);
        return s;
    }

    static TextBlock Note(string text)
    {
        var t = Txt(text, 12, dim: true);
        t.TextWrapping = TextWrapping.Wrap;
        t.TextTrimming = TextTrimming.None;
        t.Margin = new Thickness(10, 20, 0, 0);
        return t;
    }

    static string Pretty(string folder) => LauncherWindow.FriendlyFolder(folder);

    static string Ago(DateTime d)
    {
        var span = DateTime.Now - d;
        if (span.TotalMinutes < 1) return "just now";
        if (span.TotalMinutes < 60) return $"{(int)span.TotalMinutes} min ago";
        if (span.TotalHours < 24) return $"{(int)span.TotalHours} h ago";
        if (span.TotalDays < 7) return d.ToString("dddd", CultureInfo.CurrentCulture);
        return d.ToString("d MMM", CultureInfo.CurrentCulture);
    }

    static TextBlock Txt(string s, double size, bool bold = false, bool dim = false)
    {
        var t = new TextBlock { Text = s, FontSize = size, FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
        t.SetResourceReference(TextBlock.ForegroundProperty, dim ? "TextDim" : "Text");
        return t;
    }
}
