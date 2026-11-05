using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Norrsken.Shell.Core;
using Path = System.Windows.Shapes.Path;

namespace Norrsken.Shell.Ui;

/// <summary>One terminal: draws a <see cref="TerminalScreen"/> and turns keys, mouse and paste into input for its program.</summary>
public sealed class TerminalView : FrameworkElement
{
    public TerminalScreen Screen { get; }
    public PseudoConsole? Pty { get; private set; }
    public string Profile { get; }
    public event Action? TitleChanged;
    public event Action? Ended;

    double _fontSize = 14;
    GlyphTypeface? _regular, _bold;
    Typeface _face = new("Cascadia Mono");
    double _cellW = 8, _cellH = 17, _baseline = 13;
    int _scroll; // lines scrolled back from the bottom
    bool _renderQueued, _cursorOn = true, _ended;
    (int Line, int Col)? _selA, _selB;
    readonly DispatcherTimer _blink = new() { Interval = TimeSpan.FromMilliseconds(530) };
    readonly object _gate = new();
    string _lastTitle = "";

    static readonly Color[] Ansi =
    [
        Rgb(0x2B2B2B), Rgb(0xE06C75), Rgb(0x98C379), Rgb(0xE5C07B), Rgb(0x61AFEF), Rgb(0xC678DD), Rgb(0x56B6C2), Rgb(0xD4D4D4),
        Rgb(0x6B6B6B), Rgb(0xFF7A85), Rgb(0xB5E890), Rgb(0xFFD580), Rgb(0x82C4FF), Rgb(0xE59DF5), Rgb(0x7FD4DE), Rgb(0xFFFFFF),
    ];
    static Color Rgb(int v) => Color.FromRgb((byte)(v >> 16), (byte)(v >> 8), (byte)v);

    public TerminalView(string profile, string commandLine, string? workingDir)
    {
        Profile = profile;
        Focusable = true;
        FocusVisualStyle = null;
        Cursor = Cursors.IBeam;
        SetFont(_fontSize);
        Screen = new TerminalScreen(100, 30);
        Screen.Reply += s => Pty?.Write(s);
        _blink.Tick += (_, _) => { _cursorOn = !_cursorOn; InvalidateVisual(); };
        GotKeyboardFocus += (_, _) => { _cursorOn = true; _blink.Start(); InvalidateVisual(); };
        LostKeyboardFocus += (_, _) => { _blink.Stop(); _cursorOn = false; InvalidateVisual(); };
        Loaded += (_, _) => Start(commandLine, workingDir);
        Unloaded += (_, _) => _blink.Stop();
    }

    void Start(string commandLine, string? dir)
    {
        if (Pty != null) return;
        var (cols, rows) = GridSize();
        Screen.Resize(cols, rows);
        try
        {
            Pty = new PseudoConsole(commandLine, dir, cols, rows);
            Pty.Output += (buf, n) =>
            {
                lock (_gate) Screen.Feed(buf, n);
                QueueRender();
            };
            Pty.Exited += code => Dispatcher.BeginInvoke(() =>
            {
                _ended = true;
                lock (_gate) Screen.Feed($"\r\n\x1b[2m[process exited with code {code} · press Enter to close this tab]\x1b[0m");
                InvalidateVisual();
            });
        }
        catch (Exception ex)
        {
            lock (_gate) Screen.Feed($"\x1b[31mCouldn't start {commandLine}: {ex.Message}\x1b[0m\r\n");
            _ended = true;
            InvalidateVisual();
        }
    }

