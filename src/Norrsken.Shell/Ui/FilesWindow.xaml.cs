using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Norrsken.Shell.Core;
using Norrsken.Shell.Interop;
using Path = System.IO.Path;

namespace Norrsken.Shell.Ui;

/// <summary>
/// Norrsken Files: tabs, a preview pane and Space-to-Quick-Look, pinned folders, Recent, This PC,
/// Recycle Bin with restore, deep search, undo (Ctrl+Z) for rename/move/copy/new/delete,
/// zoomable icons, type-to-select, zip, drag and drop, and every Explorer keyboard shortcut.
/// File operations go through the Windows shell, so conflicts, progress and the Recycle Bin
/// behave exactly as they do everywhere else on the PC.
/// </summary>
public partial class FilesWindow : Window
{
    public const string RecycleBin = "::recycle", Recent = "::recent", ThisPC = "::thispc";

    sealed class Tab
    {
        public string Path = "";
        public Stack<string> Back = new(), Forward = new();
    }

    abstract record UndoStep(string Label);
    sealed record Renamed(string From, string To) : UndoStep("rename");
    sealed record Created(string Path) : UndoStep("new item");
    sealed record Moved(List<(string From, string To)> Items) : UndoStep("move");
    sealed record Copied(List<string> Made) : UndoStep("copy");
    sealed record Recycled(List<string> Originals) : UndoStep("delete");

    readonly ObservableCollection<FileEntry> _items = new();
    readonly List<FileEntry> _all = new();
    readonly List<Tab> _tabs = new();
    readonly Stack<UndoStep> _undo = new();
    Tab _tab = new();
    string _path = "";
    string _sort = "Name";
    bool _desc;
    int _generation;
    FileSystemWatcher? _watcher;
    readonly DispatcherTimer _reload = new() { Interval = TimeSpan.FromMilliseconds(350) };
    readonly DispatcherTimer _message = new() { Interval = TimeSpan.FromSeconds(4) };
    CancellationTokenSource? _search, _measure;
    Action<string>? _nameDone;
    Point _dragStart;
    bool _navigating;
    string _typeAhead = "";
    DateTime _typeAheadAt;
    static readonly int[] IconSizes = [40, 48, 64, 96, 128, 176];

    static readonly BlockingCollection<(FileEntry Item, int Gen, FilesWindow Owner)> IconQueue = new();
    static readonly ConcurrentDictionary<string, ImageSource?> ExtIcons = new(StringComparer.OrdinalIgnoreCase);
    static readonly HashSet<string> OwnIconExt = new(StringComparer.OrdinalIgnoreCase) { ".exe", ".lnk", ".ico", ".url", ".appref-ms", ".msc", ".cpl", ".scr" };
    static readonly HashSet<string> ImageExt = new(StringComparer.OrdinalIgnoreCase) { ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".webp", ".tif", ".tiff", ".ico", ".heic", ".jfif" };
    static readonly HashSet<string> ThumbExt = new(StringComparer.OrdinalIgnoreCase) { ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".webp", ".heic", ".tif", ".tiff", ".jfif", ".mp4", ".mov", ".mkv", ".avi", ".webm", ".pdf", ".svg" };

    static FilesWindow()
    {
        var t = new Thread(() =>
        {
            foreach (var (item, gen, owner) in IconQueue.GetConsumingEnumerable())
            {
                if (gen != owner._generation) continue;
                ImageSource? icon = null;
                try { icon = LoadIcon(item, owner.IconPixels); } catch (Exception) { }
                owner.Dispatcher.BeginInvoke(() => item.Icon = icon ?? owner.GlyphFor(item));
            }
        }) { IsBackground = true, Name = "Files icons" };
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
    }

    int IconPixels => IsListView ? 32 : (int)Math.Min(256, App.Settings.FilesIconSize * 1.5);

    static ImageSource? LoadIcon(FileEntry e, int size)
    {
        if (e.Used != null || (e.IsDir && e.ShellItem == null)) return null; // drives and folders: Norrsken glyphs
        if (e.ShellItem != null) return ShellIcons.Get(e.FullPath, size);
        string ext = Path.GetExtension(e.FullPath);
        if (ThumbExt.Contains(ext) && size > 32) return ShellIcons.Thumbnail(e.FullPath, size);
        if (OwnIconExt.Contains(ext) || ext.Length == 0) return ShellIcons.Get(e.FullPath, size);
        return ExtIcons.GetOrAdd(ext + "|" + size, _ => ShellIcons.Get(e.FullPath, size));
    }

    public FilesWindow(string? start = null)
    {
        InitializeComponent();
        ItemsList.ItemsSource = _items;
        BuildSidebar();
        ApplyView();
        ApplyPreviewPane();
        _reload.Tick += (_, _) => { _reload.Stop(); Reload(keepSelection: true); };
        _message.Tick += (_, _) => { _message.Stop(); MessageText.Text = ""; };
        PreviewKeyDown += Keys;
        PreviewMouseDown += (_, e) =>
        {
            if (e.ChangedButton == MouseButton.XButton1) { Back_Click(this, e); e.Handled = true; }
            if (e.ChangedButton == MouseButton.XButton2) { Forward_Click(this, e); e.Handled = true; }
        };
        Closed += (_, _) => { _watcher?.Dispose(); _search?.Cancel(); _measure?.Cancel(); };

        string first = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string? select = null;
        if (start != null)
        {
            if (File.Exists(start)) { first = Path.GetDirectoryName(start)!; select = start; }
            else if (Directory.Exists(start) || start is RecycleBin or Recent or ThisPC) first = start;
        }
        _tabs.Add(_tab);
        Navigate(first, record: false);
        if (select != null) SelectPath(select);
        Loaded += (_, _) => ItemsList.Focus();
        AppChrome.Attach(this, Root);
    }

    internal string CurrentPath => _path;
    internal IReadOnlyList<FileEntry> Items => _items;
    internal int TabCount => _tabs.Count;
    internal bool IsListView => App.Settings.FilesView == "list";
    bool IsVirtual => _path is RecycleBin or Recent or ThisPC;
    IntPtr Hwnd => new WindowInteropHelper(this).Handle;

    // ───────────────────────── Norrsken glyph icons (folders, drives) ─────────────────────────

    readonly Dictionary<string, ImageSource> _glyphs = new();

    ImageSource? GlyphFor(FileEntry e) => e.Used != null ? Glyph("drive") : e.IsDir ? Glyph("folder") : null;

