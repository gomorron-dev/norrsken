using System.Collections.ObjectModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;

namespace Norrsken.Shell.Core;

/// <summary>
/// Norrsken's clipboard history (Win+V): the last 30 things you copied, text and pictures, kept in
/// memory only (nothing is written to disk). Pinned items survive "Clear".
/// </summary>
public static class ClipboardHistory
{
    public sealed class Entry
    {
        public string? Text { get; init; }
        public BitmapSource? Image { get; init; }
        public DateTime When { get; init; } = DateTime.Now;
        public bool Pinned { get; set; }
        public string Preview => Text != null ? (Text.Length > 300 ? Text[..300] + "…" : Text) : "Picture";
    }

    public static ObservableCollection<Entry> Items { get; } = new();
    static HwndSource? _listener;
    static bool _ignoreNext;
    const int WM_CLIPBOARDUPDATE = 0x031D, Max = 30;

    [DllImport("user32.dll")] static extern bool AddClipboardFormatListener(IntPtr hwnd);

    public static void Start()
    {
        if (_listener != null || App.TestMode) return;
        _listener = new HwndSource(new HwndSourceParameters("Norrsken clipboard") { Width = 0, Height = 0, WindowStyle = 0, ParentWindow = new IntPtr(-3) /* HWND_MESSAGE */ });
        _listener.AddHook((IntPtr h, int msg, IntPtr w, IntPtr l, ref bool handled) =>
        {
            if (msg == WM_CLIPBOARDUPDATE) Application.Current.Dispatcher.BeginInvoke(Capture);
            return IntPtr.Zero;
        });
        AddClipboardFormatListener(_listener.Handle);
    }

    static void Capture()
    {
        if (_ignoreNext) { _ignoreNext = false; return; }
        try
        {
            Entry? e = null;
            if (Clipboard.ContainsText())
            {
                string t = Clipboard.GetText();
                if (string.IsNullOrWhiteSpace(t) || t.Length > 200_000) return;
                var same = Items.FirstOrDefault(i => i.Text == t);
                if (same != null) { Items.Remove(same); Items.Insert(0, same); return; }
                e = new Entry { Text = t };
            }
            else if (Clipboard.ContainsImage() && Clipboard.GetImage() is { } img)
            {
                var frozen = new WriteableBitmap(img);
                frozen.Freeze();
                e = new Entry { Image = frozen };
            }
            if (e == null) return;
            Items.Insert(0, e);
            while (Items.Count > Max && Items.LastOrDefault(i => !i.Pinned) is { } old) Items.Remove(old);
        }
        catch (Exception) { /* another app is holding the clipboard */ }
    }

    /// <summary>Puts an entry back on the clipboard (and moves it to the top).</summary>
    public static void Use(Entry e)
    {
        _ignoreNext = true;
        try
        {
            if (e.Text != null) Clipboard.SetText(e.Text); else if (e.Image != null) Clipboard.SetImage(e.Image);
        }
        catch (Exception) { _ignoreNext = false; }
        Items.Remove(e);
        Items.Insert(0, e);
    }

    public static void Clear()
    {
        foreach (var i in Items.Where(i => !i.Pinned).ToList()) Items.Remove(i);
    }

    /// <summary>UI tests.</summary>
    internal static void TestAdd(string text) => Items.Insert(0, new Entry { Text = text });
}