    public void SetFont(double size)
    {
        _fontSize = Math.Clamp(size, 8, 40);
        foreach (var family in new[] { "Cascadia Mono", "Cascadia Code", "Consolas" })
        {
            var tf = new Typeface(new FontFamily(family), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
            if (tf.TryGetGlyphTypeface(out var g))
            {
                _face = tf; _regular = g;
                new Typeface(new FontFamily(family), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal).TryGetGlyphTypeface(out _bold);
                break;
            }
        }
        if (_regular != null)
        {
            _cellW = Math.Round(_regular.AdvanceWidths[_regular.CharacterToGlyphMap['M']] * _fontSize, 2);
            _cellH = Math.Ceiling(_regular.Height * _fontSize * 1.08);
            _baseline = Math.Round(_regular.Baseline * _fontSize + (_cellH - _regular.Height * _fontSize) / 2);
        }
        OnSizeChanged();
        InvalidateVisual();
    }

    public double FontSizePoints => _fontSize;

    (int Cols, int Rows) GridSize() => (Math.Max(20, (int)((ActualWidth > 0 ? ActualWidth : 800) / _cellW)), Math.Max(5, (int)((ActualHeight > 0 ? ActualHeight : 480) / _cellH)));

    protected override void OnRenderSizeChanged(SizeChangedInfo info)
    {
        base.OnRenderSizeChanged(info);
        OnSizeChanged();
    }

    void OnSizeChanged()
    {
        if (Screen == null) return;
        var (cols, rows) = GridSize();
        lock (_gate) Screen.Resize(cols, rows);
        Pty?.Resize(cols, rows);
    }

    void QueueRender()
    {
        if (_renderQueued) return;
        _renderQueued = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Render, () =>
        {
            _renderQueued = false;
            _scroll = 0;
            _cursorOn = true;
            if (Screen.Title != _lastTitle) { _lastTitle = Screen.Title; TitleChanged?.Invoke(); }
            InvalidateVisual();
        });
    }

    public string Title => _lastTitle;

    // ───────────────────────── drawing ─────────────────────────

    Brush Fg => (Brush)FindResource("Text");
    readonly Dictionary<int, SolidColorBrush> _brushes = new();

    Brush BrushOf(int color, bool fg, byte attr)
    {
        if (color == -1) return fg ? Fg : Brushes.Transparent;
        if (fg && (attr & 1) != 0 && color < 8) color += 8; // bold brightens the basic colours
        if (_brushes.TryGetValue(color, out var b)) return b;
        Color c = color switch
        {
            < 16 => Ansi[color],
            < 232 => Color.FromRgb(Cube((color - 16) / 36), Cube((color - 16) / 6 % 6), Cube((color - 16) % 6)),
            < 256 => Color.FromRgb((byte)(8 + (color - 232) * 10), (byte)(8 + (color - 232) * 10), (byte)(8 + (color - 232) * 10)),
            _ => Color.FromRgb((byte)(color >> 16), (byte)(color >> 8), (byte)color),
        };
        b = new SolidColorBrush(c);
        b.Freeze();
        return _brushes[color] = b;
    }

    static byte Cube(int i) => (byte)(i == 0 ? 0 : 55 + i * 40);

