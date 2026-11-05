using System.Globalization;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Win32;
using Norrsken.Shell.Core;

namespace Norrsken.Shell.Ui;

/// <summary>
/// Norrsken Notes: a calm plain-text editor (Notepad's job). Keeps each file's encoding and line
/// endings, find/replace, zoom, word wrap, time stamp (F5), drag-and-drop, and asks before losing changes.
/// </summary>
public partial class NotesWindow : Window
{
    string? _path;
    Encoding _encoding = new UTF8Encoding(false);
    string _eol = "\r\n";
    bool _dirty, _loading, _forceClose;
    double _zoom = 1;
    Action? _afterPrompt;
    const double BaseFont = 14.5;

    public NotesWindow(string? path = null)
    {
        InitializeComponent();
        Editor.TextWrapping = App.Settings.NotesWrap ? TextWrapping.Wrap : TextWrapping.NoWrap;
        if (path != null && File.Exists(path)) Load(path);
        UpdateTitle();
        UpdateStatus();
        Drop += (_, e) =>
        {
            if (e.Data.GetData(DataFormats.FileDrop) is string[] { Length: > 0 } files) Guard(() => Load(files[0]));
        };
        PreviewKeyDown += Keys;
        Loaded += (_, _) => Editor.Focus();
        AppChrome.Attach(this, Root);
    }

    internal TextBox EditorBox => Editor;
    internal bool Dirty => _dirty;
    internal string? FilePath => _path;

    // ───────────────────────── files ─────────────────────────

    internal void Load(string path)
    {
        try
        {
            byte[] bytes = File.ReadAllBytes(path);
            _encoding = Detect(bytes, out int bom);
            string text = _encoding.GetString(bytes, bom, bytes.Length - bom);
            _eol = text.Contains("\r\n") ? "\r\n" : text.Contains('\n') ? "\n" : "\r\n";
            _loading = true;
            Editor.Text = text;
            _loading = false;
            Editor.CaretIndex = 0;
            _path = path;
            _dirty = false;
            UpdateTitle();
            UpdateStatus();
        }
        catch (Exception ex) { Notify($"Couldn't open {Path.GetFileName(path)}: {ex.Message}"); }
    }

    static Encoding Detect(byte[] b, out int bom)
    {
        bom = 0;
        if (b.Length >= 3 && b[0] == 0xEF && b[1] == 0xBB && b[2] == 0xBF) { bom = 3; return new UTF8Encoding(true); }
        if (b.Length >= 2 && b[0] == 0xFF && b[1] == 0xFE) { bom = 2; return Encoding.Unicode; }
        if (b.Length >= 2 && b[0] == 0xFE && b[1] == 0xFF) { bom = 2; return Encoding.BigEndianUnicode; }
        try { new UTF8Encoding(false, true).GetString(b); return new UTF8Encoding(false); }
        catch (DecoderFallbackException) { return Encoding.Latin1; }
    }

    internal bool Save(bool saveAs = false)
    {
        string? target = _path;
        if (saveAs || target == null)
        {
            var dlg = new SaveFileDialog
            {
                Filter = "Text documents (*.txt)|*.txt|All files (*.*)|*.*",
                FileName = _path != null ? Path.GetFileName(_path) : "Untitled.txt",
                InitialDirectory = _path != null ? Path.GetDirectoryName(_path) : Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            };
            if (dlg.ShowDialog(this) != true) return false;
            target = dlg.FileName;
        }
        return SaveTo(target);
    }

    internal bool SaveTo(string target)
    {
        try
        {
            string text = Editor.Text.Replace("\r\n", "\n");
            if (_eol != "\n") text = text.Replace("\n", _eol);
            string tmp = target + ".norrsken-tmp";
            File.WriteAllText(tmp, text, _encoding);
            File.Move(tmp, target, overwrite: true);
            _path = target;
            _dirty = false;
            UpdateTitle();
            UpdateStatus();
            return true;
        }
        catch (Exception ex) { Notify($"Couldn't save: {ex.Message}"); return false; }
    }

    void Notify(string message) => MessageBox.Show(this, message, "Notes", MessageBoxButton.OK, MessageBoxImage.Warning);

    // ───────────────────────── unsaved changes ─────────────────────────

    /// <summary>Runs <paramref name="then"/> now, or after the user saves or discards changes.</summary>
    void Guard(Action then)
    {
        if (!_dirty) { then(); return; }
        _afterPrompt = then;
        PromptTitle.Text = $"Save changes to {DisplayName}?";
        Prompt.Visibility = Visibility.Visible;
        Motion.From(PromptCard, OpacityProperty, 0, 1, 160);
    }

