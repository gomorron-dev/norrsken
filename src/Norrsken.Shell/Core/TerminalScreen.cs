using System.Text;

namespace Norrsken.Shell.Core;

/// <summary>One character cell: the character and its look (colours are 0–15 palette, 16–255 xterm, or 0x1RRGGBB true colour; -1 = default).</summary>
public struct Cell
{
    public char Ch;
    public int Fg, Bg;
    public byte Attr; // 1 bold, 2 dim, 4 italic, 8 underline, 16 inverse
    public static Cell Blank => new() { Ch = ' ', Fg = -1, Bg = -1 };
}

/// <summary>
/// The terminal's screen: a grid of cells, scrollback, and a VT/xterm parser covering what
/// PowerShell, PSReadLine, cmd, git, ssh, vim/nano and most TUIs use (cursor movement, erase,
/// insert/delete, scroll regions, SGR colours incl. 256 and true colour, alternate screen, titles).
/// </summary>
public sealed class TerminalScreen
{
    public int Cols { get; private set; }
    public int Rows { get; private set; }
    public int CursorX { get; private set; }
    public int CursorY { get; private set; }
    public bool CursorVisible { get; private set; } = true;
    public bool AppCursorKeys { get; private set; }
    public bool BracketedPaste { get; private set; }
    public string Title { get; private set; } = "";
    public List<Cell[]> Scrollback { get; } = new();
    public const int MaxScrollback = 5000;

    Cell[][] _main, _alt;
    bool _onAlt;
    Cell[][] Lines => _onAlt ? _alt : _main;
    int _top, _bottom; // scroll region (inclusive)
    Cell _pen = Cell.Blank;
    int _savedX, _savedY;
    Cell _savedPen = Cell.Blank;
    bool _wrapPending;
    readonly Decoder _utf8 = Encoding.UTF8.GetDecoder();
    readonly char[] _chars = new char[32768];

    enum State { Ground, Escape, Csi, Osc, OscEscape, Charset }
    State _state;
    readonly StringBuilder _params = new();
    readonly StringBuilder _osc = new();
    char _csiPrivate;

    /// <summary>Text the terminal must send back to the program (answers to status queries).</summary>
    public event Action<string>? Reply;

    public TerminalScreen(int cols, int rows)
    {
        Cols = cols; Rows = rows;
        _main = NewLines(rows, cols);
        _alt = NewLines(rows, cols);
        _top = 0; _bottom = rows - 1;
    }

    static Cell[][] NewLines(int rows, int cols) => Enumerable.Range(0, rows).Select(_ => NewLine(cols)).ToArray();
    static Cell[] NewLine(int cols) { var l = new Cell[cols]; Array.Fill(l, Cell.Blank); return l; }

    public Cell[] Line(int y) => Lines[y];

    public void Resize(int cols, int rows)
    {
        if (cols == Cols && rows == Rows) return;
        Cell[][] Fit(Cell[][] src, bool keepBottom)
        {
            var dst = NewLines(rows, cols);
            int offset = keepBottom ? Math.Max(0, CursorY + 1 - rows) : 0;
            for (int y = 0; y < rows && y + offset < src.Length; y++)
                Array.Copy(src[y + offset], dst[y], Math.Min(cols, src[y + offset].Length));
            if (keepBottom && offset > 0 && !_onAlt) for (int y = 0; y < offset; y++) PushScrollback(src[y]);
            return dst;
        }
        int shift = Math.Max(0, CursorY + 1 - rows);
        _main = Fit(_main, true);
        _alt = Fit(_alt, false);
        Cols = cols; Rows = rows;
        _top = 0; _bottom = rows - 1;
        CursorY = Math.Clamp(CursorY - shift, 0, rows - 1);
        CursorX = Math.Clamp(CursorX, 0, cols - 1);
        _wrapPending = false;
    }

    void PushScrollback(Cell[] line)
    {
        Scrollback.Add(line);
        if (Scrollback.Count > MaxScrollback) Scrollback.RemoveRange(0, Scrollback.Count - MaxScrollback);
    }