    protected override void OnRender(DrawingContext dc)
    {
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(RenderSize));
        if (_regular == null) return;
        lock (_gate)
        {
            int rows = Screen.Rows, sb = Screen.Scrollback.Count;
            int first = sb - _scroll;
            var selBrush = (Brush)FindResource("Selection");
            for (int r = 0; r < rows; r++)
            {
                int abs = first + r;
                Cell[] line = abs < sb ? Screen.Scrollback[abs] : abs - sb < rows ? Screen.Line(abs - sb) : [];
                double y = r * _cellH;
                int x = 0;
                while (x < line.Length)
                {
                    var c = line[x];
                    int end = x + 1;
                    while (end < line.Length && line[end].Fg == c.Fg && line[end].Bg == c.Bg && line[end].Attr == c.Attr) end++;
                    bool inverse = (c.Attr & 16) != 0;
                    Brush fg = BrushOf(inverse ? (c.Bg == -1 ? 0 : c.Bg) : c.Fg, true, c.Attr);
                    Brush bg = inverse ? (c.Fg == -1 ? Fg : BrushOf(c.Fg, false, 0)) : BrushOf(c.Bg, false, 0);
                    if (inverse && c.Bg == -1) fg = (Brush)FindResource("SurfaceSolid");
                    if (bg != Brushes.Transparent) dc.DrawRectangle(bg, null, new Rect(x * _cellW, y, (end - x) * _cellW, _cellH));
                    DrawRun(dc, line, x, end, y, fg, c.Attr);
                    x = end;
                }
                if (Selected(abs) is { } span) dc.DrawRectangle(selBrush, null, new Rect(span.From * _cellW, y, (span.To - span.From) * _cellW, _cellH));
            }
            // Cursor: a soft block that blinks while the terminal has focus.
            if (Screen.CursorVisible && _scroll == 0 && !_ended)
            {
                var rect = new Rect(Screen.CursorX * _cellW, Screen.CursorY * _cellH, _cellW, _cellH);
                if (IsKeyboardFocused && _cursorOn) { var b = Fg.Clone(); b.Opacity = 0.75; dc.DrawRectangle(b, null, rect); }
                else if (!IsKeyboardFocused) dc.DrawRectangle(null, new Pen(Fg, 1), new Rect(rect.X + 0.5, rect.Y + 0.5, rect.Width - 1, rect.Height - 1));
            }
        }
    }

    void DrawRun(DrawingContext dc, Cell[] line, int from, int to, double y, Brush fg, byte attr)
    {
        var face = (attr & 1) != 0 && _bold != null ? _bold : _regular!;
        var glyphs = new List<ushort>();
        var advances = new List<double>();
        int start = from;
        double size = _fontSize;
        void Flush(int at)
        {
            if (glyphs.Count == 0) return;
            var run = new GlyphRun(face, 0, false, size, (float)VisualTreeHelper.GetDpi(this).PixelsPerDip, glyphs.ToArray(), new Point(start * _cellW, y + _baseline), advances.ToArray(), null, null, null, null, null, null);
            var brush = fg;
            if ((attr & 2) != 0) { brush = fg.Clone(); brush.Opacity = 0.6; }
            dc.DrawGlyphRun(brush, run);
            glyphs.Clear(); advances.Clear();
        }
        for (int x = from; x < to; x++)
        {
            char ch = line[x].Ch;
            if (ch == ' ' || ch == '\0') { Flush(x); start = x + 1; continue; }
            if (face.CharacterToGlyphMap.TryGetValue(ch, out ushort g)) { if (glyphs.Count == 0) start = x; glyphs.Add(g); advances.Add(_cellW); }
            else
            {
                Flush(x);
                // Characters the font lacks (emoji, some CJK): draw with Windows' font fallback.
                var ft = new FormattedText(ch.ToString(), CultureInfo.InvariantCulture, FlowDirection.LeftToRight, _face, size, fg, VisualTreeHelper.GetDpi(this).PixelsPerDip);
                dc.DrawText(ft, new Point(x * _cellW, y + (_cellH - ft.Height) / 2));
                start = x + 1;
            }
        }
        Flush(to);
        if ((attr & 8) != 0) dc.DrawLine(new Pen(fg, 1), new Point(from * _cellW, y + _cellH - 1.5), new Point(to * _cellW, y + _cellH - 1.5));
    }

    // ───────────────────────── selection, scrolling ─────────────────────────

    (int From, int To)? Selected(int abs)
    {
        if (_selA is not { } a || _selB is not { } b) return null;
        if ((b.Line, b.Col).CompareTo((a.Line, a.Col)) < 0) (a, b) = (b, a);
        if (abs < a.Line || abs > b.Line) return null;
        int from = abs == a.Line ? a.Col : 0, to = abs == b.Line ? b.Col + 1 : Screen.Cols;
        return from < to ? (from, to) : null;
    }

    (int Line, int Col) Hit(Point p) => (Screen.Scrollback.Count - _scroll + Math.Clamp((int)(p.Y / _cellH), 0, Screen.Rows - 1), Math.Clamp((int)(p.X / _cellW), 0, Screen.Cols - 1));

    protected override void OnMouseDown(MouseButtonEventArgs e)
    {
        Focus();
        if (e.ChangedButton == MouseButton.Left) { _selA = _selB = Hit(e.GetPosition(this)); CaptureMouse(); if (e.ClickCount == 2) SelectWord(); InvalidateVisual(); }
        else if (e.ChangedButton == MouseButton.Right) { if (HasSelection) { Copy(); _selA = _selB = null; InvalidateVisual(); } else Paste(); }
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (IsMouseCaptured && e.LeftButton == MouseButtonState.Pressed) { _selB = Hit(e.GetPosition(this)); InvalidateVisual(); }
    }

    protected override void OnMouseUp(MouseButtonEventArgs e)
    {
        ReleaseMouseCapture();
        if (_selA == _selB && e.ClickCount == 1) { _selA = _selB = null; InvalidateVisual(); }
    }

    void SelectWord()
    {
        if (_selA is not { } a) return;
        var line = LineAt(a.Line);
        int l = a.Col, r = a.Col;
        while (l > 0 && !char.IsWhiteSpace(line[l - 1].Ch)) l--;
        while (r < line.Length - 1 && !char.IsWhiteSpace(line[r + 1].Ch)) r++;
        _selA = (a.Line, l); _selB = (a.Line, r);
    }

    Cell[] LineAt(int abs) { int sb = Screen.Scrollback.Count; return abs < sb ? Screen.Scrollback[abs] : abs - sb < Screen.Rows ? Screen.Line(abs - sb) : []; }

    bool HasSelection => _selA != null && _selB != null && _selA != _selB;

    public void Copy()
    {
        if (_selA is not { } a || _selB is not { } b) return;
        if ((b.Line, b.Col).CompareTo((a.Line, a.Col)) < 0) (a, b) = (b, a);
        var lines = new List<string>();
        lock (_gate)
            for (int abs = a.Line; abs <= b.Line; abs++)
                lines.Add(TerminalScreen.TextOf(LineAt(abs), abs == a.Line ? a.Col : 0, abs == b.Line ? b.Col + 1 : Screen.Cols));
        try { Clipboard.SetText(string.Join(Environment.NewLine, lines)); } catch (Exception) { }
    }

    public void Paste()
    {
        string text;
        try { text = Clipboard.GetText(); } catch (Exception) { return; }
        if (text.Length == 0) return;
        text = text.Replace("\r\n", "\r").Replace('\n', '\r');
        Send(Screen.BracketedPaste ? "\x1b[200~" + text + "\x1b[201~" : text);
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) { SetFont(_fontSize + Math.Sign(e.Delta)); e.Handled = true; return; }
        _scroll = Math.Clamp(_scroll + Math.Sign(e.Delta) * 3, 0, Screen.Scrollback.Count);
        InvalidateVisual();
        e.Handled = true;
    }

    // ───────────────────────── keyboard ─────────────────────────

    void Send(string s)
    {
        if (_scroll != 0) { _scroll = 0; InvalidateVisual(); }
        if (_selA != null) { _selA = _selB = null; InvalidateVisual(); }
        Pty?.Write(s);
    }

    protected override void OnTextInput(TextCompositionEventArgs e)
    {
        if (e.Text.Length > 0 && !char.IsControl(e.Text[0])) { Send(e.Text); e.Handled = true; }
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        var mods = Keyboard.Modifiers;
        bool ctrl = mods.HasFlag(ModifierKeys.Control), shift = mods.HasFlag(ModifierKeys.Shift), alt = mods.HasFlag(ModifierKeys.Alt);
        if (_ended && key == Key.Enter) { Ended?.Invoke(); e.Handled = true; return; }
        if (ctrl && shift && key == Key.C) { Copy(); e.Handled = true; return; }
        if (ctrl && key == Key.V || shift && key == Key.Insert) { Paste(); e.Handled = true; return; }
        if (ctrl && key == Key.C && HasSelection) { Copy(); _selA = _selB = null; InvalidateVisual(); e.Handled = true; return; }
        if (shift && key is Key.PageUp or Key.PageDown) { _scroll = Math.Clamp(_scroll + (key == Key.PageUp ? Screen.Rows - 1 : -(Screen.Rows - 1)), 0, Screen.Scrollback.Count); InvalidateVisual(); e.Handled = true; return; }
        if (ctrl && key is Key.OemPlus or Key.Add) { SetFont(_fontSize + 1); e.Handled = true; return; }
        if (ctrl && key is Key.OemMinus or Key.Subtract) { SetFont(_fontSize - 1); e.Handled = true; return; }
        if (ctrl && key is Key.D0 or Key.NumPad0) { SetFont(14); e.Handled = true; return; }

        int m = 1 + (shift ? 1 : 0) + (alt ? 2 : 0) + (ctrl ? 4 : 0);
        string Mod(string plain, string code, char final) => m == 1 ? plain : $"\x1b[1;{m}{final}";
        string? seq = key switch
        {
            Key.Enter => alt ? "\x1b\r" : "\r",
            Key.Back => ctrl ? "\x08" : alt ? "\x1b\x7f" : "\x7f",
            Key.Tab => shift ? "\x1b[Z" : "\t",
            Key.Escape => "\x1b",
            Key.Up => Mod(Screen.AppCursorKeys ? "\x1bOA" : "\x1b[A", "A", 'A'),
            Key.Down => Mod(Screen.AppCursorKeys ? "\x1bOB" : "\x1b[B", "B", 'B'),
            Key.Right => Mod(Screen.AppCursorKeys ? "\x1bOC" : "\x1b[C", "C", 'C'),
            Key.Left => Mod(Screen.AppCursorKeys ? "\x1bOD" : "\x1b[D", "D", 'D'),
            Key.Home => Mod("\x1b[H", "H", 'H'),
            Key.End => Mod("\x1b[F", "F", 'F'),
            Key.PageUp => "\x1b[5~",
            Key.PageDown => "\x1b[6~",
            Key.Insert => "\x1b[2~",
            Key.Delete => m == 1 ? "\x1b[3~" : $"\x1b[3;{m}~",
            Key.F1 => "\x1bOP", Key.F2 => "\x1bOQ", Key.F3 => "\x1bOR", Key.F4 => "\x1bOS",
            Key.F5 => "\x1b[15~", Key.F6 => "\x1b[17~", Key.F7 => "\x1b[18~", Key.F8 => "\x1b[19~",
            Key.F9 => "\x1b[20~", Key.F10 => "\x1b[21~", Key.F11 => "\x1b[23~", Key.F12 => "\x1b[24~",
            _ => null,
        };
        if (seq == null && ctrl && !alt)
        {
            if (key is >= Key.A and <= Key.Z) seq = ((char)(key - Key.A + 1)).ToString();
            else if (key == Key.Space || key == Key.D2) seq = "\0";
            else if (key == Key.OemOpenBrackets) seq = "\x1b";
            else if (key == Key.Oem5) seq = "\x1c";
            else if (key == Key.OemCloseBrackets) seq = "\x1d";
        }
        else if (seq == null && alt && !ctrl && key is >= Key.A and <= Key.Z)
            seq = "\x1b" + (char)((shift ? 'A' : 'a') + (key - Key.A));
        if (seq != null) { Send(seq); e.Handled = true; }
    }

    public void Close() { Pty?.Dispose(); _blink.Stop(); }
}

