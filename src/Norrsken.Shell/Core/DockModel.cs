using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;

namespace Norrsken.Shell.Core;

/// <summary>One dock icon: a pinned app, a running program, or both.</summary>
public sealed class DockItem(string key) : INotifyPropertyChanged
{
    public string Key { get; } = key;
    public List<TrackedWindow> Windows { get; } = new();

    AppEntry? _entry;
    public AppEntry? Entry
    {
        get => _entry;
        private set
        {
            if (_entry == value) return;
            if (_entry != null) _entry.PropertyChanged -= OnEntryChanged;
            _entry = value;
            if (_entry != null) _entry.PropertyChanged += OnEntryChanged;
            Raise(nameof(Entry)); Raise(nameof(Name)); Raise(nameof(Icon)); Raise(nameof(Glyph)); Raise(nameof(HasGlyph));
        }
    }

    string _fallbackName = "";
    ImageSource? _fallbackIcon;
    Geometry? _fallbackGlyph;

    public string Name => Entry?.Name ?? _fallbackName;
    public ImageSource? Icon => Entry?.Icon ?? _fallbackIcon;
    public Geometry? Glyph => Entry != null ? Entry.Glyph : _fallbackGlyph;
    public bool HasGlyph => Glyph != null;

    bool _pinned, _running, _active, _attention;
    public bool IsPinned { get => _pinned; private set => Set(ref _pinned, value); }
    public bool IsRunning { get => _running; private set => Set(ref _running, value); }
    public bool IsActive { get => _active; private set => Set(ref _active, value); }
    public bool NeedsAttention { get => _attention; private set => Set(ref _attention, value); }

    double _dotOpacity;
    /// <summary>Running indicator: full when any window is up, dimmed when all are minimised, hidden when not running.</summary>
    public double DotOpacity
    {
        get => _dotOpacity;
        private set { if (_dotOpacity != value) { _dotOpacity = value; Raise(nameof(DotOpacity)); } }
    }

    /// <summary>Set while the view animates the icon out; it may be revived if the app comes back.</summary>
    public bool Leaving { get; set; }

    /// <summary>The next time this item's cell loads (after a reorder), skip the arrival animation.</summary>
    public bool SkipAppear { get; set; }

    internal void Update(AppEntry? entry, bool pinned, List<TrackedWindow> windows)
    {
        Entry = entry;
        IsPinned = pinned;
        Windows.Clear();
        Windows.AddRange(windows);
        var first = windows.FirstOrDefault();
        string name = first == null ? "" : first.AppName.Length > 0 ? first.AppName : first.Title;
        if (name != _fallbackName) { _fallbackName = name; Raise(nameof(Name)); }
        if (first?.Icon != _fallbackIcon) { _fallbackIcon = first?.Icon; Raise(nameof(Icon)); }
        var glyph = IconPack.ForExe(first?.ExePath);
        if (glyph != _fallbackGlyph) { _fallbackGlyph = glyph; Raise(nameof(Glyph)); Raise(nameof(HasGlyph)); }
        IsRunning = windows.Count > 0;
        IsActive = windows.Any(w => w.IsActive);
        NeedsAttention = windows.Any(w => w.NeedsAttention);
        DotOpacity = windows.Count == 0 ? 0 : windows.All(w => w.IsMinimized) ? 0.4 : 1;
    }

    void OnEntryChanged(object? s, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AppEntry.Icon)) Raise(nameof(Icon));
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    void Set(ref bool field, bool value, [CallerMemberName] string? name = null)
    {
        if (field == value) return;
        field = value;
        Raise(name!);
    }
}

/// <summary>
/// Builds the dock from pinned apps plus running windows, matching each window to its app
/// (AppUserModelID first, then executable). Updates are diffed so items keep their identity
/// and the view can animate arrivals and departures.
/// </summary>
public sealed class DockModel
{
    public ObservableCollection<DockItem> Items { get; } = new();
    public Action<DockItem, Action>? AnimateOut { get; set; }
    public Action<DockItem>? Revived { get; set; }

    Dictionary<string, AppEntry> _byParsing = new(StringComparer.OrdinalIgnoreCase);
    Dictionary<string, AppEntry> _byTarget = new(StringComparer.OrdinalIgnoreCase);
    Dictionary<string, AppEntry?> _byExeName = new(StringComparer.OrdinalIgnoreCase);
    readonly HashSet<TrackedWindow> _watched = new();
    bool _mapsDirty = true, _queued;