    void HidePrompt() => Prompt.Visibility = Visibility.Collapsed;
    void PromptCancel_Click(object sender, RoutedEventArgs e) { _afterPrompt = null; HidePrompt(); Editor.Focus(); }
    void PromptDiscard_Click(object sender, RoutedEventArgs e) { HidePrompt(); _dirty = false; var a = _afterPrompt; _afterPrompt = null; a?.Invoke(); }
    void PromptSave_Click(object sender, RoutedEventArgs e)
    {
        HidePrompt();
        if (!Save()) return;
        var a = _afterPrompt; _afterPrompt = null; a?.Invoke();
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (_dirty && !_forceClose && !App.TestMode)
        {
            e.Cancel = true;
            Guard(() => { _forceClose = true; Close(); });
        }
        base.OnClosing(e);
    }

    // ───────────────────────── editor ─────────────────────────

    string DisplayName => _path != null ? Path.GetFileName(_path) : "Untitled";

    void UpdateTitle()
    {
        Title = (_dirty ? "• " : "") + DisplayName + " — Notes";
        TitleText.Text = (_dirty ? "• " : "") + DisplayName;
    }

    void Editor_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_loading) return;
        if (!_dirty) { _dirty = true; UpdateTitle(); }
        UpdateStatus();
        if (FindBar.Visibility == Visibility.Visible) CountMatches();
    }

    void Editor_SelectionChanged(object sender, RoutedEventArgs e) => UpdateStatus();

    void UpdateStatus()
    {
        int caret = Editor.CaretIndex;
        int line = Editor.GetLineIndexFromCharacterIndex(caret);
        int col = line >= 0 ? caret - Editor.GetCharacterIndexFromLineIndex(line) : caret;
        PosText.Text = $"Ln {Math.Max(line, 0) + 1}, Col {col + 1}";
        CountText.Text = Editor.SelectionLength > 0 ? $"{Editor.SelectionLength:N0} of {Editor.Text.Length:N0} characters" : $"{Editor.Text.Length:N0} characters";
        ZoomText.Text = $"{Math.Round(_zoom * 100)}%";
        EolText.Text = _eol == "\n" ? "Unix (LF)" : "Windows (CRLF)";
        EncodingText.Text = _encoding switch
        {
            UTF8Encoding u when u.GetPreamble().Length > 0 => "UTF-8 with BOM",
            UTF8Encoding => "UTF-8",
            UnicodeEncoding when _encoding.CodePage == 1201 => "UTF-16 BE",
            UnicodeEncoding => "UTF-16 LE",
            _ => _encoding.WebName.ToUpperInvariant(),
        };
    }

    void SetZoom(double z)
    {
        _zoom = Math.Clamp(z, 0.5, 4);
        Editor.FontSize = BaseFont * _zoom;
        UpdateStatus();
    }

    void Keys(object sender, KeyEventArgs e)
    {
        var mods = Keyboard.Modifiers;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        bool ctrl = mods.HasFlag(ModifierKeys.Control), shift = mods.HasFlag(ModifierKeys.Shift);
        if (Prompt.Visibility == Visibility.Visible)
        {
            if (key == Key.Escape) { PromptCancel_Click(this, e); e.Handled = true; }
            return;
        }
        switch (key)
        {
            case Key.N when ctrl: New_Click(this, e); break;
            case Key.O when ctrl: Open_Click(this, e); break;
            case Key.S when ctrl: Save(shift); break;
            case Key.W when ctrl: Close(); break;
            case Key.F when ctrl: ShowFind(false); break;
            case Key.H when ctrl: ShowFind(true); break;
            case Key.F3: Find(!shift); break;
            case Key.F5: Editor.SelectedText = DateTime.Now.ToString("g", CultureInfo.CurrentCulture); Editor.CaretIndex = Editor.SelectionStart + Editor.SelectionLength; Editor.SelectionLength = 0; break;
            case Key.OemPlus or Key.Add when ctrl: SetZoom(_zoom + 0.1); break;
            case Key.OemMinus or Key.Subtract when ctrl: SetZoom(_zoom - 0.1); break;
            case Key.D0 or Key.NumPad0 when ctrl: SetZoom(1); break;
            case Key.Escape when FindBar.Visibility == Visibility.Visible: CloseFind_Click(this, e); break;
            default: return;
        }
        e.Handled = true;
    }

    protected override void OnPreviewMouseWheel(MouseWheelEventArgs e)
    {
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) { SetZoom(_zoom + Math.Sign(e.Delta) * 0.1); e.Handled = true; }
        base.OnPreviewMouseWheel(e);
    }

    // ───────────────────────── commands ─────────────────────────

    void New_Click(object sender, RoutedEventArgs e) => NorrskenApps.Launch("notes");

    void Open_Click(object sender, RoutedEventArgs e) => Guard(() =>
    {
        var dlg = new OpenFileDialog { Filter = "Text documents (*.txt)|*.txt|All files (*.*)|*.*" };
        if (dlg.ShowDialog(this) == true) Load(dlg.FileName);
    });

    void Save_Click(object sender, RoutedEventArgs e) => Save();

    void More_Click(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu { PlacementTarget = MoreButton, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
        void Item(string header, Action a, bool enabled = true)
        {
            var mi = new MenuItem { Header = header, IsEnabled = enabled };
            mi.Click += (_, _) => a();
            menu.Items.Add(mi);
        }
        Item("Save as…", () => Save(true));
        Item("Find…", () => ShowFind(false));
        Item("Replace…", () => ShowFind(true));
        menu.Items.Add(new Separator());
        Item(Editor.TextWrapping == TextWrapping.Wrap ? "✓  Word wrap" : "     Word wrap", ToggleWrap);
        Item("Zoom in", () => SetZoom(_zoom + 0.1));
        Item("Zoom out", () => SetZoom(_zoom - 0.1));
        Item("Reset zoom", () => SetZoom(1));
        menu.Items.Add(new Separator());
        Item(_eol == "\n" ? "Use Windows line endings (CRLF)" : "Use Unix line endings (LF)", () => { _eol = _eol == "\n" ? "\r\n" : "\n"; _dirty = true; UpdateTitle(); UpdateStatus(); });
        Item("Save as UTF-8", () => { _encoding = new UTF8Encoding(false); _dirty = true; UpdateTitle(); UpdateStatus(); });
        Item("Insert time and date", () => { Editor.SelectedText = DateTime.Now.ToString("g", CultureInfo.CurrentCulture); });
        menu.Items.Add(new Separator());
        Item("Show in Files", () => NorrskenApps.Launch("files", _path), _path != null);
        menu.IsOpen = true;
    }

    void ToggleWrap()
    {
        Editor.TextWrapping = Editor.TextWrapping == TextWrapping.Wrap ? TextWrapping.NoWrap : TextWrapping.Wrap;
        App.Settings.NotesWrap = Editor.TextWrapping == TextWrapping.Wrap;
        try { App.Settings.Save(); } catch (IOException) { }
    }

    // ───────────────────────── find & replace ─────────────────────────

    void ShowFind(bool replace)
    {
        FindBar.Visibility = Visibility.Visible;
        ReplaceRow.Visibility = replace ? Visibility.Visible : Visibility.Collapsed;
        if (Editor.SelectionLength > 0 && !Editor.SelectedText.Contains('\n')) FindBox.Text = Editor.SelectedText;
        FindBox.Focus();
        FindBox.SelectAll();
        CountMatches();
    }

    void CloseFind_Click(object sender, RoutedEventArgs e) { FindBar.Visibility = Visibility.Collapsed; Editor.Focus(); }
    StringComparison Cmp => CaseToggle.IsChecked == true ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;

    void CountMatches()
    {
        string q = FindBox.Text;
        if (q.Length == 0) { MatchText.Text = ""; return; }
        int n = 0;
        for (int i = Editor.Text.IndexOf(q, Cmp); i >= 0; i = Editor.Text.IndexOf(q, i + q.Length, Cmp)) n++;
        MatchText.Text = n == 0 ? "No results" : $"{n} found";
    }

    void FindBox_TextChanged(object sender, TextChangedEventArgs e) => CountMatches();

    void FindBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { Find(!Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)); e.Handled = true; }
    }

    void FindNext_Click(object sender, RoutedEventArgs e) => Find(true);
    void FindPrev_Click(object sender, RoutedEventArgs e) => Find(false);

    internal bool Find(bool forward)
    {
        string q = FindBox.Text, text = Editor.Text;
        if (q.Length == 0) return false;
        int i = forward
            ? text.IndexOf(q, Math.Min(text.Length, Editor.SelectionStart + Editor.SelectionLength), Cmp)
            : (Editor.SelectionStart > 0 ? text.LastIndexOf(q, Editor.SelectionStart - 1, Cmp) : -1);
        if (i < 0) i = forward ? text.IndexOf(q, Cmp) : text.LastIndexOf(q, Cmp); // wrap around
        if (i < 0) return false;
        Editor.Select(i, q.Length);
        Editor.ScrollToLine(Editor.GetLineIndexFromCharacterIndex(i));
        return true;
    }

    void Replace_Click(object sender, RoutedEventArgs e)
    {
        if (Editor.SelectionLength > 0 && string.Equals(Editor.SelectedText, FindBox.Text, Cmp)) Editor.SelectedText = ReplaceBox.Text;
        Find(true);
    }

    internal void ReplaceAll_Click(object sender, RoutedEventArgs e)
    {
        string q = FindBox.Text;
        if (q.Length == 0) return;
        var sb = new StringBuilder();
        string text = Editor.Text;
        int last = 0, n = 0;
        for (int i = text.IndexOf(q, Cmp); i >= 0; i = text.IndexOf(q, i + q.Length, Cmp))
        {
            sb.Append(text, last, i - last).Append(ReplaceBox.Text);
            last = i + q.Length;
            n++;
        }
        if (n == 0) return;
        sb.Append(text, last, text.Length - last);
        Editor.SelectAll();
        Editor.SelectedText = sb.ToString(); // one undo step
        MatchText.Text = $"Replaced {n}";
    }
}