/// <summary>
/// Norrsken Terminal: tabs of PowerShell, Command Prompt, WSL, Git Bash or anything else, on
/// Windows' own pseudo-console, in Norrsken's calm frame. Ctrl+Shift+T new tab, Ctrl+Shift+W
/// close, Ctrl+Tab next, Ctrl+scroll or Ctrl+± font size, select to copy, right-click to paste.
/// </summary>
public sealed class TerminalWindow : Window
{
    public sealed record Shell(string Name, string Command);

    readonly Border _root = new();
    readonly StackPanel _tabs = new() { Orientation = Orientation.Horizontal };
    readonly Grid _host = new() { Margin = new Thickness(14, 4, 8, 10) };
    readonly List<TerminalView> _views = new();
    TerminalView? _active;
    readonly string? _startDir;

    public static List<Shell> Shells()
    {
        var list = new List<Shell>();
        string? pwsh = new[] { @"C:\Program Files\PowerShell\7\pwsh.exe", @"C:\Program Files\PowerShell\7-preview\pwsh.exe" }.FirstOrDefault(File.Exists);
        if (pwsh != null) list.Add(new("PowerShell", $"\"{pwsh}\" -NoLogo"));
        string sys = Environment.SystemDirectory;
        list.Add(new(pwsh != null ? "Windows PowerShell" : "PowerShell", System.IO.Path.Combine(sys, @"WindowsPowerShell\v1.0\powershell.exe") + " -NoLogo"));
        list.Add(new("Command Prompt", System.IO.Path.Combine(sys, "cmd.exe")));
        if (File.Exists(System.IO.Path.Combine(sys, "wsl.exe")) && Directory.Exists(@"C:\Windows\System32\lxss") | Directory.Exists(Environment.ExpandEnvironmentVariables(@"%LOCALAPPDATA%\Packages")))
            list.Add(new("WSL", System.IO.Path.Combine(sys, "wsl.exe") + " ~"));
        string gitBash = @"C:\Program Files\Git\bin\bash.exe";
        if (File.Exists(gitBash)) list.Add(new("Git Bash", $"\"{gitBash}\" --login -i"));
        return list;
    }