    public void Feed(byte[] data, int count)
    {
        int n = _utf8.GetChars(data, 0, count, _chars, 0);
        for (int i = 0; i < n; i++) Step(_chars[i]);
    }

    public void Feed(string s) { foreach (char c in s) Step(c); }

    void Step(char c)
    {
        switch (_state)
        {
            case State.Ground:
                if (c == '\x1b') { _state = State.Escape; return; }
                if (c < ' ' || c == '\x7f') { Control(c); return; }
                Print(c);
                return;
            case State.Escape:
                _state = State.Ground;
                switch (c)
                {
                    case '[': _state = State.Csi; _params.Clear(); _csiPrivate = '\0'; return;
                    case ']': _state = State.Osc; _osc.Clear(); return;
                    case '(' or ')' or '*' or '+': _state = State.Charset; return;
                    case '7': Save(); return;
                    case '8': Restore(); return;
                    case 'M': ReverseIndex(); return;
                    case 'D': LineFeed(); return;
                    case 'E': CursorX = 0; LineFeed(); return;
                    case 'c': Reset(); return;
                }
                return;
            case State.Charset:
                _state = State.Ground;
                return;
            case State.Csi:
                if (c is >= '0' and <= '9' or ';' or ':') { _params.Append(c); return; }
                if (c is '?' or '>' or '=' or '!' or ' ' or '"' or '$' or '\'') { _csiPrivate = c; return; }
                _state = State.Ground;
                Csi(c);
                return;
            case State.Osc:
                if (c == '\x07') { Osc(); _state = State.Ground; return; }
                if (c == '\x1b') { _state = State.OscEscape; return; }
                if (_osc.Length < 4096) _osc.Append(c);
                return;
            case State.OscEscape:
                Osc();
                _state = c == '\\' ? State.Ground : State.Escape;
                if (_state == State.Escape) Step(c);
                return;
        }
    }

    void Control(char c)
    {
        switch (c)
        {
            case '\r': CursorX = 0; _wrapPending = false; break;
            case '\n' or '\v' or '\f': LineFeed(); break;
            case '\b': if (CursorX > 0) CursorX--; _wrapPending = false; break;
            case '\t': CursorX = Math.Min(Cols - 1, (CursorX / 8 + 1) * 8); break;
        }
    }

    void Print(char c)
    {
        if (_wrapPending) { CursorX = 0; LineFeed(); _wrapPending = false; }
        var cell = _pen;
        cell.Ch = c;
        Lines[CursorY][CursorX] = cell;
        if (CursorX == Cols - 1) _wrapPending = true; else CursorX++;
    }

    void LineFeed()
    {
        _wrapPending = false;
        if (CursorY == _bottom) ScrollUp(1);
        else if (CursorY < Rows - 1) CursorY++;
    }

    void ReverseIndex()
    {
        if (CursorY == _top) ScrollDown(1); else if (CursorY > 0) CursorY--;
    }

    void ScrollUp(int n)
    {
        var lines = Lines;
        for (int k = 0; k < n; k++)
        {
            if (_top == 0 && !_onAlt) PushScrollback(lines[_top]);
            for (int y = _top; y < _bottom; y++) lines[y] = lines[y + 1];
            lines[_bottom] = BlankLine();
        }
    }

    void ScrollDown(int n)
    {
        var lines = Lines;
        for (int k = 0; k < n; k++)
        {
            for (int y = _bottom; y > _top; y--) lines[y] = lines[y - 1];
            lines[_top] = BlankLine();
        }
    }

    Cell[] BlankLine() { var l = NewLine(Cols); if (_pen.Bg != -1) for (int i = 0; i < l.Length; i++) l[i].Bg = _pen.Bg; return l; }
    Cell Erased => new() { Ch = ' ', Fg = -1, Bg = _pen.Bg };

    void Save() { _savedX = CursorX; _savedY = CursorY; _savedPen = _pen; }
    void Restore() { CursorX = Math.Min(_savedX, Cols - 1); CursorY = Math.Min(_savedY, Rows - 1); _pen = _savedPen; _wrapPending = false; }