    ImageSource Glyph(string kind)
    {
        if (_glyphs.TryGetValue(kind, out var img)) return img;
        var text = ((SolidColorBrush)FindResource("Text")).Color;
        Brush Tone(double a) { var b = new SolidColorBrush(Color.FromArgb((byte)(255 * a), text.R, text.G, text.B)); b.Freeze(); return b; }
        var g = new DrawingGroup();
        if (kind == "folder")
        {
            g.Children.Add(new GeometryDrawing(Tone(0.28), null, Geometry.Parse("M4,9 C4,7.3 5.3,6 7,6 H19 L23,10 H41 C42.7,10 44,11.3 44,13 V38 C44,39.7 42.7,41 41,41 H7 C5.3,41 4,39.7 4,38 Z")));
            g.Children.Add(new GeometryDrawing(Tone(0.52), new Pen(Tone(0.22), 0.8), Geometry.Parse("M4,17 C4,15.3 5.3,14 7,14 H41 C42.7,14 44,15.3 44,17 V38 C44,39.7 42.7,41 41,41 H7 C5.3,41 4,39.7 4,38 Z")));
            g.Children.Add(new GeometryDrawing(null, new Pen(Tone(0.32), 1.2) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round }, Geometry.Parse("M10,20 H38")));
        }
        else
        {
            g.Children.Add(new GeometryDrawing(Tone(0.18), null, new RectangleGeometry(new Rect(0, 0, 48, 48))) { Brush = Brushes.Transparent });
            g.Children.Add(new GeometryDrawing(Tone(0.5), new Pen(Tone(0.22), 0.8), Geometry.Parse("M6,26 H42 V37 C42,38.7 40.7,40 39,40 H9 C7.3,40 6,38.7 6,37 Z")));
            g.Children.Add(new GeometryDrawing(Tone(0.28), null, Geometry.Parse("M6,26 L11,12 C11.5,10.8 12.5,10 14,10 H34 C35.5,10 36.5,10.8 37,12 L42,26 Z")));
            g.Children.Add(new GeometryDrawing(Tone(0.9), null, new EllipseGeometry(new Point(35, 33), 1.8, 1.8)));
        }
        var di = new DrawingImage(g);
        di.Freeze();
        return _glyphs[kind] = di;
    }

    // ───────────────────────── sidebar ─────────────────────────

    record NavTarget(string Path, string Label, string Glyph);

    static readonly string FolderGlyph = "M3,7.5 C3,6.4 3.9,5.5 5,5.5 L9.2,5.5 L11,7.5 L19,7.5 C20.1,7.5 21,8.4 21,9.5 L21,17.5 C21,18.6 20.1,19.5 19,19.5 L5,19.5 C3.9,19.5 3,18.6 3,17.5 Z";

    void BuildSidebar()
    {
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var places = new List<NavTarget>
        {
            new(home, "Home", "M4,11 L12,4 L20,11 V20 H4 Z M10,20 V14 H14 V20"),
            new(Recent, "Recent", "M12,3.5 A8.5,8.5 0 1 1 11.99,3.5 Z M12,7.5 V12 L15,14"),
            new(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "Desktop", "M3.5,5 H20.5 V16 H3.5 Z M9,20 H15 M12,16 V20"),
            new(Files.Downloads, "Downloads", "M12,4 V15 M7.5,10.5 L12,15 L16.5,10.5 M5,19.5 H19"),
            new(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Documents", "M6,3.5 H14.5 L18.5,7.5 V20.5 H6 Z M14.5,3.5 V7.5 H18.5 M8.5,11.5 H15.5 M8.5,14.5 H15.5"),
            new(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "Pictures", "M4,5 H20 V19 H4 Z M4,16 L9,11 L13,15 L15.5,12.5 L20,17"),
            new(Environment.GetFolderPath(Environment.SpecialFolder.MyMusic), "Music", "M9,17.5 A2.5,2.5 0 1 1 8.99,17.5 Z M11.5,17.5 V5 L19,3.5 V15 M19,15 A2.5,2.5 0 1 1 18.99,15 Z"),
            new(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "Videos", "M12,3.5 A8.5,8.5 0 1 1 11.99,3.5 Z M10,8.5 L15.5,12 L10,15.5 Z"),
            new(ThisPC, "This PC", "M3.5,5 H20.5 V16 H3.5 Z M9,20 H15 M12,16 V20"),
            new(RecycleBin, "Recycle Bin", "M5,7 H19 M9,7 V5 H15 V7 M7,7 L8,20 H16 L17,7"),
        };
        foreach (var p in places.Where(p => p.Path.StartsWith("::") || Directory.Exists(p.Path)))
            NavList.Items.Add(NavRow(p, null));
        BuildPins();
        BuildDrives();
    }

    void BuildPins()
    {
        PinList.Items.Clear();
        foreach (var p in App.Settings.FilesPins.Where(Directory.Exists))
            PinList.Items.Add(NavRow(new(p, DisplayName(p), FolderGlyph), null));
        PinnedHeader.Visibility = PinList.Visibility = PinList.Items.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    void BuildDrives()
    {
        DriveList.Items.Clear();
        foreach (var d in DriveInfo.GetDrives())
        {
            try
            {
                if (!d.IsReady) continue;
                double used = d.TotalSize > 0 ? 1 - (double)d.AvailableFreeSpace / d.TotalSize : 0;
                DriveList.Items.Add(NavRow(new(d.RootDirectory.FullName, DriveLabel(d.Name), "M4,13 H20 V19 H4 Z M4,13 L6.5,6 H17.5 L20,13 M16.5,16 H16.6"),
                    (used, $"{Files.FormatSize(d.AvailableFreeSpace)} free of {Files.FormatSize(d.TotalSize)}")));
            }
            catch (IOException) { }
        }
    }

    ListBoxItem NavRow(NavTarget t, (double Used, string Free)? usage)
    {
        var glyph = new System.Windows.Shapes.Path { Data = Geometry.Parse(t.Glyph), StrokeThickness = 1.6, Width = 16, Height = 16, Stretch = Stretch.Uniform, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, StrokeLineJoin = PenLineJoin.Round, Margin = new Thickness(0, 0, 11, 0) };
        glyph.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, "TextDim");
        var text = new TextBlock { Text = t.Label, FontSize = 13, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        text.SetResourceReference(TextBlock.ForegroundProperty, "Text");
        var row = new DockPanel();
        row.Children.Add(glyph);
        if (usage is { } u)
        {
            var stack = new StackPanel();
            stack.Children.Add(text);
            var track = new Border { Height = 3, CornerRadius = Theme.R(1.5), Margin = new Thickness(0, 5, 0, 1) };
            track.SetResourceReference(Border.BackgroundProperty, "Border");
            var fill = new Border { Height = 3, CornerRadius = Theme.R(1.5), HorizontalAlignment = HorizontalAlignment.Left };
            fill.SetResourceReference(Border.BackgroundProperty, u.Used > 0.9 ? "Text" : "TextDim");
            var bar = new Grid();
            bar.Children.Add(track);
            bar.Children.Add(fill);
            bar.SizeChanged += (_, _) => fill.Width = bar.ActualWidth * u.Used;
            stack.Children.Add(bar);
            row.ToolTip = u.Free;
            row.Children.Add(stack);
        }
        else row.Children.Add(text);
        var item = new ListBoxItem { Content = row, Tag = t.Path, AllowDrop = !t.Path.StartsWith("::") || t.Path == RecycleBin };
        item.Drop += (_, e) => { if (t.Path == RecycleBin) DropToBin(e); else DropFiles(e, t.Path); e.Handled = true; };
        item.MouseUp += (_, e) => { if (e.ChangedButton == MouseButton.Middle) { OpenTab(t.Path); e.Handled = true; } };
        if (App.Settings.FilesPins.Contains(t.Path))
        {
            var menu = new ContextMenu();
            var unpin = new MenuItem { Header = "Unpin from sidebar" };
            unpin.Click += (_, _) => TogglePin(t.Path);
            menu.Items.Add(unpin);
            item.ContextMenu = menu;
        }
        return item;
    }

    void Nav_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_navigating || sender is not ListBox lb || lb.SelectedItem is not ListBoxItem { Tag: string path }) return;
        Navigate(path);
    }

    void SyncSidebar()
    {
        _navigating = true;
        foreach (var lb in new[] { PinList, NavList, DriveList })
            lb.SelectedItem = lb.Items.Cast<ListBoxItem>().FirstOrDefault(i => string.Equals((string)i.Tag, _path, StringComparison.OrdinalIgnoreCase));
        if (PinList.SelectedItem != null) { NavList.SelectedItem = null; DriveList.SelectedItem = null; }
        else if (NavList.SelectedItem != null) DriveList.SelectedItem = null;
        _navigating = false;
    }

    void TogglePin(string path)
    {
        var pins = App.Settings.FilesPins;
        if (!pins.Remove(path)) pins.Add(path);
        SaveSettings();
        BuildPins();
        SyncSidebar();
        Message(pins.Contains(path) ? "Pinned to the sidebar" : "Unpinned");
    }

    // ───────────────────────── tabs ─────────────────────────

    void NewTab_Click(object sender, RoutedEventArgs e) => OpenTab(IsVirtual ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) : _path);

    internal void OpenTab(string path)
    {
        var t = new Tab();
        _tabs.Insert(_tabs.IndexOf(_tab) + 1, t);
        SwitchTab(t, path);
    }

    void SwitchTab(Tab t, string? path = null)
    {
        _tab.Path = _path;
        _tab = t;
        Navigate(path ?? t.Path, record: false);
        BuildTabs();
    }

    internal void CloseCurrentTab() => CloseTab();

    void CloseTab(Tab? t = null)
    {
        t ??= _tab;
        if (_tabs.Count == 1) { Close(); return; }
        int i = _tabs.IndexOf(t);
        _tabs.Remove(t);
        if (t == _tab) { _tab = _tabs[Math.Min(i, _tabs.Count - 1)]; Navigate(_tab.Path, record: false); }
        BuildTabs();
    }

    void CycleTab(int dir)
    {
        if (_tabs.Count < 2) return;
        int i = (_tabs.IndexOf(_tab) + dir + _tabs.Count) % _tabs.Count;
        SwitchTab(_tabs[i]);
    }

    void BuildTabs()
    {
        TabStrip.Children.Clear();
        TabBar.Visibility = _tabs.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
        if (_tabs.Count < 2) return;
        foreach (var t in _tabs)
        {
            string p = t == _tab ? _path : t.Path;
            var close = new Button { Style = (Style)FindResource("IconButton"), Width = 20, Height = 20, Margin = new Thickness(6, 0, 0, 0), ToolTip = "Close tab (Ctrl+W)" };
            var x = new System.Windows.Shapes.Path { Data = Geometry.Parse("M7,7 L17,17 M17,7 L7,17"), StrokeThickness = 1.6, Width = 8, Height = 8, Stretch = Stretch.Uniform, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round };
            x.SetBinding(System.Windows.Shapes.Shape.StrokeProperty, new System.Windows.Data.Binding("Foreground") { Source = close });
            close.Content = x;
            var tab = t;
            close.Click += (_, _) => CloseTab(tab);
            var content = new StackPanel { Orientation = Orientation.Horizontal };
            content.Children.Add(new TextBlock { Text = DisplayName(p), FontSize = 12.5, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 160 });
            content.Children.Add(close);
            var pill = new RadioButton { Style = (Style)FindResource("TabPill"), Content = content, IsChecked = t == _tab, GroupName = "tabs", ToolTip = p.StartsWith("::") ? DisplayName(p) : p };
            pill.Checked += (_, _) => { if (tab != _tab) SwitchTab(tab); };
            pill.MouseUp += (_, e) => { if (e.ChangedButton == MouseButton.Middle) { CloseTab(tab); e.Handled = true; } };
            pill.AllowDrop = true;
            pill.Drop += (_, e) => { DropFiles(e, tab == _tab ? _path : tab.Path); e.Handled = true; };
            TabStrip.Children.Add(pill);
        }
    }

    // ───────────────────────── navigation ─────────────────────────

    internal void Navigate(string path, bool record = true)
    {
        if (!path.StartsWith("::"))
        {
            try { path = Path.GetFullPath(Environment.ExpandEnvironmentVariables(path)); }
            catch (Exception) { Message("That isn't a valid path."); return; }
            if (!Directory.Exists(path))
            {
                if (File.Exists(path)) { Files.Open(path); return; }
                Message($"“{path}” wasn't found.");
                return;
            }
        }
        if (record && _path.Length > 0 && !string.Equals(_path, path, StringComparison.OrdinalIgnoreCase)) { _tab.Back.Push(_path); _tab.Forward.Clear(); }
        _path = path;
        _tab.Path = path;
        _search?.Cancel();
        _search = null;
        SearchBox.Text = "";
        Watch();
        Reload(keepSelection: false);
        BuildCrumbs();
        SyncSidebar();
        BackButton.IsEnabled = _tab.Back.Count > 0;
        ForwardButton.IsEnabled = _tab.Forward.Count > 0;
        UpButton.IsEnabled = !IsVirtual && Directory.GetParent(_path) != null || (!IsVirtual && Path.GetPathRoot(_path) == _path);
        NewFolderButton.IsEnabled = !IsVirtual;
        Title = DisplayName(_path) + " — Files";
        if (_tabs.Count > 1) BuildTabs();
        ItemsList.Focus();
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => Motion.Stagger(Enumerable.Range(0, Math.Min(_items.Count, 36)).Select(i => ItemsList.ItemContainerGenerator.ContainerFromIndex(i)).OfType<UIElement>(), stepMs: 10, max: 36, rise: 8, ms: 280));
    }

    static string DisplayName(string path)
    {
        switch (path)
        {
            case RecycleBin: return "Recycle Bin";
            case Recent: return "Recent";
            case ThisPC: return "This PC";
        }
        if (string.Equals(path, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), StringComparison.OrdinalIgnoreCase)) return "Home";
        string name = Path.GetFileName(path.TrimEnd('\\'));
        return name.Length > 0 ? name : DriveLabel(path);
    }

    void Back_Click(object sender, RoutedEventArgs e)
    {
        if (_tab.Back.Count == 0) return;
        _tab.Forward.Push(_path);
        Navigate(_tab.Back.Pop(), record: false);
    }

    void Forward_Click(object sender, RoutedEventArgs e)
    {
        if (_tab.Forward.Count == 0) return;
        _tab.Back.Push(_path);
        Navigate(_tab.Forward.Pop(), record: false);
    }

    void Up_Click(object sender, RoutedEventArgs e)
    {
        if (IsVirtual) return;
        string from = _path;
        if (Directory.GetParent(_path) is { } parent) Navigate(parent.FullName);
        else Navigate(ThisPC);
        SelectPath(from);
    }

    void Watch()
    {
        _watcher?.Dispose();
        _watcher = null;
        if (IsVirtual) return;
        try
        {
            _watcher = new FileSystemWatcher(_path) { NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.Size | NotifyFilters.LastWrite, IncludeSubdirectories = false };
            FileSystemEventHandler h = (_, _) => Dispatcher.BeginInvoke(() => { _reload.Stop(); _reload.Start(); });
            _watcher.Created += h; _watcher.Deleted += h; _watcher.Changed += h;
            _watcher.Renamed += (_, _) => Dispatcher.BeginInvoke(() => { _reload.Stop(); _reload.Start(); });
            _watcher.EnableRaisingEvents = true;
        }
        catch (Exception ex) { Log.Write("Files watcher: " + ex.Message); }
    }

    // ───────────────────────── listing ─────────────────────────

    internal async void Reload(bool keepSelection)
    {
        var keep = keepSelection ? ItemsList.SelectedItems.Cast<FileEntry>().Select(i => i.FullPath).ToHashSet(StringComparer.OrdinalIgnoreCase) : null;
        int gen = ++_generation;
        _all.Clear();
        switch (_path)
        {
            case RecycleBin: _all.AddRange(RecycleItems()); break;
            case ThisPC: _all.AddRange(DriveItems()); break;
            case Recent:
                StatusText.Text = "Loading…";
                var recent = await Files.RunSta(RecentItems);
                if (gen != _generation) return;
                _all.AddRange(recent);
                break;
            default:
                try
                {
                    foreach (var fsi in new DirectoryInfo(_path).EnumerateFileSystemInfos("*", new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = 0 }))
                    {
                        bool hidden = (fsi.Attributes & (FileAttributes.Hidden | FileAttributes.System)) != 0;
                        if (hidden && !App.Settings.FilesShowHidden) continue;
                        bool isDir = (fsi.Attributes & FileAttributes.Directory) != 0;
                        _all.Add(new FileEntry
                        {
                            FullPath = fsi.FullName, Name = fsi.Name, IsDir = isDir, IsHidden = hidden, Modified = fsi.LastWriteTime,
                            Size = isDir ? 0 : ((FileInfo)fsi).Length, TypeName = Files.TypeName(fsi.FullName, isDir),
                        });
                    }
                }
                catch (UnauthorizedAccessException) { Message("You don't have permission to open this folder."); }
                catch (IOException ex) { Message(ex.Message); }
                break;
        }
        ApplyFilter();
        if (keep != null)
            foreach (var i in _items.Where(i => keep.Contains(i.FullPath))) ItemsList.SelectedItems.Add(i);
    }

    static IEnumerable<FileEntry> DriveItems()
    {
        foreach (var d in DriveInfo.GetDrives())
        {
            FileEntry? e = null;
            try
            {
                if (!d.IsReady) continue;
                double used = d.TotalSize > 0 ? 1 - (double)d.AvailableFreeSpace / d.TotalSize : 0;
                e = new FileEntry
                {
                    FullPath = d.RootDirectory.FullName, Name = DriveLabel(d.Name), IsDir = true, Size = d.TotalSize, Used = used,
                    TypeName = d.DriveType switch { DriveType.Removable => "USB drive", DriveType.Network => "Network drive", DriveType.CDRom => "Disc drive", _ => "Local disk" },
                    Detail = $"{Files.FormatSize(d.AvailableFreeSpace)} free of {Files.FormatSize(d.TotalSize)}",
                };
            }
            catch (IOException) { }
            if (e != null) yield return e;
        }
    }

    static List<FileEntry> RecentItems()
    {
        var list = new List<FileEntry>();
        string dir = Environment.GetFolderPath(Environment.SpecialFolder.Recent);
        try
        {
            dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell")!)!;
            foreach (var lnk in new DirectoryInfo(dir).EnumerateFiles("*.lnk").OrderByDescending(f => f.LastWriteTime).Take(250))
            {
                string target;
                try { target = shell.CreateShortcut(lnk.FullName).TargetPath; } catch (Exception) { continue; }
                if (string.IsNullOrEmpty(target)) continue;
                bool isDir = Directory.Exists(target);
                if (!isDir && !File.Exists(target)) continue;
                long size = isDir ? 0 : new FileInfo(target).Length;
                list.Add(new FileEntry { FullPath = target, Name = Path.GetFileName(target.TrimEnd('\\')) is { Length: > 0 } n ? n : target, IsDir = isDir, Modified = lnk.LastWriteTime, Size = size, TypeName = Files.TypeName(target, isDir), Location = Path.GetDirectoryName(target) });
            }
        }
        catch (Exception ex) { Log.Write("Recent: " + ex.Message); }
        return list;
    }

    IEnumerable<FileEntry> RecycleItems()
    {
        var list = new List<FileEntry>();
        try
        {
            dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("Shell.Application")!)!;
            dynamic bin = shell.NameSpace(10);
            foreach (dynamic item in bin.Items())
            {
                string from = "";
                try { from = item.ExtendedProperty("System.Recycle.DeletedFrom") as string ?? ""; } catch (Exception) { }
                DateTime deleted = default;
                try { if (item.ExtendedProperty("System.Recycle.DateDeleted") is DateTime d) deleted = d; } catch (Exception) { }
                long size = 0;
                try { size = Convert.ToInt64(item.Size); } catch (Exception) { }
                list.Add(new FileEntry { FullPath = item.Path, Name = item.Name, IsDir = item.IsFolder, Modified = deleted, Size = size, TypeName = item.Type, Location = from.Length > 0 ? "From " + from : null, ShellItem = item });
            }
        }
        catch (Exception ex) { Log.Write("Recycle Bin listing failed: " + ex.Message); }
        return list;
    }

    void ApplyFilter()
    {
        string q = SearchBox.Text.Trim();
        IEnumerable<FileEntry> rows = q.Length == 0 || _search != null ? _all : _all.Where(i => i.Name.Contains(q, StringComparison.CurrentCultureIgnoreCase));
        Func<FileEntry, object> key = _sort switch
        {
            "Modified" => i => i.Modified,
            "Type" => i => i.TypeName,
            "Size" => i => i.Size,
            _ => i => i.Name,
        };
        IOrderedEnumerable<FileEntry> sorted = _path == Recent && _sort == "Name" && !_desc
            ? rows.OrderByDescending(i => i.Modified)
            : rows.OrderBy(i => i.IsDir ? 0 : 1);
        if (!(_path == Recent && _sort == "Name" && !_desc))
            sorted = _desc ? sorted.ThenByDescending(key, Comparer<object>.Create(CompareNatural)) : sorted.ThenBy(key, Comparer<object>.Create(CompareNatural));
        _items.Clear();
        foreach (var i in sorted) { _items.Add(i); IconQueue.Add((i, _generation, this)); }
        EmptyText.Text = _path switch { RecycleBin => "The Recycle Bin is empty", Recent => "Nothing opened recently", _ => q.Length > 0 ? "No matches" : "This folder is empty" };
        EmptyText.Visibility = _items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        UpdateSortLabels();
        UpdateStatus();
    }

    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)] static extern int StrCmpLogicalW(string a, string b);
    static int CompareNatural(object? a, object? b) => a is string sa && b is string sb ? StrCmpLogicalW(sa, sb) : Comparer<object>.Default.Compare(a, b);

    void Sort_Click(object sender, RoutedEventArgs e) => SortBy((string)((Button)sender).Tag, toggle: true);

    void SortBy(string field, bool toggle = false, bool? desc = null)
    {
        _desc = desc ?? (toggle && _sort == field && !_desc);
        _sort = field;
        ApplyFilter();
    }

    void UpdateSortLabels()
    {
        string Arrow(string f) => _sort == f ? (_desc ? "  ↓" : "  ↑") : "";
        SortName.Text = "Name" + Arrow("Name");
        SortModified.Text = (_path == RecycleBin ? "Date deleted" : _path == Recent ? "Opened" : "Date modified") + Arrow("Modified");
        SortType.Text = "Type" + Arrow("Type");
        SortSize.Text = (_path == ThisPC ? "Space" : "Size") + Arrow("Size");
    }

    void UpdateStatus()
    {
        int sel = ItemsList.SelectedItems.Count;
        string s = $"{_items.Count:N0} item{(_items.Count == 1 ? "" : "s")}";
        if (sel > 0)
        {
            long bytes = ItemsList.SelectedItems.Cast<FileEntry>().Where(i => !i.IsDir).Sum(i => i.Size);
            s += $"   ·   {sel:N0} selected" + (bytes > 0 ? $"   ·   {Files.FormatSize(bytes)}" : "");
        }
        if (!IsVirtual)
        {
            try { var d = new DriveInfo(Path.GetPathRoot(_path)!); if (d.IsReady) s += $"   ·   {Files.FormatSize(d.AvailableFreeSpace)} free"; }
            catch (Exception) { }
        }
        StatusText.Text = s;
    }

    void Items_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateStatus();
        if (App.Settings.FilesPreview) UpdatePreview();
        if (QuickLook.Visibility == Visibility.Visible) ShowQuickLook();
    }

    void ApplyView()
    {
        bool list = IsListView;
        ItemsList.ItemTemplate = (DataTemplate)FindResource(list ? "ListTemplate" : "GridTemplate");
        ItemsList.ItemsPanel = (ItemsPanelTemplate)FindResource(list ? "ListPanel" : "GridPanel");
        Headers.Visibility = list ? Visibility.Visible : Visibility.Collapsed;
        ViewGlyph.Data = Geometry.Parse(list ? "M4,4 H10 V10 H4 Z M14,4 H20 V10 H14 Z M4,14 H10 V20 H4 Z M14,14 H20 V20 H14 Z" : "M8,6 H20 M8,12 H20 M8,18 H20 M4,6 H4.1 M4,12 H4.1 M4,18 H4.1");
        ViewButton.ToolTip = list ? "Icons (Ctrl+Shift+1)" : "Details (Ctrl+Shift+2)";
        double size = Math.Clamp(App.Settings.FilesIconSize, IconSizes[0], IconSizes[^1]);
        Resources["FileIconSize"] = size;
        Resources["FileTileWidth"] = Math.Max(96, size + 44);
    }

    void SetView(string view)
    {
        if (App.Settings.FilesView == view) return;
        App.Settings.FilesView = view;
        SaveSettings();
        ApplyView();
        RefreshIcons();
    }

    void View_Click(object sender, RoutedEventArgs e) => SetView(IsListView ? "grid" : "list");

    void RefreshIcons()
    {
        _generation++;
        foreach (var i in _items) { i.Icon = null; IconQueue.Add((i, _generation, this)); }
    }

    void Zoom(int step)
    {
        if (IsListView) { if (step > 0) SetView("grid"); return; }
        int i = Array.FindIndex(IconSizes, s => s >= App.Settings.FilesIconSize);
        i = Math.Clamp((i < 0 ? 2 : i) + step, 0, IconSizes.Length - 1);
        if (step < 0 && IconSizes[i] == App.Settings.FilesIconSize && i == 0) { SetView("list"); return; }
        App.Settings.FilesIconSize = IconSizes[i];
        SaveSettings();
        ApplyView();
        RefreshIcons();
    }

    void Items_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (!Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) return;
        Zoom(Math.Sign(e.Delta));
        e.Handled = true;
    }

    static void SaveSettings() { try { App.Settings.Save(); } catch (IOException) { } }

    void SelectPath(string path)
    {
        var item = _items.FirstOrDefault(i => string.Equals(i.FullPath, path, StringComparison.OrdinalIgnoreCase));
        if (item == null) return;
        ItemsList.SelectedItems.Clear();
        ItemsList.SelectedItem = item;
        ItemsList.ScrollIntoView(item);
    }

    // ───────────────────────── breadcrumb & path editing ─────────────────────────

    void BuildCrumbs()
    {
        Crumbs.Children.Clear();
        PathEdit.Visibility = Visibility.Collapsed;
        CrumbScroll.Visibility = Visibility.Visible;
        if (IsVirtual) { Crumbs.Children.Add(Crumb(DisplayName(_path), _path)); return; }
        var parts = new List<(string Label, string Path)>();
        for (var d = new DirectoryInfo(_path); d != null; d = d.Parent)
            parts.Insert(0, (d.Parent == null ? DriveLabel(d.FullName) : d.Name, d.FullName));
        for (int i = 0; i < parts.Count; i++)
        {
            if (i > 0) Crumbs.Children.Add(CrumbChevron(parts[i - 1].Path));
            Crumbs.Children.Add(Crumb(parts[i].Label, parts[i].Path));
        }
        CrumbScroll.ScrollToRightEnd();
    }

    static string DriveLabel(string root)
    {
        try { var d = new DriveInfo(root); return (d.IsReady && d.VolumeLabel.Length > 0 ? d.VolumeLabel : d.DriveType == DriveType.Removable ? "USB drive" : "Local disk") + $" ({root.TrimEnd('\\')})"; }
        catch (Exception) { return root; }
    }

    Button Crumb(string label, string path)
    {
        var t = new TextBlock { Text = label, FontSize = 13, Margin = new Thickness(8, 4, 8, 4) };
        t.SetResourceReference(TextBlock.ForegroundProperty, "Text");
        var b = new Button { Style = (Style)FindResource("IconButton"), Content = t, Cursor = Cursors.Hand, ToolTip = path.StartsWith("::") ? null : path };
        b.Click += (_, _) => Navigate(path);
        b.MouseUp += (_, e) => { if (e.ChangedButton == MouseButton.Middle) OpenTab(path); };
        b.AllowDrop = !path.StartsWith("::");
        b.Drop += (_, e) => { DropFiles(e, path); e.Handled = true; };
        return b;
    }

    /// <summary>The "›" between crumbs lists that folder's subfolders, to jump sideways.</summary>
    Button CrumbChevron(string parent)
    {
        var t = new TextBlock { Text = "›", FontSize = 13, Margin = new Thickness(3, 2, 3, 3) };
        t.SetResourceReference(TextBlock.ForegroundProperty, "TextDim");
        var b = new Button { Style = (Style)FindResource("IconButton"), Content = t, Cursor = Cursors.Hand, ToolTip = "Folders here" };
        b.Click += (_, _) =>
        {
            var menu = new ContextMenu { PlacementTarget = b, Placement = PlacementMode.Bottom };
            try
            {
                foreach (var d in new DirectoryInfo(parent).EnumerateDirectories("*", new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = FileAttributes.Hidden | FileAttributes.System }).OrderBy(d => d.Name).Take(60))
                {
                    var mi = new MenuItem { Header = d.Name };
                    string p = d.FullName;
                    mi.Click += (_, _) => Navigate(p);
                    menu.Items.Add(mi);
                }
            }
            catch (Exception) { }
            if (menu.Items.Count > 0) menu.IsOpen = true;
        };
        return b;
    }

    void PathBox_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is FrameworkElement fe && (fe.TemplatedParent is Button || fe is TextBlock)) return;
        EditPath();
    }

    void EditPath()
    {
        if (IsVirtual) return;
        CrumbScroll.Visibility = Visibility.Collapsed;
        PathEdit.Visibility = Visibility.Visible;
        PathEdit.Text = _path;
        PathBox.SetResourceReference(Border.BackgroundProperty, "SurfaceAlt");
        PathEdit.Focus();
        PathEdit.SelectAll();
    }

    void PathEdit_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { string p = PathEdit.Text.Trim().Trim('"'); EndPathEdit(); if (p.Length > 0) Navigate(p); e.Handled = true; }
        else if (e.Key == Key.Escape) { EndPathEdit(); ItemsList.Focus(); e.Handled = true; }
    }

    void PathEdit_LostFocus(object sender, KeyboardFocusChangedEventArgs e) => EndPathEdit();

    void EndPathEdit()
    {
        PathEdit.Visibility = Visibility.Collapsed;
        CrumbScroll.Visibility = Visibility.Visible;
        PathBox.Background = Brushes.Transparent;
    }

    // ───────────────────────── search ─────────────────────────

    void Search_TextChanged(object sender, TextChangedEventArgs e)
    {
        SearchHint.Visibility = SearchBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (_search != null) { _search.Cancel(); _search = null; Reload(false); return; }
        ApplyFilter();
    }

    void Search_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { SearchBox.Text = ""; ItemsList.Focus(); e.Handled = true; }
        else if (e.Key == Key.Enter && SearchBox.Text.Trim().Length > 0 && !IsVirtual) { _ = DeepSearch(SearchBox.Text.Trim()); e.Handled = true; }
        else if (e.Key == Key.Down) { ItemsList.Focus(); if (_items.Count > 0) ItemsList.SelectedIndex = 0; e.Handled = true; }
    }

    /// <summary>Enter in the search box: look through every subfolder too (stops at 2,000 results).</summary>
    internal async Task DeepSearch(string q)
    {
        _search?.Cancel();
        var cts = _search = new CancellationTokenSource();
        string root = _path;
        _generation++;
        _items.Clear();
        _all.Clear();
        StatusText.Text = "Searching…";
        var results = await Task.Run(() =>
        {
            var found = new List<FileEntry>();
            var opts = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, MatchCasing = MatchCasing.CaseInsensitive, AttributesToSkip = App.Settings.FilesShowHidden ? 0 : FileAttributes.Hidden | FileAttributes.System };
            try
            {
                foreach (var fsi in new DirectoryInfo(root).EnumerateFileSystemInfos("*" + q + "*", opts))
                {
                    if (cts.IsCancellationRequested || found.Count >= 2000) break;
                    bool isDir = (fsi.Attributes & FileAttributes.Directory) != 0;
                    found.Add(new FileEntry
                    {
                        FullPath = fsi.FullName, Name = fsi.Name, IsDir = isDir, Modified = fsi.LastWriteTime, Size = isDir ? 0 : ((FileInfo)fsi).Length,
                        TypeName = Files.TypeName(fsi.FullName, isDir), Location = Path.GetDirectoryName(fsi.FullName),
                    });
                }
            }
            catch (Exception ex) { Log.Write("Search: " + ex.Message); }
            return found;
        });
        if (cts.IsCancellationRequested) return;
        _all.AddRange(results);
        ApplyFilter();
        StatusText.Text = $"{results.Count:N0} result{(results.Count == 1 ? "" : "s")} in {DisplayName(root)} and its subfolders" + (results.Count >= 2000 ? " (first 2,000)" : "");
    }

    // ───────────────────────── opening ─────────────────────────

    List<FileEntry> Selected => ItemsList.SelectedItems.Cast<FileEntry>().ToList();
    FileEntry? Hit(object source) => source is DependencyObject d && ItemsControl.ContainerFromElement(ItemsList, d) is ListBoxItem { DataContext: FileEntry f } ? f : null;

    void Items_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left && Hit(e.OriginalSource) is { } f) OpenEntry(f);
    }

    void Items_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Middle && Hit(e.OriginalSource) is { IsDir: true, ShellItem: null } f) { OpenTab(f.FullPath); e.Handled = true; }
    }

    internal void OpenEntry(FileEntry f)
    {
        if (f.ShellItem != null) { Message("Restore it first to open it."); return; }
        if (f.IsDir) { Navigate(f.FullPath); return; }
        try { Files.Open(f.FullPath); }
        catch (Exception ex) { Message($"Couldn't open {f.Name}: {ex.Message}"); }
    }

    void Items_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.None && Selected is { Count: > 0 } sel) { foreach (var f in sel.Take(sel.Count == 1 ? 1 : 15)) OpenEntry(f); e.Handled = true; }
    }

    /// <summary>Type the start of a name to jump to it.</summary>
    void Items_TextInput(object sender, TextCompositionEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(e.Text) || Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) return;
        if ((DateTime.Now - _typeAheadAt).TotalMilliseconds > 900) _typeAhead = "";
        _typeAheadAt = DateTime.Now;
        _typeAhead += e.Text;
        var match = _items.FirstOrDefault(i => i.Name.StartsWith(_typeAhead, StringComparison.CurrentCultureIgnoreCase));
        if (match != null) { ItemsList.SelectedItems.Clear(); ItemsList.SelectedItem = match; ItemsList.ScrollIntoView(match); FocusItem(match); }
        e.Handled = true;
    }

    void FocusItem(FileEntry f) => (ItemsList.ItemContainerGenerator.ContainerFromItem(f) as ListBoxItem)?.Focus();

    void Keys(object sender, KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        var mods = Keyboard.Modifiers;
        bool ctrl = mods.HasFlag(ModifierKeys.Control), shift = mods.HasFlag(ModifierKeys.Shift), alt = mods.HasFlag(ModifierKeys.Alt);
        if (NamePrompt.Visibility == Visibility.Visible) { if (key == Key.Escape) { NameCancel_Click(this, e); e.Handled = true; } return; }
        if (QuickLook.Visibility == Visibility.Visible)
        {
            switch (key)
            {
                case Key.Space or Key.Escape: HideQuickLook(); e.Handled = true; return;
                case Key.Left or Key.Up: MoveSelection(-1); e.Handled = true; return;
                case Key.Right or Key.Down: MoveSelection(1); e.Handled = true; return;
                case Key.Enter: HideQuickLook(); if (Selected.FirstOrDefault() is { } f) OpenEntry(f); e.Handled = true; return;
            }
        }
        bool typing = e.OriginalSource is TextBox;
        switch (key)
        {
            case Key.Left when alt: Back_Click(this, e); break;
            case Key.Right when alt: Forward_Click(this, e); break;
            case Key.Up when alt: Up_Click(this, e); break;
            case Key.P when alt: PreviewToggle_Click(this, e); break;
            case Key.Back when !typing: Up_Click(this, e); break;
            case Key.Space when !typing && Selected.Count > 0: ShowQuickLook(); break;
            case Key.L when ctrl: EditPath(); break;
            case Key.D when alt: EditPath(); break;
            case Key.F when ctrl: SearchBox.Focus(); SearchBox.SelectAll(); break;
            case Key.E when ctrl: SearchBox.Focus(); SearchBox.SelectAll(); break;
            case Key.F5: Reload(true); break;
            case Key.T when ctrl: NewTab_Click(this, e); break;
            case Key.W when ctrl: CloseTab(); break;
            case Key.Tab when ctrl: CycleTab(shift ? -1 : 1); break;
            case Key.N when ctrl && shift: NewFolder_Click(this, e); break;
            case Key.N when ctrl: NorrskenApps.Launch("files", IsVirtual ? null : _path); break;
            case Key.Z when ctrl && !typing: Undo(); break;
            case Key.D when ctrl && !typing: Duplicate(); break;
            case Key.Delete when !typing: DeleteSelected(shift); break;
            case Key.F2 when !typing: Rename(); break;
            case Key.C when ctrl && shift && !typing: CopyAsPath(); break;
            case Key.C when ctrl && !typing: ToClipboard(move: false); break;
            case Key.X when ctrl && !typing: ToClipboard(move: true); break;
            case Key.V when ctrl && !typing: Paste(); break;
            case Key.A when ctrl && !typing: ItemsList.SelectAll(); break;
            case Key.I when ctrl && !typing: InvertSelection(); break;
            case Key.Enter when alt && !typing: Properties(); break;
            case Key.H when ctrl && !typing: ToggleHidden(); break;
            case Key.D1 when ctrl && shift: SetView("grid"); break;
            case Key.D2 when ctrl && shift: SetView("list"); break;
            case Key.OemPlus or Key.Add when ctrl: Zoom(1); break;
            case Key.OemMinus or Key.Subtract when ctrl: Zoom(-1); break;
            default: return;
        }
        e.Handled = true;
    }

    void MoveSelection(int d)
    {
        if (_items.Count == 0) return;
        int i = Math.Clamp(ItemsList.SelectedIndex + d, 0, _items.Count - 1);
        ItemsList.SelectedItems.Clear();
        ItemsList.SelectedIndex = i;
        ItemsList.ScrollIntoView(_items[i]);
    }

    void InvertSelection()
    {
        var sel = Selected.ToHashSet();
        ItemsList.SelectedItems.Clear();
        foreach (var i in _items.Where(i => !sel.Contains(i))) ItemsList.SelectedItems.Add(i);
    }

    void Properties()
    {
        string? p = Selected.FirstOrDefault()?.FullPath ?? (IsVirtual ? null : _path);
        if (p != null && !App.TestMode) Files.Properties(Hwnd, p);
    }

    // ───────────────────────── preview pane & Quick Look ─────────────────────────

    void PreviewToggle_Click(object sender, RoutedEventArgs e)
    {
        App.Settings.FilesPreview = !App.Settings.FilesPreview;
        SaveSettings();
        ApplyPreviewPane();
    }

    void ApplyPreviewPane()
    {
        bool on = App.Settings.FilesPreview;
        PreviewColumn.Width = on ? new GridLength(300) : new GridLength(0);
        PreviewPane.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        if (on) { UpdatePreview(); Motion.From(PreviewPane, OpacityProperty, 0, 1, 200); }
    }

    TextBlock Dim(string text, double size = 12) { var t = new TextBlock { Text = text, FontSize = size, TextWrapping = TextWrapping.Wrap }; t.SetResourceReference(TextBlock.ForegroundProperty, "TextDim"); return t; }
    TextBlock Strong(string text, double size = 15) { var t = new TextBlock { Text = text, FontSize = size, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap }; t.SetResourceReference(TextBlock.ForegroundProperty, "Text"); return t; }

    internal void UpdatePreview()
    {
        _measure?.Cancel();
        PreviewContent.Children.Clear();
        var sel = Selected;
        if (sel.Count != 1)
        {
            PreviewContent.Children.Add(Strong(sel.Count == 0 ? DisplayName(_path) : $"{sel.Count} items"));
            PreviewContent.Children.Add(Dim(sel.Count == 0 ? $"{_items.Count:N0} items" : Files.FormatSize(sel.Where(s => !s.IsDir).Sum(s => s.Size)) + " in files"));
            return;
        }
        var f = sel[0];
        var visual = new Border { CornerRadius = Theme.R(12), ClipToBounds = true, Margin = new Thickness(0, 4, 0, 14), HorizontalAlignment = HorizontalAlignment.Left };
        visual.SetResourceReference(Border.BackgroundProperty, "SurfaceAlt");
        string ext = Path.GetExtension(f.FullPath);
        if (!f.IsDir && ImageExt.Contains(ext) && f.ShellItem == null) visual.Child = new Image { Source = Decode(f.FullPath, 520), MaxHeight = 220, MaxWidth = 260, Stretch = Stretch.Uniform };
        else if (!f.IsDir && IsText(f.FullPath) && f.Size < 4_000_000)
        {
            var t = new TextBlock { Text = ReadHead(f.FullPath, 40), FontFamily = new FontFamily("Cascadia Mono, Consolas"), FontSize = 10.5, Margin = new Thickness(12), TextWrapping = TextWrapping.NoWrap };
            t.SetResourceReference(TextBlock.ForegroundProperty, "Text");
            visual.Child = t;
            visual.MaxHeight = 260;
            visual.Width = 260;
        }
        else visual.Child = new Image { Source = f.Icon ?? GlyphFor(f), Width = 96, Height = 96, Margin = new Thickness(14) };
        PreviewContent.Children.Add(visual);
        PreviewContent.Children.Add(Strong(f.Name));
        PreviewContent.Children.Add(Dim(f.TypeName));

        var facts = new Grid { Margin = new Thickness(0, 14, 0, 0) };
        facts.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        facts.ColumnDefinitions.Add(new ColumnDefinition());
        void Fact(string k, string v)
        {
            int r = facts.RowDefinitions.Count;
            facts.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var kt = Dim(k, 12); kt.Margin = new Thickness(0, 3, 14, 3); Grid.SetRow(kt, r);
            var vt = new TextBlock { Text = v, FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 3, 0, 3) }; vt.SetResourceReference(TextBlock.ForegroundProperty, "Text"); Grid.SetRow(vt, r); Grid.SetColumn(vt, 1);
            facts.Children.Add(kt); facts.Children.Add(vt);
        }
        if (f.Used != null) Fact("Space", f.Detail ?? "");
        else if (!f.IsDir) Fact("Size", $"{Files.FormatSize(f.Size)} ({f.Size:N0} bytes)");
        try
        {
            if (f.ShellItem == null && f.Used == null)
            {
                var info = f.IsDir ? (FileSystemInfo)new DirectoryInfo(f.FullPath) : new FileInfo(f.FullPath);
                Fact("Modified", info.LastWriteTime.ToString("g"));
                Fact("Created", info.CreationTime.ToString("g"));
                if (info.Attributes.HasFlag(FileAttributes.ReadOnly)) Fact("Attributes", "Read-only");
            }
        }
        catch (Exception) { }
        if (!f.IsDir && ImageExt.Contains(ext) && f.ShellItem == null)
        {
            try { using var fs = File.OpenRead(f.FullPath); var fr = BitmapDecoder.Create(fs, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None).Frames[0]; Fact("Dimensions", $"{fr.PixelWidth} × {fr.PixelHeight}"); }
            catch (Exception) { }
        }
        if (f.Location != null) Fact("Location", f.Location.Replace("From ", ""));
        PreviewContent.Children.Add(facts);

        if (f.IsDir && f.Used == null && f.ShellItem == null)
        {
            var size = Dim("Measuring…", 12);
            size.Margin = new Thickness(0, 6, 0, 0);
            PreviewContent.Children.Add(size);
            var cts = _measure = new CancellationTokenSource();
            string dir = f.FullPath;
            Task.Run(() => Files.Measure(dir, cts.Token)).ContinueWith(t =>
            {
                if (cts.IsCancellationRequested) return;
                if (t.Status != TaskStatus.RanToCompletion) { size.Text = "Couldn't measure this folder"; return; }
                var (bytes, files, folders) = t.Result;
                size.Text = $"{Files.FormatSize(bytes)}  ·  {files:N0} files, {folders:N0} folders";
            }, TaskScheduler.FromCurrentSynchronizationContext());
        }

        var actions = new WrapPanel { Margin = new Thickness(0, 14, 0, 0) };
        void Act(string label, Action a) { var b = new Button { Style = (Style)FindResource("LinkButton"), Content = new TextBlock { Text = label, FontSize = 12.5 }, Margin = new Thickness(-6, 0, 10, 0) }; b.Click += (_, _) => a(); actions.Children.Add(b); }
        if (f.ShellItem == null) Act("Open", () => OpenEntry(f));
        Act("Copy path", CopyAsPath);
        if (f.ShellItem == null && f.Used == null) Act("Properties", Properties);
        PreviewContent.Children.Add(actions);
    }

    static BitmapSource? Decode(string path, int width)
    {
        try
        {
            var img = new BitmapImage();
            img.BeginInit();
            img.UriSource = new Uri(path);
            img.DecodePixelWidth = width;
            img.CacheOption = BitmapCacheOption.OnLoad;
            img.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            img.EndInit();
            img.Freeze();
            return img;
        }
        catch (Exception) { return null; }
    }

    static string ReadHead(string path, int lines)
    {
        try
        {
            using var r = new StreamReader(path, detectEncodingFromByteOrderMarks: true);
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < lines && r.ReadLine() is { } line; i++) sb.AppendLine(line.Length > 120 ? line[..120] + "…" : line);
            return sb.ToString();
        }
        catch (Exception ex) { return ex.Message; }
    }

    internal bool QuickLookOpen => QuickLook.Visibility == Visibility.Visible;

    internal void ShowQuickLook()
    {
        if (Selected.FirstOrDefault() is not { } f) return;
        bool wasOpen = QuickLook.Visibility == Visibility.Visible;
        QuickBody.Children.Clear();
        string ext = Path.GetExtension(f.FullPath);
        double maxW = Math.Max(300, ActualWidth - 200), maxH = Math.Max(240, ActualHeight - 220);
        if (!f.IsDir && ImageExt.Contains(ext) && f.ShellItem == null)
            QuickBody.Children.Add(new Image { Source = Decode(f.FullPath, (int)Math.Min(2400, maxW * 1.5)), MaxWidth = maxW, MaxHeight = maxH, Stretch = Stretch.Uniform });
        else if (!f.IsDir && IsText(f.FullPath) && f.Size < 8_000_000)
        {
            var t = new TextBox { Text = ReadHead(f.FullPath, 600), IsReadOnly = true, FontFamily = new FontFamily("Cascadia Mono, Consolas"), FontSize = 12.5, BorderThickness = new Thickness(0), Background = Brushes.Transparent, Width = Math.Min(900, maxW), Height = Math.Min(640, maxH), VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto };
            QuickBody.Children.Add(t);
        }
        else
        {
            var stack = new StackPanel { Margin = new Thickness(40, 30, 40, 10) };
            stack.Children.Add(new Image { Source = (f.IsDir ? null : ShellIcons.Get(f.FullPath, 256)) ?? f.Icon ?? GlyphFor(f), Width = 160, Height = 160 });
            QuickBody.Children.Add(stack);
        }
        QuickName.Text = f.Name;
        QuickInfo.Text = f.IsDir ? f.TypeName : $"{f.TypeName}  ·  {Files.FormatSize(f.Size)}  ·  {f.ModifiedText}";
        QuickLook.Visibility = Visibility.Visible;
        if (!wasOpen)
        {
            Motion.From(QuickLook, OpacityProperty, 0, 1, 160);
            Motion.From(QuickScale, ScaleTransform.ScaleXProperty, 0.94, 1, 260, Motion.SoftSpring);
            Motion.From(QuickScale, ScaleTransform.ScaleYProperty, 0.94, 1, 260, Motion.SoftSpring);
        }
    }

    void HideQuickLook()
    {
        Motion.To(QuickLook, OpacityProperty, 0, 120, done: () => { QuickLook.Visibility = Visibility.Collapsed; QuickLook.Opacity = 1; QuickBody.Children.Clear(); });
        ItemsList.Focus();
    }

    void QuickLook_MouseDown(object sender, MouseButtonEventArgs e) { if (e.OriginalSource == QuickLook) HideQuickLook(); }

    // ───────────────────────── operations ─────────────────────────

    void Message(string text)
    {
        MessageText.Text = text;
        _message.Stop();
        _message.Start();
    }

    async void DeleteSelected(bool forever)
    {
        var sel = Selected.Where(f => f.Used == null).ToList();
        if (sel.Count == 0) return;
        if (_path == RecycleBin)
        {
            foreach (var f in sel) InvokeVerb(f, "delete");
            Reload(false);
            return;
        }
        var paths = sel.Select(f => f.FullPath).ToList();
        var hwnd = Hwnd;
        if (App.TestMode) { App.TestLaunches.Add((forever ? "delete-forever:" : "recycle:") + string.Join("|", paths.Select(Path.GetFileName))); return; }
        await Files.RunSta(() => { if (forever) Files.DeleteForever(hwnd, paths); else Files.Recycle(hwnd, paths); });
        if (!forever && paths.Any(p => !File.Exists(p) && !Directory.Exists(p))) _undo.Push(new Recycled(paths.Where(p => !File.Exists(p) && !Directory.Exists(p)).ToList()));
        Message(forever ? $"Deleted {paths.Count} item{(paths.Count == 1 ? "" : "s")}" : $"Moved {paths.Count} item{(paths.Count == 1 ? "" : "s")} to the Recycle Bin  ·  Ctrl+Z to undo");
        Reload(false);
    }

    static void InvokeVerb(FileEntry f, string verb)
    {
        try { ((dynamic)f.ShellItem!).InvokeVerb(verb); }
        catch (Exception ex) { Log.Write($"Recycle Bin {verb} failed: {ex.Message}"); }
    }

    void ToClipboard(bool move)
    {
        var sel = Selected.Where(f => f.ShellItem == null).ToList();
        if (sel.Count == 0) return;
        var files = new System.Collections.Specialized.StringCollection();
        files.AddRange(sel.Select(f => f.FullPath).ToArray());
        var data = new DataObject();
        data.SetFileDropList(files);
        data.SetData("Preferred DropEffect", new MemoryStream(BitConverter.GetBytes(move ? 2 : 5)));
        if (!App.TestMode) Clipboard.SetDataObject(data, true);
        Message(move ? $"Cut {sel.Count} item{(sel.Count == 1 ? "" : "s")}" : $"Copied {sel.Count} item{(sel.Count == 1 ? "" : "s")}");
    }

    void CopyAsPath()
    {
        var text = string.Join(Environment.NewLine, Selected.Select(f => Selected.Count > 1 ? $"\"{f.FullPath}\"" : f.FullPath));
        if (text.Length == 0 && !IsVirtual) text = _path;
        if (text.Length > 0 && !App.TestMode) Clipboard.SetText(text);
        Message("Path copied");
    }

    /// <summary>Runs a copy or move and records it for undo (only the items that really appeared).</summary>
    async Task Transfer(List<string> files, string dest, bool move)
    {
        if (files.Count == 0 || !Directory.Exists(dest)) return;
        var before = Directory.EnumerateFileSystemEntries(dest).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var hwnd = Hwnd;
        if (App.TestMode) { foreach (var f in files) { string t = Path.Combine(dest, Path.GetFileName(f)); if (move) { if (Directory.Exists(f)) Directory.Move(f, t); else File.Move(f, t); } else if (File.Exists(f)) File.Copy(f, Unique(t)); } }
        else await Files.RunSta(() => { if (move) Files.Move(hwnd, files, dest); else Files.Copy(hwnd, files, dest); });
        var made = Directory.EnumerateFileSystemEntries(dest).Where(p => !before.Contains(p)).ToList();
        if (move)
        {
            var pairs = files.Select(f => (f, Path.Combine(dest, Path.GetFileName(f)))).Where(p => made.Contains(p.Item2, StringComparer.OrdinalIgnoreCase)).ToList();
            if (pairs.Count > 0) _undo.Push(new Moved(pairs));
        }
        else if (made.Count > 0) _undo.Push(new Copied(made));
        if (string.Equals(dest, _path, StringComparison.OrdinalIgnoreCase))
        {
            Reload(false);
            foreach (var m in made) if (_items.FirstOrDefault(i => i.FullPath.Equals(m, StringComparison.OrdinalIgnoreCase)) is { } item) ItemsList.SelectedItems.Add(item);
        }
        else Message($"{(move ? "Moved" : "Copied")} {made.Count} item{(made.Count == 1 ? "" : "s")} to {DisplayName(dest)}");
    }

    async void Paste()
    {
        if (IsVirtual) return;
        string dest = _path;
        if (Clipboard.ContainsFileDropList())
        {
            var files = Clipboard.GetFileDropList().Cast<string>().ToList();
            bool move = Clipboard.GetData("Preferred DropEffect") is MemoryStream ms && ms.Length >= 4 && (BitConverter.ToInt32(ms.ToArray(), 0) & 2) != 0;
            await Transfer(files, dest, move);
            if (move) Clipboard.Clear();
        }
        else if (Clipboard.ContainsImage() && Clipboard.GetImage() is { } img)
        {
            string file = Unique(Path.Combine(dest, $"Pasted image {DateTime.Now:yyyy-MM-dd HHmmss}.png"));
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(img));
            using (var fs = File.Create(file)) enc.Save(fs);
            _undo.Push(new Created(file));
            Reload(false);
            SelectPath(file);
        }
        else if (Clipboard.ContainsText())
        {
            string file = Unique(Path.Combine(dest, "Pasted text.txt"));
            File.WriteAllText(file, Clipboard.GetText());
            _undo.Push(new Created(file));
            Reload(false);
            SelectPath(file);
        }
    }

    async void Duplicate()
    {
        var sel = Selected.Where(f => f.ShellItem == null && f.Used == null).Select(f => f.FullPath).ToList();
        if (sel.Count == 0 || IsVirtual) return;
        await Transfer(sel, _path, move: false);
    }

    async void CopyOrMoveTo(bool move)
    {
        var sel = Selected.Where(f => f.ShellItem == null && f.Used == null).Select(f => f.FullPath).ToList();
        if (sel.Count == 0) return;
        var dlg = new Microsoft.Win32.OpenFolderDialog { Title = move ? "Move to" : "Copy to", InitialDirectory = IsVirtual ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) : _path };
        if (dlg.ShowDialog(this) != true) return;
        await Transfer(sel, dlg.FolderName, move);
    }

    void CreateShortcut(FileEntry f)
    {
        string lnk = Unique(Path.Combine(IsVirtual ? Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory) : _path, Path.GetFileNameWithoutExtension(f.Name) + " - Shortcut.lnk"));
        try { ShellLink.Save(lnk, f.FullPath, "", null, "", null); _undo.Push(new Created(lnk)); Reload(false); SelectPath(lnk); }
        catch (Exception ex) { Message("Shortcut failed: " + ex.Message); }
    }

    /// <summary>Ctrl+Z: reverses the last rename, move, copy, new item or delete made in this window.</summary>
    internal async void Undo()
    {
        if (!_undo.TryPop(out var step)) { Message("Nothing to undo"); return; }
        var hwnd = Hwnd;
        try
        {
            switch (step)
            {
                case Renamed r:
                    if (Directory.Exists(r.To)) Directory.Move(r.To, r.From); else File.Move(r.To, r.From);
                    break;
                case Created c:
                    if (App.TestMode) { if (Directory.Exists(c.Path)) Directory.Delete(c.Path, true); else File.Delete(c.Path); }
                    else await Files.RunSta(() => Files.Recycle(hwnd, [c.Path]));
                    break;
                case Copied c:
                    if (App.TestMode) foreach (var p in c.Made) { if (Directory.Exists(p)) Directory.Delete(p, true); else File.Delete(p); }
                    else await Files.RunSta(() => Files.Recycle(hwnd, c.Made));
                    break;
                case Moved m:
                    foreach (var (from, to) in m.Items)
                    {
                        if (App.TestMode || string.Equals(Path.GetPathRoot(from), Path.GetPathRoot(to), StringComparison.OrdinalIgnoreCase))
                        { if (Directory.Exists(to)) Directory.Move(to, from); else File.Move(to, from); }
                        else { string back = Path.GetDirectoryName(from)!; await Files.RunSta(() => Files.Move(hwnd, [to], back)); }
                    }
                    break;
                case Recycled d:
                    int n = await Files.RunSta(() => Files.Restore(d.Originals));
                    if (n == 0) { Message("Couldn't find those items in the Recycle Bin"); return; }
                    break;
            }
            Message($"Undid {step.Label}");
        }
        catch (Exception ex) { Message($"Couldn't undo {step.Label}: {ex.Message}"); }
        Reload(false);
    }

    internal int UndoDepth => _undo.Count;

    static string Unique(string path)
    {
        if (!File.Exists(path) && !Directory.Exists(path)) return path;
        string dir = Path.GetDirectoryName(path)!, name = Path.GetFileNameWithoutExtension(path), ext = Path.GetExtension(path);
        if (Directory.Exists(path)) { name = Path.GetFileName(path); ext = ""; }
        for (int i = 2; ; i++)
        {
            string p = Path.Combine(dir, $"{name} ({i}){ext}");
            if (!File.Exists(p) && !Directory.Exists(p)) return p;
        }
    }

    void AskName(string title, string initial, Action<string> done, bool selectStem = true)
    {
        NameTitle.Text = title;
        NameBox.Text = initial;
        NameError.Text = "";
        _nameDone = done;
        NamePrompt.Visibility = Visibility.Visible;
        Motion.From(NameCard, OpacityProperty, 0, 1, 150);
        NameBox.Focus();
        int stem = selectStem && Path.HasExtension(initial) ? Path.GetFileNameWithoutExtension(initial).Length : initial.Length;
        NameBox.Select(0, stem);
    }

    void NameBox_KeyDown(object sender, KeyEventArgs e) { if (e.Key == Key.Enter) { NameOk_Click(this, e); e.Handled = true; } }
    void NameCancel_Click(object sender, RoutedEventArgs e) { NamePrompt.Visibility = Visibility.Collapsed; _nameDone = null; ItemsList.Focus(); }

    internal void NameOk_Click(object sender, RoutedEventArgs e)
    {
        string name = NameBox.Text.Trim();
        if (name.Length == 0 || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) { NameError.Text = "A name can't be empty or contain \\ / : * ? \" < > |"; return; }
        try
        {
            _nameDone?.Invoke(name);
            NamePrompt.Visibility = Visibility.Collapsed;
            _nameDone = null;
            ItemsList.Focus();
        }
        catch (Exception ex) { NameError.Text = ex.Message; }
    }

    internal TextBox NameInput => NameBox;

    internal void Rename()
    {
        if (Selected is not [var f] || IsVirtual) return;
        AskName("Rename", f.Name, name =>
        {
            if (name == f.Name) return;
            string target = Path.Combine(Path.GetDirectoryName(f.FullPath)!, name);
            if (!string.Equals(target, f.FullPath, StringComparison.OrdinalIgnoreCase) && (File.Exists(target) || Directory.Exists(target)))
                throw new IOException($"“{name}” already exists here.");
            if (f.IsDir) Directory.Move(f.FullPath, target); else File.Move(f.FullPath, target);
            _undo.Push(new Renamed(f.FullPath, target));
            Reload(false);
            SelectPath(target);
        }, selectStem: !f.IsDir);
    }

    internal void NewFolder_Click(object sender, RoutedEventArgs e)
    {
        if (IsVirtual) return;
        AskName("New folder", Path.GetFileName(Unique(Path.Combine(_path, "New folder"))), name =>
        {
            string p = Path.Combine(_path, name);
            if (Directory.Exists(p) || File.Exists(p)) throw new IOException($"“{name}” already exists here.");
            Directory.CreateDirectory(p);
            _undo.Push(new Created(p));
            Reload(false);
            SelectPath(p);
        }, selectStem: false);
    }

    void NewTextFile()
    {
        if (IsVirtual) return;
        AskName("New text document", Path.GetFileName(Unique(Path.Combine(_path, "New text document.txt"))), name =>
        {
            string p = Path.Combine(_path, name);
            if (File.Exists(p) || Directory.Exists(p)) throw new IOException($"“{name}” already exists here.");
            File.WriteAllText(p, "");
            _undo.Push(new Created(p));
            Reload(false);
            SelectPath(p);
        });
    }

    void ToggleHidden()
    {
        App.Settings.FilesShowHidden = !App.Settings.FilesShowHidden;
        SaveSettings();
        Reload(true);
        Message(App.Settings.FilesShowHidden ? "Showing hidden items" : "Hiding hidden items");
    }

    async void Compress(List<FileEntry> sel)
    {
        string zip = Unique(Path.Combine(_path, (sel.Count == 1 ? Path.GetFileNameWithoutExtension(sel[0].Name) : DisplayName(_path)) + ".zip"));
        Message("Compressing…");
        try
        {
            await Task.Run(() =>
            {
                using var archive = ZipFile.Open(zip, ZipArchiveMode.Create);
                foreach (var f in sel)
                {
                    if (f.IsDir)
                        foreach (var file in Directory.EnumerateFiles(f.FullPath, "*", SearchOption.AllDirectories))
                            archive.CreateEntryFromFile(file, Path.Combine(f.Name, Path.GetRelativePath(f.FullPath, file)), CompressionLevel.Optimal);
                    else archive.CreateEntryFromFile(f.FullPath, f.Name, CompressionLevel.Optimal);
                }
            });
            _undo.Push(new Created(zip));
            Reload(false);
            SelectPath(zip);
            Message("Compressed");
        }
        catch (Exception ex) { Message("Compress failed: " + ex.Message); }
    }

    async void Extract(FileEntry zip)
    {
        string dest = Unique(Path.Combine(_path, Path.GetFileNameWithoutExtension(zip.Name)));
        Message("Extracting…");
        try
        {
            await Task.Run(() => ZipFile.ExtractToDirectory(zip.FullPath, dest));
            _undo.Push(new Created(dest));
            Reload(false);
            SelectPath(dest);
            Message("Extracted");
        }
        catch (Exception ex) { Message("Extract failed: " + ex.Message); }
    }

    static void StartTool(string exe, string args, bool admin = false)
    {
        if (App.TestMode) { App.TestLaunches.Add(exe + " " + args); return; }
        try { Process.Start(new ProcessStartInfo(exe, args) { UseShellExecute = true, Verb = admin ? "runas" : "" }); }
        catch (Exception ex) { Log.Write($"{exe} failed: {ex.Message}"); }
    }

    void OpenTerminal(string dir)
    {
        if (App.TestMode) { App.TestLaunches.Add("terminal:" + dir); return; }
        try { Process.Start(new ProcessStartInfo("wt.exe", $"-d \"{dir}\"") { UseShellExecute = true }); }
        catch (Exception) { StartTool("powershell.exe", $"-NoExit -Command Set-Location -LiteralPath '{dir.Replace("'", "''")}'"); }
    }

    // ───────────────────────── context menu ─────────────────────────

    void Items_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        var menu = ItemsList.ContextMenu!;
        menu.Items.Clear();
        var hit = e.OriginalSource is DependencyObject d ? ItemsControl.ContainerFromElement(ItemsList, d) as ListBoxItem : null;
        if (hit != null && !hit.IsSelected) { ItemsList.SelectedItems.Clear(); hit.IsSelected = true; }
        if (hit == null) ItemsList.SelectedItems.Clear();
        var sel = Selected;

        void Item(string header, Action a, bool enabled = true, string? keys = null)
        {
            var row = new DockPanel { MinWidth = 200 };
            if (keys != null)
            {
                var k = Dim(keys, 11.5); k.Margin = new Thickness(24, 0, 0, 0);
                DockPanel.SetDock(k, Dock.Right);
                row.Children.Add(k);
            }
            row.Children.Add(new TextBlock { Text = header });
            var mi = new MenuItem { Header = row, IsEnabled = enabled };
            mi.Click += (_, _) => a();
            menu.Items.Add(mi);
        }
        void Sep() { if (menu.Items.Count > 0 && menu.Items[^1] is not Separator) menu.Items.Add(new Separator()); }

        if (_path == RecycleBin)
        {
            if (sel.Count > 0)
            {
                Item("Restore", () => { foreach (var f in sel) InvokeVerb(f, "undelete"); Reload(false); });
                Item("Delete permanently", () => DeleteSelected(true), keys: "Del");
            }
            Sep();
            Item("Empty Recycle Bin", () => { if (!App.TestMode) Files.EmptyRecycleBin(Hwnd); Reload(false); }, _all.Count > 0);
            Item("Refresh", () => Reload(false), keys: "F5");
            return;
        }

        if (sel.Count > 0)
        {
            var first = sel[0];
            bool drive = first.Used != null;
            Item("Open", () => { foreach (var f in sel.Take(15)) OpenEntry(f); }, keys: "Enter");
            if (sel.Count == 1 && first.IsDir)
            {
                Item("Open in new tab", () => OpenTab(first.FullPath), keys: "Middle-click");
                Item("Open in new window", () => NorrskenApps.Launch("files", first.FullPath));
            }
            if (sel.Count == 1 && !first.IsDir)
            {
                Item("Open with…", () => StartTool("rundll32.exe", $"shell32.dll,OpenAs_RunDLL {first.FullPath}"));
                if (IsText(first.FullPath)) Item("Edit in Notes", () => NorrskenApps.Launch("notes", first.FullPath));
                Item("Quick Look", ShowQuickLook, keys: "Space");
                if (Path.GetExtension(first.FullPath).ToLowerInvariant() is ".exe" or ".msi" or ".bat" or ".cmd" or ".ps1")
                    Item("Run as administrator", () => StartTool(first.FullPath, "", admin: true));
            }
            if (_path == Recent || _path.StartsWith("::") == false && first.Location != null)
                Item("Open file location", () => { Navigate(Path.GetDirectoryName(first.FullPath)!); SelectPath(first.FullPath); });
            Sep();
            if (!drive)
            {
                Item("Cut", () => ToClipboard(true), keys: "Ctrl+X");
                Item("Copy", () => ToClipboard(false), keys: "Ctrl+C");
                Item("Duplicate", Duplicate, !IsVirtual, "Ctrl+D");
                Item("Copy to…", () => CopyOrMoveTo(false));
                Item("Move to…", () => CopyOrMoveTo(true));
            }
            Item("Copy path", CopyAsPath, keys: "Ctrl+Shift+C");
            if (!drive)
            {
                Item("Rename", Rename, sel.Count == 1 && !IsVirtual, "F2");
                Item("Delete", () => DeleteSelected(false), keys: "Del");
            }
            Sep();
            if (sel.Count == 1 && first.IsDir) Item(App.Settings.FilesPins.Contains(first.FullPath) ? "Unpin from sidebar" : "Pin to sidebar", () => TogglePin(first.FullPath));
            if (!drive) Item("Compress to ZIP", () => Compress(sel), !IsVirtual);
            if (sel.Count == 1 && first.FullPath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) Item("Extract here", () => Extract(first), !IsVirtual);
            if (sel.Count == 1 && !drive) Item("Create shortcut", () => CreateShortcut(first));
            if (sel.Count == 1 && !first.IsDir && Path.GetExtension(first.FullPath).ToLowerInvariant() is ".png" or ".jpg" or ".jpeg" or ".bmp")
                Item("Set as desktop background", () => { if (!App.TestMode) Files.SetWallpaper(first.FullPath); Message("Background set"); });
            if (sel.Count == 1 && first.IsDir) Item("Open in Terminal", () => OpenTerminal(first.FullPath));
            Sep();
            Item("Properties", Properties, keys: "Alt+Enter");
        }
        else
        {
            if (!IsVirtual)
            {
                Item("New folder", () => NewFolder_Click(this, new RoutedEventArgs()), keys: "Ctrl+Shift+N");
                Item("New text document", NewTextFile);
                Item("Paste", Paste, Clipboard.ContainsFileDropList() || Clipboard.ContainsImage() || Clipboard.ContainsText(), "Ctrl+V");
                Item("Undo", Undo, _undo.Count > 0, "Ctrl+Z");
                Sep();
            }
            Item("Sort by name", () => SortBy("Name", desc: false));
            Item("Sort by date", () => SortBy("Modified", desc: true));
            Item("Sort by type", () => SortBy("Type", desc: false));
            Item("Sort by size", () => SortBy("Size", desc: true));
            Sep();
            Item(IsListView ? "Icons" : "Details", () => SetView(IsListView ? "grid" : "list"), keys: IsListView ? "Ctrl+Shift+1" : "Ctrl+Shift+2");
            Item("Bigger icons", () => Zoom(1), keys: "Ctrl+Scroll");
            Item("Smaller icons", () => Zoom(-1));
            Item(App.Settings.FilesShowHidden ? "Hide hidden items" : "Show hidden items", ToggleHidden, keys: "Ctrl+H");
            Sep();
            if (!IsVirtual)
            {
                Item(App.Settings.FilesPins.Contains(_path) ? "Unpin this folder" : "Pin this folder to the sidebar", () => TogglePin(_path));
                Item("Open in Terminal", () => OpenTerminal(_path));
            }
            Item("Refresh", () => Reload(true), keys: "F5");
            if (!IsVirtual) Item("Properties", Properties, keys: "Alt+Enter");
        }
    }

    static bool IsText(string path) => Path.GetExtension(path).ToLowerInvariant() is ".txt" or ".md" or ".log" or ".ini" or ".cfg" or ".json" or ".xml" or ".csv" or ".yml" or ".yaml" or ".ps1" or ".bat" or ".cmd" or ".cs" or ".js" or ".ts" or ".py" or ".html" or ".css" or ".toml" or ".properties" or ".reg" or ".xaml" or ".csproj" or ".sh" or ".lua" or ".java" or ".c" or ".cpp" or ".h" or ".rs" or ".go" or ".sql" or ".gitignore" or ".env";

    void Menu_Click(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu { PlacementTarget = MenuButton, Placement = PlacementMode.Bottom };
        void Item(string header, Action a, bool enabled = true) { var mi = new MenuItem { Header = header, IsEnabled = enabled }; mi.Click += (_, _) => a(); menu.Items.Add(mi); }
        Item("New tab", () => NewTab_Click(this, new RoutedEventArgs()));
        Item("New window", () => NorrskenApps.Launch("files", IsVirtual ? null : _path));
        Item("New text document", NewTextFile, !IsVirtual);
        menu.Items.Add(new Separator());
        Item("Undo", Undo, _undo.Count > 0);
        Item("Select all", () => ItemsList.SelectAll());
        Item("Invert selection", InvertSelection);
        Item("Paste", Paste, !IsVirtual);
        menu.Items.Add(new Separator());
        Item(App.Settings.FilesPreview ? "Hide preview pane" : "Show preview pane", () => PreviewToggle_Click(this, new RoutedEventArgs()));
        Item(App.Settings.FilesShowHidden ? "Hide hidden items" : "Show hidden items", ToggleHidden);
        Item("Open in Terminal", () => OpenTerminal(_path), !IsVirtual);
        menu.IsOpen = true;
    }

    // ───────────────────────── drag and drop ─────────────────────────

    void Items_PreviewMouseDown(object sender, MouseButtonEventArgs e) => _dragStart = e.GetPosition(ItemsList);

    void Items_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _path == RecycleBin) return;
        var p = e.GetPosition(ItemsList);
        if (Math.Abs(p.X - _dragStart.X) < 6 && Math.Abs(p.Y - _dragStart.Y) < 6) return;
        if (e.OriginalSource is not DependencyObject d || ItemsControl.ContainerFromElement(ItemsList, d) is not ListBoxItem { IsSelected: true }) return;
        var files = new System.Collections.Specialized.StringCollection();
        files.AddRange(Selected.Where(f => f.Used == null).Select(f => f.FullPath).ToArray());
        if (files.Count == 0) return;
        var data = new DataObject();
        data.SetFileDropList(files);
        DragDrop.DoDragDrop(ItemsList, data, DragDropEffects.Copy | DragDropEffects.Move | DragDropEffects.Link);
    }

    void Items_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = !e.Data.GetDataPresent(DataFormats.FileDrop) || IsVirtual ? DragDropEffects.None
            : e.KeyStates.HasFlag(DragDropKeyStates.ControlKey) ? DragDropEffects.Copy : DragDropEffects.Move;
        e.Handled = true;
    }

    void Items_Drop(object sender, DragEventArgs e)
    {
        if (IsVirtual && Hit(e.OriginalSource) is not { IsDir: true }) return;
        string dest = _path;
        if (Hit(e.OriginalSource) is { IsDir: true, ShellItem: null } folder) dest = folder.FullPath;
        DropFiles(e, dest);
    }

    async void DropFiles(DragEventArgs e, string dest)
    {
        if (dest.StartsWith("::") || e.Data.GetData(DataFormats.FileDrop) is not string[] files || files.Length == 0) return;
        files = files.Where(f => !string.Equals(Path.GetDirectoryName(f), dest, StringComparison.OrdinalIgnoreCase) && !string.Equals(f, dest, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (files.Length == 0) return;
        bool sameDrive = string.Equals(Path.GetPathRoot(files[0]), Path.GetPathRoot(dest), StringComparison.OrdinalIgnoreCase);
        bool copy = e.KeyStates.HasFlag(DragDropKeyStates.ControlKey) || (!sameDrive && !e.KeyStates.HasFlag(DragDropKeyStates.ShiftKey));
        await Transfer(files.ToList(), dest, move: !copy);
        if (!string.Equals(dest, _path, StringComparison.OrdinalIgnoreCase)) Reload(false);
    }

    async void DropToBin(DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] files || files.Length == 0) return;
        var hwnd = Hwnd;
        if (App.TestMode) { App.TestLaunches.Add("recycle-drop"); return; }
        await Files.RunSta(() => Files.Recycle(hwnd, files));
        _undo.Push(new Recycled(files.ToList()));
        Reload(false);
    }
}