    public TerminalWindow(string? startDir)
    {
        _startDir = startDir != null && Directory.Exists(startDir) ? startDir : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        Title = Unelevated.IsElevated ? "Terminal (administrator)" : "Terminal";
        Width = 960; Height = 600; MinWidth = 420; MinHeight = 240;
        WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.CanResize; WindowStartupLocation = WindowStartupLocation.CenterScreen;
        FontFamily = (FontFamily)FindResource("UiFont");
        SetResourceReference(BackgroundProperty, "SurfaceSolid");
        System.Windows.Shell.WindowChrome.SetWindowChrome(this, new System.Windows.Shell.WindowChrome { CaptionHeight = 46, ResizeBorderThickness = new Thickness(6), GlassFrameThickness = new Thickness(0), UseAeroCaptionButtons = false });

        var bar = new DockPanel { Height = 46, Margin = new Thickness(10, 0, 0, 0) };
        var pebbles = new WindowPebbles { VerticalAlignment = VerticalAlignment.Center };
        DockPanel.SetDock(pebbles, Dock.Right);
        bar.Children.Add(pebbles);
        var add = new Button { Style = (Style)FindResource("IconButton"), Width = 30, Height = 30, ToolTip = "New tab (Ctrl+Shift+T) · right-click for shells", Margin = new Thickness(4, 0, 0, 0) };
        var plus = new Path { Data = Geometry.Parse("M12,5 V19 M5,12 H19"), StrokeThickness = 1.7, Width = 11, Height = 11, Stretch = Stretch.Uniform, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round };
        plus.SetBinding(System.Windows.Shapes.Shape.StrokeProperty, new System.Windows.Data.Binding("Foreground") { Source = add });
        add.Content = plus;
        add.Click += (_, _) => ShellMenu(add);
        System.Windows.Shell.WindowChrome.SetIsHitTestVisibleInChrome(add, true);
        System.Windows.Shell.WindowChrome.SetIsHitTestVisibleInChrome(_tabs, true);
        var left = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        left.Children.Add(_tabs);
        left.Children.Add(add);
        bar.Children.Add(left);

        var dock = new DockPanel();
        DockPanel.SetDock(bar, Dock.Top);
        dock.Children.Add(bar);
        dock.Children.Add(_host);
        _root.Child = dock;
        _root.BorderThickness = new Thickness(1);
        _root.SetResourceReference(Border.BorderBrushProperty, Unelevated.IsElevated ? "TextDim" : "Border");
        _root.SetResourceReference(Border.BackgroundProperty, "SurfaceSolid");
        Content = _root;

        PreviewKeyDown += Keys;
        Closed += (_, _) => { foreach (var v in _views) v.Close(); };
        Loaded += (_, _) => NewTab(Shells()[0]);
        AppChrome.Attach(this, _root);
    }