    void Reset()
    {
        _main = NewLines(Rows, Cols); _alt = NewLines(Rows, Cols); _onAlt = false;
        CursorX = CursorY = 0; _pen = Cell.Blank; _top = 0; _bottom = Rows - 1; CursorVisible = true;
    }

    int[] Params(int defaults, int count)
    {
        var parts = _params.ToString().Split(';', ':');
        var r = new int[Math.Max(count, parts.Length)];
        for (int i = 0; i < r.Length; i++)
            r[i] = i < parts.Length && int.TryParse(parts[i], out int v) ? v : defaults;
        return r;
    }

    void Csi(char final)
    {
        var p = Params(0, 2);
        int n = Math.Max(1, p[0]);
        _wrapPending = false;
        if (_csiPrivate == '?')
        {
            if (final is 'h' or 'l') foreach (var mode in p) SetPrivateMode(mode, final == 'h');
            return;
        }
        if (_csiPrivate is '>' or '=' or '!' or ' ' or '"' or '$' or '\'') { if (_csiPrivate == '>' && final == 'c') Reply?.Invoke("\x1b[>0;10;1c"); return; }
        var lines = Lines;
        switch (final)
        {
            case 'A': CursorY = Math.Max(CursorY < _top ? 0 : _top, CursorY - n); break;
            case 'B' or 'e': CursorY = Math.Min(CursorY > _bottom ? Rows - 1 : _bottom, CursorY + n); break;
            case 'C' or 'a': CursorX = Math.Min(Cols - 1, CursorX + n); break;
            case 'D': CursorX = Math.Max(0, CursorX - n); break;
            case 'E': CursorX = 0; CursorY = Math.Min(Rows - 1, CursorY + n); break;
            case 'F': CursorX = 0; CursorY = Math.Max(0, CursorY - n); break;
            case 'G' or '`': CursorX = Math.Clamp(n - 1, 0, Cols - 1); break;
            case 'd': CursorY = Math.Clamp(n - 1, 0, Rows - 1); break;
            case 'H' or 'f':
                CursorY = Math.Clamp(Math.Max(1, p[0]) - 1, 0, Rows - 1);
                CursorX = Math.Clamp(Math.Max(1, p[1]) - 1, 0, Cols - 1);
                break;
            case 'J':
                if (p[0] == 0) { EraseLine(CursorY, CursorX, Cols); for (int y = CursorY + 1; y < Rows; y++) EraseLine(y, 0, Cols); }
                else if (p[0] == 1) { for (int y = 0; y < CursorY; y++) EraseLine(y, 0, Cols); EraseLine(CursorY, 0, CursorX + 1); }
                else { for (int y = 0; y < Rows; y++) EraseLine(y, 0, Cols); if (p[0] == 3) Scrollback.Clear(); }
                break;
            case 'K':
                if (p[0] == 0) EraseLine(CursorY, CursorX, Cols);
                else if (p[0] == 1) EraseLine(CursorY, 0, CursorX + 1);
                else EraseLine(CursorY, 0, Cols);
                break;
            case 'X': EraseLine(CursorY, CursorX, Math.Min(Cols, CursorX + n)); break;
            case 'P':
                {
                    var l = lines[CursorY];
                    for (int x = CursorX; x < Cols; x++) l[x] = x + n < Cols ? l[x + n] : Erased;
                    break;
                }
            case '@':
                {
                    var l = lines[CursorY];
                    for (int x = Cols - 1; x >= CursorX; x--) l[x] = x - n >= CursorX ? l[x - n] : Erased;
                    break;
                }
            case 'L':
                if (CursorY >= _top && CursorY <= _bottom)
                    for (int k = 0; k < n; k++) { for (int y = _bottom; y > CursorY; y--) lines[y] = lines[y - 1]; lines[CursorY] = BlankLine(); }
                break;
            case 'M':
                if (CursorY >= _top && CursorY <= _bottom)
                    for (int k = 0; k < n; k++) { for (int y = CursorY; y < _bottom; y++) lines[y] = lines[y + 1]; lines[_bottom] = BlankLine(); }
                break;
            case 'S': ScrollUp(n); break;
            case 'T': ScrollDown(n); break;
            case 'r':
                {
                    int top = Math.Max(1, p[0]) - 1, bottom = (p[1] == 0 ? Rows : p[1]) - 1;
                    if (top < bottom && bottom < Rows) { _top = top; _bottom = bottom; }
                    CursorX = 0; CursorY = 0;
                    break;
                }
            case 's': Save(); break;
            case 'u': Restore(); break;
            case 'm': Sgr(); break;
            case 'n':
                if (p[0] == 6) Reply?.Invoke($"\x1b[{CursorY + 1};{CursorX + 1}R");
                else if (p[0] == 5) Reply?.Invoke("\x1b[0n");
                break;
            case 'c': Reply?.Invoke("\x1b[?1;2c"); break;
        }
    }