    public void Attach()
    {
        App.Pinned.CollectionChanged += (_, _) => Queue();
        App.Apps.CollectionChanged += (_, _) => { _mapsDirty = true; Queue(); };
        App.Tracker.Windows.CollectionChanged += (_, _) => Queue();
        RefreshNow();
    }

    void Queue()
    {
        if (_queued) return;
        _queued = true;
        Application.Current.Dispatcher.BeginInvoke(DispatcherPriority.Normal, new Action(RefreshNow));
    }

    public AppEntry? Match(TrackedWindow w)
    {
        if (_mapsDirty) RebuildMaps();
        if (w.Aumid != null && _byParsing.TryGetValue(w.Aumid, out var a)) return a;
        if (w.ExePath == null) return null;
        if (_byTarget.TryGetValue(w.ExePath, out a)) return a;
        return _byExeName.TryGetValue(Path.GetFileName(w.ExePath), out var byName) ? byName : null;
    }

    void RebuildMaps()
    {
        _mapsDirty = false;
        var byParsing = new Dictionary<string, AppEntry>(StringComparer.OrdinalIgnoreCase);
        var byTarget = new Dictionary<string, AppEntry>(StringComparer.OrdinalIgnoreCase);
        var byName = new Dictionary<string, AppEntry?>(StringComparer.OrdinalIgnoreCase);
        foreach (var a in App.Apps)
        {
            byParsing.TryAdd(a.ParsingName, a);
            if (a.TargetPath is not { } t || !t.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) continue;
            byTarget.TryAdd(t, a);
            string n = Path.GetFileName(t);
            // An exe name shared by several apps (e.g. Squirrel's Update.exe) is ambiguous: no match.
            byName[n] = byName.TryGetValue(n, out var existing) && existing != a ? null : a;
        }
        // File Explorer's entry has no link target; its windows belong to explorer.exe.
        if (byParsing.TryGetValue("Microsoft.Windows.Explorer", out var explorer)) byName["explorer.exe"] = explorer;
        _byParsing = byParsing; _byTarget = byTarget; _byExeName = byName;
    }

    void WatchWindows()
    {
        var current = App.Tracker.Windows.ToHashSet();
        foreach (var w in _watched.Where(w => !current.Contains(w)).ToList())
        {
            w.PropertyChanged -= OnWindowChanged;
            _watched.Remove(w);
        }
        foreach (var w in current)
            if (_watched.Add(w)) w.PropertyChanged += OnWindowChanged;
    }

    void OnWindowChanged(object? s, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(TrackedWindow.IsActive) or nameof(TrackedWindow.IsMinimized)
            or nameof(TrackedWindow.NeedsAttention) or nameof(TrackedWindow.Icon)) Queue();
    }

    public void RefreshNow()
    {
        _queued = false;
        if (_mapsDirty) RebuildMaps();
        WatchWindows();

        var desired = new List<(string Key, AppEntry? Entry, bool Pinned, List<TrackedWindow> Windows)>();
        var index = new Dictionary<string, int>();
        foreach (var p in App.Pinned)
        {
            string k = "app:" + p.ParsingName;
            if (index.TryAdd(k, desired.Count)) desired.Add((k, p, true, new()));
        }
        foreach (var w in App.Tracker.Windows)
        {
            var entry = Match(w);
            string k = entry != null ? "app:" + entry.ParsingName : w.GroupKey;
            if (!index.TryGetValue(k, out int i)) { i = desired.Count; index[k] = i; desired.Add((k, entry, false, new())); }
            desired[i].Windows.Add(w);
        }

        var keep = index.Keys.ToHashSet();
        foreach (var item in Items.ToList())
        {
            if (keep.Contains(item.Key) || item.Leaving) continue;
            item.Leaving = true;
            void Remove() { if (item.Leaving) Items.Remove(item); }
            if (AnimateOut != null) AnimateOut(item, Remove); else Remove();
        }

        DockItem? prev = null;
        foreach (var d in desired)
        {
            var item = Items.FirstOrDefault(x => x.Key == d.Key);
            int want = prev == null ? 0 : Items.IndexOf(prev) + 1;
            if (item == null)
            {
                item = new DockItem(d.Key);
                Items.Insert(want, item);
            }
            else
            {
                if (item.Leaving) { item.Leaving = false; Revived?.Invoke(item); }
                int cur = Items.IndexOf(item);
                if (cur != want) Items.Move(cur, want > cur ? want - 1 : want);
            }
            item.Update(d.Entry, d.Pinned, d.Windows);
            prev = item;
        }
    }
}