    internal TerminalView? Active => _active;
    internal int TabCount => _views.Count;

    void ShellMenu(FrameworkElement anchor)
    {
        var m = new ContextMenu { PlacementTarget = anchor, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
        foreach (var s in Shells())
        {
            var mi = new MenuItem { Header = s.Name };
            mi.Click += (_, _) => NewTab(s);
            m.Items.Add(mi);
        }
        m.Items.Add(new Separator());
        var admin = new MenuItem { Header = "New window as administrator", IsEnabled = !Unelevated.IsElevated };
        admin.Click += (_, _) => { try { Process.Start(new ProcessStartInfo(Environment.ProcessPath!, "--app terminal") { UseShellExecute = true, Verb = "runas" }); } catch (Exception) { } };
        m.Items.Add(admin);
        m.IsOpen = true;
    }

    public void NewTab(Shell shell)
    {
        var view = new TerminalView(shell.Name, shell.Command, _startDir);
        view.TitleChanged += RenderTabs;
        view.Ended += () => CloseTab(view);
        _views.Add(view);
        _host.Children.Add(view);
        Activate(view);
    }

    void Activate(TerminalView view)
    {
        _active = view;
        foreach (var v in _views) v.Visibility = v == view ? Visibility.Visible : Visibility.Hidden;
        RenderTabs();
        Dispatcher.BeginInvoke(DispatcherPriority.Input, () => view.Focus());
    }

    void CloseTab(TerminalView view)
    {
        int i = _views.IndexOf(view);
        view.Close();
        _views.Remove(view);
        _host.Children.Remove(view);
        if (_views.Count == 0) { Close(); return; }
        Activate(_views[Math.Min(i, _views.Count - 1)]);
    }

    void RenderTabs()
    {
        _tabs.Children.Clear();
        foreach (var v in _views)
        {
            // Shells often title themselves with their own path; show the shell's name then.
            string title = v.Title.Length > 0 && !v.Title.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? v.Title : v.Profile;
            if (title.Length > 34) title = "…" + title[^33..];
            var t = new TextBlock { Text = title, FontSize = 12.5, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 4, 0), MaxWidth = 220, TextTrimming = TextTrimming.CharacterEllipsis };
            t.SetResourceReference(TextBlock.ForegroundProperty, v == _active ? "Text" : "TextDim");
            var close = new Button { Style = (Style)FindResource("IconButton"), Width = 20, Height = 20, Margin = new Thickness(2, 0, 6, 0), ToolTip = "Close tab (Ctrl+Shift+W)" };
            var x = new Path { Data = Geometry.Parse("M7,7 L17,17 M17,7 L7,17"), StrokeThickness = 1.6, Width = 7, Height = 7, Stretch = Stretch.Uniform, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round };
            x.SetBinding(System.Windows.Shapes.Shape.StrokeProperty, new System.Windows.Data.Binding("Foreground") { Source = close });
            close.Content = x;
            var view = v;
            close.Click += (_, _) => CloseTab(view);
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            row.Children.Add(t);
            row.Children.Add(close);
            var pill = new Border { CornerRadius = Theme.R(9), Height = 30, Margin = new Thickness(0, 0, 4, 0), Child = row, Cursor = Cursors.Hand, BorderThickness = new Thickness(1) };
            if (v == _active) { pill.SetResourceReference(Border.BackgroundProperty, "SurfaceAlt"); pill.SetResourceReference(Border.BorderBrushProperty, "Border"); }
            else { pill.Background = Brushes.Transparent; pill.BorderBrush = Brushes.Transparent; }
            pill.MouseLeftButtonUp += (_, _) => Activate(view);
            pill.MouseUp += (_, e) => { if (e.ChangedButton == MouseButton.Middle) CloseTab(view); };
            _tabs.Children.Add(pill);
        }
        Title = (_active?.Title is { Length: > 0 } at ? at : _active?.Profile ?? "Terminal") + (Unelevated.IsElevated ? " (administrator)" : "") + " — Terminal";
    }

    void Keys(object sender, KeyEventArgs e)
    {
        var mods = Keyboard.Modifiers;
        bool ctrl = mods.HasFlag(ModifierKeys.Control), shift = mods.HasFlag(ModifierKeys.Shift);
        if (ctrl && shift && e.Key == Key.T) { NewTab(Shells()[0]); e.Handled = true; }
        else if (ctrl && shift && e.Key == Key.W && _active != null) { CloseTab(_active); e.Handled = true; }
        else if (ctrl && e.Key == Key.Tab && _views.Count > 1 && _active != null)
        {
            int i = (_views.IndexOf(_active) + (shift ? _views.Count - 1 : 1)) % _views.Count;
            Activate(_views[i]);
            e.Handled = true;
        }
        else if (ctrl && shift && e.Key >= Key.D1 && e.Key <= Key.D9)
        {
            var shells = Shells();
            int i = e.Key - Key.D1;
            if (i < shells.Count) NewTab(shells[i]);
            e.Handled = true;
        }
    }
}