    void SetPrivateMode(int mode, bool on)
    {
        switch (mode)
        {
            case 1: AppCursorKeys = on; break;
            case 25: CursorVisible = on; break;
            case 2004: BracketedPaste = on; break;
            case 47 or 1047 or 1049:
                if (on == _onAlt) return;
                if (on) { if (mode == 1049) Save(); _alt = NewLines(Rows, Cols); _onAlt = true; }
                else { _onAlt = false; if (mode == 1049) Restore(); }
                _top = 0; _bottom = Rows - 1;
                break;
        }
    }

    void EraseLine(int y, int from, int to)
    {
        var l = Lines[y];
        for (int x = Math.Max(0, from); x < Math.Min(Cols, to); x++) l[x] = Erased;
    }

    void Sgr()
    {
        var p = Params(0, 1);
        for (int i = 0; i < p.Length; i++)
        {
            int v = p[i];
            switch (v)
            {
                case 0: _pen = Cell.Blank; break;
                case 1: _pen.Attr |= 1; break;
                case 2: _pen.Attr |= 2; break;
                case 3: _pen.Attr |= 4; break;
                case 4: _pen.Attr |= 8; break;
                case 7: _pen.Attr |= 16; break;
                case 22: _pen.Attr &= unchecked((byte)~3); break;
                case 23: _pen.Attr &= unchecked((byte)~4); break;
                case 24: _pen.Attr &= unchecked((byte)~8); break;
                case 27: _pen.Attr &= unchecked((byte)~16); break;
                case >= 30 and <= 37: _pen.Fg = v - 30; break;
                case 39: _pen.Fg = -1; break;
                case >= 40 and <= 47: _pen.Bg = v - 40; break;
                case 49: _pen.Bg = -1; break;
                case >= 90 and <= 97: _pen.Fg = v - 90 + 8; break;
                case >= 100 and <= 107: _pen.Bg = v - 100 + 8; break;
                case 38 or 48:
                    {
                        int color = -1;
                        if (i + 2 < p.Length && p[i + 1] == 5) { color = p[i + 2]; i += 2; }
                        else if (i + 4 < p.Length && p[i + 1] == 2) { color = 0x1000000 | (p[i + 2] << 16) | (p[i + 3] << 8) | p[i + 4]; i += 4; }
                        if (v == 38) _pen.Fg = color; else _pen.Bg = color;
                        break;
                    }
            }
        }
    }

    void Osc()
    {
        string s = _osc.ToString();
        int semi = s.IndexOf(';');
        if (semi < 0) return;
        if (s[..semi] is "0" or "2") Title = s[(semi + 1)..];
    }

    /// <summary>Plain text of a range of cells (for copying a selection).</summary>
    public static string TextOf(Cell[] line, int from, int to)
    {
        var sb = new StringBuilder();
        for (int x = Math.Max(0, from); x < Math.Min(line.Length, to); x++) sb.Append(line[x].Ch == '\0' ? ' ' : line[x].Ch);
        return sb.ToString().TrimEnd();
    }
}
