using System;
using System.Collections.Generic;
using System.Text;

namespace CnSharp.VSIX.Yolo
{
    /// <summary>
    /// One character cell of the screen buffer. Colors are 0xRRGGBB, or -1 for
    /// "use the terminal default". <see cref="Ch"/> is a Unicode scalar (not a UTF-16
    /// code unit) so astral characters like emoji fit in a single cell.
    /// </summary>
    internal struct TerminalCell
    {
        public int Ch;
        public int Fg;
        public int Bg;
        public byte Flags;

        /// <summary>Text to draw for this cell; empty for blanks and wide-char trailers.</summary>
        public string Text
        {
            get
            {
                if ((Flags & TerminalEmulator.FlagTrailer) != 0) return string.Empty;
                if (Ch <= 32) return Ch == 32 ? " " : string.Empty;
                return Ch <= 0xFFFF ? ((char)Ch).ToString() : char.ConvertFromUtf32(Ch);
            }
        }
    }

    /// <summary>
    /// A headless VT100/xterm screen buffer + escape-sequence parser.
    /// <para>
    /// This replaces xterm.js: WebView2 cannot composite inside this machine's VS
    /// tool window (the page loads but the HWND paints black), so the terminal is
    /// rendered by WPF instead. This class owns the state; <see cref="TerminalSurface"/>
    /// draws it.
    /// </para>
    /// Supported: SGR (16/256/true color, bold, underline, inverse), cursor
    /// positioning, erase display/line/chars, insert/delete lines and chars,
    /// scroll regions (DECSTBM), deferred line wrap, alternate screen buffer,
    /// and incremental UTF-8 decoding across pty chunks.
    /// </summary>
    internal sealed class TerminalEmulator
    {
        internal const byte FlagBold = 1;
        internal const byte FlagUnderline = 2;
        internal const byte FlagInverse = 4;
        /// <summary>Leading cell of a double-width character.</summary>
        internal const byte FlagWide = 8;
        /// <summary>Placeholder cell covered by the preceding double-width character.</summary>
        internal const byte FlagTrailer = 16;

        private enum State { Ground, Esc, EscIntermediate, Csi, Str, StrEsc }

        private TerminalCell[] _cells = new TerminalCell[0];
        private TerminalCell[]? _altCells;
        private int _cols;
        private int _rows;

        // Scrollback: a ring of past screen rows. When the live screen scrolls up the
        // top rows are frozen here so the user can review them with the mouse wheel.
        private readonly List<TerminalCell[]> _history = new List<TerminalCell[]>();
        private int _viewOffset;            // rows scrolled back from the bottom (0 = follow live)
        private int _historyCapacity = 2000;

        private int _cx, _cy;
        private int _savedX, _savedY;
        private int _scrollTop, _scrollBottom;
        private bool _wrapPending;
        private bool _applicationCursor;   // DECCKM: cursor keys emit ESC O .. instead of ESC [ ..
        internal bool ApplicationCursor => _applicationCursor;

        private int _fg = -1, _bg = -1;
        private byte _flags;

        private State _state = State.Ground;
        private char _pendingHighSurrogate;
        private readonly StringBuilder _csi = new StringBuilder();
        private readonly List<int> _params = new List<int>();

        private readonly Decoder _decoder = new UTF8Encoding(false).GetDecoder();
        private char[] _charBuf = new char[1024];

        public int Columns => _cols;
        public int Rows => _rows;
        public int CursorX => _cx;
        public int CursorY => _cy;
        public bool CursorVisible { get; private set; } = true;

        /// <summary>Maximum scrollback rows retained (oldest are dropped beyond this).</summary>
        public int HistoryCapacity
        {
            get => _historyCapacity;
            set
            {
                _historyCapacity = Math.Max(0, value);
                while (_history.Count > _historyCapacity)
                {
                    _history.RemoveAt(0);
                    if (_viewOffset > 0) _viewOffset--;
                }
                Version++;
            }
        }

        /// <summary>Rows currently held in scrollback.</summary>
        public int HistoryCount => _history.Count;

        /// <summary>How many rows the view is scrolled up from the live bottom.</summary>
        public int ViewOffset => _viewOffset;

        /// <summary>True when the view shows the live screen (not reviewing history).</summary>
        public bool AtBottom => _viewOffset == 0;

        /// <summary>Total scrollable rows: scrollback plus the live screen.</summary>
        public int TotalRows => _history.Count + _rows;

        /// <summary>Virtual row index at the top of the currently visible window.</summary>
        public int TopVirtualRow => Math.Max(0, _history.Count - _viewOffset);

        /// <summary>True while the alternate screen (vim, less, …) is active — scrollback is suspended then.</summary>
        public bool InAltScreen => _altCells != null;

        /// <summary>Bumped on every mutation so the renderer knows it must repaint.</summary>
        public int Version { get; private set; }

        public TerminalEmulator()
        {
            Resize(80, 24);
        }

        public TerminalCell this[int x, int y] => _cells[y * _cols + x];

        /// <summary>
        /// Re-shapes the buffer. Existing content is kept top-anchored; when the screen
        /// shrinks vertically the topmost lines are dropped so the prompt stays visible.
        /// </summary>
        public void Resize(int cols, int rows)
        {
            cols = Math.Max(1, cols);
            rows = Math.Max(1, rows);
            if (cols == _cols && rows == _rows) return;

            // A width change means every history row must be re-padded to the new size.
            if (cols != _cols)
            {
                for (int i = 0; i < _history.Count; i++)
                {
                    var old = _history[i];
                    var nr = new TerminalCell[cols];
                    for (int x = 0; x < cols; x++) nr[x] = x < old.Length ? old[x] : Blank();
                    _history[i] = nr;
                }
            }

            var next = new TerminalCell[cols * rows];
            for (int i = 0; i < next.Length; i++) next[i] = Blank();

            if (_cols > 0 && _rows > 0)
            {
                // Keep the bottom of the old screen (that's where the prompt lives).
                int copyRows = Math.Min(_rows, rows);
                int srcTop = _rows - copyRows;
                int copyCols = Math.Min(_cols, cols);
                for (int y = 0; y < copyRows; y++)
                    for (int x = 0; x < copyCols; x++)
                        next[y * cols + x] = _cells[(srcTop + y) * _cols + x];
                _cy = Math.Max(0, _cy - srcTop);
            }

            _cells = next;
            _cols = cols;
            _rows = rows;
            _altCells = null;
            _scrollTop = 0;
            _scrollBottom = rows - 1;
            _cx = Clamp(_cx, 0, cols - 1);
            _cy = Clamp(_cy, 0, rows - 1);
            _wrapPending = false;
            Version++;
        }

        /// <summary>Feeds raw pty bytes. Safe to call with partial UTF-8 sequences.</summary>
        public void Write(byte[] data)
        {
            if (data == null || data.Length == 0) return;
            int needed = _decoder.GetCharCount(data, 0, data.Length, false);
            if (_charBuf.Length < needed) _charBuf = new char[Math.Max(needed, _charBuf.Length * 2)];
            int n = _decoder.GetChars(data, 0, data.Length, _charBuf, 0, false);
            for (int i = 0; i < n; i++) Process(_charBuf[i]);
            Version++;
        }

        public void ClearAll()
        {
            for (int i = 0; i < _cells.Length; i++) _cells[i] = Blank();
            _cx = 0;
            _cy = 0;
            _wrapPending = false;
            Version++;
        }

        // ---------------------------------------------------------------- parser

        private void Process(char c)
        {
            switch (_state)
            {
                case State.Ground:
                    ProcessGround(c);
                    break;

                case State.Esc:
                    ProcessEsc(c);
                    break;

                case State.EscIntermediate:
                    // Charset designators etc. (ESC ( B) — consume the final byte.
                    _state = State.Ground;
                    break;

                case State.Csi:
                    if (c >= '\x40' && c <= '\x7e') { ExecuteCsi(c); _state = State.Ground; }
                    else if (c == '\x1b') { _state = State.Esc; _csi.Clear(); }
                    else _csi.Append(c);
                    break;

                case State.Str:
                    // OSC / DCS / PM / APC string: ends at BEL or ST (ESC \).
                    if (c == '\x07') _state = State.Ground;
                    else if (c == '\x1b') _state = State.StrEsc;
                    break;

                case State.StrEsc:
                    _state = State.Ground;
                    break;
            }
        }

        private void ProcessGround(char c)
        {
            switch (c)
            {
                case '\x1b':
                    _state = State.Esc;
                    _csi.Clear();
                    return;
                case '\r':
                    _cx = 0;
                    _wrapPending = false;
                    return;
                case '\n':
                case '\v':
                case '\f':
                    LineFeed();
                    return;
                case '\b':
                    if (_cx > 0) _cx--;
                    _wrapPending = false;
                    return;
                case '\t':
                    _cx = Math.Min(_cols - 1, (_cx / 8 + 1) * 8);
                    _wrapPending = false;
                    return;
                case '\x07':
                    return;
            }
            if (c < ' ' || c == '\x7f') return;

            // Combine UTF-16 surrogate pairs into one scalar so an emoji occupies a single
            // (double-width) cell instead of two broken halves.
            if (char.IsHighSurrogate(c)) { _pendingHighSurrogate = c; return; }
            if (char.IsLowSurrogate(c))
            {
                if (_pendingHighSurrogate != '\0')
                {
                    int scalar = char.ConvertToUtf32(_pendingHighSurrogate, c);
                    _pendingHighSurrogate = '\0';
                    PutChar(scalar);
                }
                return;
            }
            _pendingHighSurrogate = '\0';
            PutChar(c);
        }

        private void ProcessEsc(char c)
        {
            switch (c)
            {
                case '[':
                    _state = State.Csi;
                    _csi.Clear();
                    return;
                case ']':
                case 'P':
                case 'X':
                case '^':
                case '_':
                    _state = State.Str;
                    return;
                case '(':
                case ')':
                case '*':
                case '+':
                case '#':
                case '%':
                    _state = State.EscIntermediate;
                    return;
                case 'D':
                    LineFeed();
                    break;
                case 'E':
                    _cx = 0;
                    LineFeed();
                    break;
                case 'M':
                    ReverseIndex();
                    break;
                case '7':
                    SaveCursor();
                    break;
                case '8':
                    RestoreCursor();
                    break;
                case 'c':
                    HardReset();
                    break;
            }
            _state = State.Ground;
        }

        private void ExecuteCsi(char final)
        {
            string s = _csi.ToString();
            char priv = '\0';
            int start = 0;
            if (s.Length > 0 && (s[0] == '?' || s[0] == '>' || s[0] == '<' || s[0] == '=')) { priv = s[0]; start = 1; }

            ParseParams(s, start);
            int p0 = Param(0, 1);
            int p1 = Param(1, 1);

            switch (final)
            {
                case 'A': _cy = Math.Max(0, _cy - p0); _wrapPending = false; break;
                case 'B': _cy = Math.Min(_rows - 1, _cy + p0); _wrapPending = false; break;
                case 'C': _cx = Math.Min(_cols - 1, _cx + p0); _wrapPending = false; break;
                case 'D': _cx = Math.Max(0, _cx - p0); _wrapPending = false; break;
                case 'E': _cy = Math.Min(_rows - 1, _cy + p0); _cx = 0; _wrapPending = false; break;
                case 'F': _cy = Math.Max(0, _cy - p0); _cx = 0; _wrapPending = false; break;
                case 'G':
                case '`': _cx = Clamp(p0 - 1, 0, _cols - 1); _wrapPending = false; break;
                case 'd': _cy = Clamp(p0 - 1, 0, _rows - 1); _wrapPending = false; break;
                case 'H':
                case 'f':
                    _cy = Clamp(p0 - 1, 0, _rows - 1);
                    _cx = Clamp(p1 - 1, 0, _cols - 1);
                    _wrapPending = false;
                    break;
                case 'J': EraseDisplay(Param(0, 0)); break;
                case 'K': EraseLine(Param(0, 0)); break;
                case 'L': InsertLines(p0); break;
                case 'M': DeleteLines(p0); break;
                case 'P': DeleteChars(p0); break;
                case '@': InsertChars(p0); break;
                case 'X': EraseChars(p0); break;
                case 'S': ScrollUp(p0); break;
                case 'T': ScrollDown(p0); break;
                case 'm': ApplySgr(); break;
                case 'r':
                    _scrollTop = Clamp(Param(0, 1) - 1, 0, _rows - 1);
                    _scrollBottom = Clamp(Param(1, _rows) - 1, _scrollTop, _rows - 1);
                    _cx = 0;
                    _cy = _scrollTop;
                    break;
                case 's': SaveCursor(); break;
                case 'u': RestoreCursor(); break;
                case 'h': SetMode(priv, true); break;
                case 'l': SetMode(priv, false); break;
            }
        }

        private void SetMode(char priv, bool on)
        {
            if (priv != '?') return;
            for (int i = 0; i < _params.Count; i++)
            {
                switch (_params[i])
                {
                    case 1:
                        _applicationCursor = on;   // DECCKM (smkx/rmkx)
                        break;
                    case 25:
                        CursorVisible = on;
                        break;
                    case 1047:
                    case 1049:
                    case 47:
                        SwitchAltScreen(on);
                        break;
                }
            }
        }

        private void SwitchAltScreen(bool on)
        {
            if (on)
            {
                if (_altCells != null) return;
                _altCells = _cells;
                _cells = new TerminalCell[_cols * _rows];
                for (int i = 0; i < _cells.Length; i++) _cells[i] = Blank();
                SaveCursor();
                _cx = 0;
                _cy = 0;
            }
            else
            {
                if (_altCells == null) return;
                if (_altCells.Length == _cols * _rows) _cells = _altCells;
                _altCells = null;
                RestoreCursor();
            }
            _wrapPending = false;
        }

        private void ApplySgr()
        {
            if (_params.Count == 0) { ResetAttrs(); return; }
            for (int i = 0; i < _params.Count; i++)
            {
                int p = _params[i];
                switch (p)
                {
                    case 0: ResetAttrs(); break;
                    case 1: _flags |= FlagBold; break;
                    case 4: _flags |= FlagUnderline; break;
                    case 7: _flags |= FlagInverse; break;
                    case 21:
                    case 22: _flags &= unchecked((byte)~FlagBold); break;
                    case 24: _flags &= unchecked((byte)~FlagUnderline); break;
                    case 27: _flags &= unchecked((byte)~FlagInverse); break;
                    case 39: _fg = -1; break;
                    case 49: _bg = -1; break;
                    case 38:
                    case 48:
                        {
                            int color = ReadExtendedColor(ref i);
                            if (color != -2) { if (p == 38) _fg = color; else _bg = color; }
                            break;
                        }
                    default:
                        if (p >= 30 && p <= 37) _fg = TerminalPalette.Ansi(p - 30);
                        else if (p >= 40 && p <= 47) _bg = TerminalPalette.Ansi(p - 40);
                        else if (p >= 90 && p <= 97) _fg = TerminalPalette.Ansi(p - 90 + 8);
                        else if (p >= 100 && p <= 107) _bg = TerminalPalette.Ansi(p - 100 + 8);
                        break;
                }
            }
        }

        /// <summary>Reads 38/48 sub-parameters: ;5;idx (256-color) or ;2;r;g;b (true color).</summary>
        private int ReadExtendedColor(ref int i)
        {
            if (i + 1 >= _params.Count) return -2;
            int mode = _params[i + 1];
            if (mode == 5 && i + 2 < _params.Count)
            {
                int idx = _params[i + 2];
                i += 2;
                return TerminalPalette.Xterm256(idx);
            }
            if (mode == 2 && i + 4 < _params.Count)
            {
                int r = _params[i + 2] & 0xff, g = _params[i + 3] & 0xff, b = _params[i + 4] & 0xff;
                i += 4;
                return (r << 16) | (g << 8) | b;
            }
            i = _params.Count;
            return -2;
        }

        private void ResetAttrs()
        {
            _fg = -1;
            _bg = -1;
            _flags = 0;
        }

        private void ParseParams(string s, int start)
        {
            _params.Clear();
            int value = 0;
            bool any = false;
            for (int i = start; i < s.Length; i++)
            {
                char c = s[i];
                if (c >= '0' && c <= '9') { value = value * 10 + (c - '0'); any = true; }
                else if (c == ';' || c == ':') { _params.Add(any ? value : 0); value = 0; any = false; }
                // intermediates (space ! " $ ') are ignored
            }
            if (any) _params.Add(value);
        }

        private int Param(int index, int fallback)
        {
            if (index >= _params.Count) return fallback;
            int v = _params[index];
            return v == 0 ? fallback : v;
        }

        // ------------------------------------------------------------- buffer ops

        private void PutChar(int cp)
        {
            int width = CharWidth.Of(cp);

            // Combining marks and zero-width joiners must not consume a cell or they would
            // shift every following column. We drop them rather than corrupt the grid.
            if (width == 0) return;

            if (_wrapPending)
            {
                _cx = 0;
                LineFeed();
                _wrapPending = false;
            }
            if (_cx >= _cols) { _cx = 0; LineFeed(); }

            // A double-width glyph cannot straddle the right margin, so wrap early.
            if (width == 2 && _cx == _cols - 1)
            {
                ClearCell(_cy * _cols + _cx);
                _cx = 0;
                LineFeed();
            }

            int i = _cy * _cols + _cx;
            BreakPairAt(i);
            if (width == 2) BreakPairAt(i + 1);

            _cells[i].Ch = cp;
            _cells[i].Fg = _fg;
            _cells[i].Bg = _bg;
            _cells[i].Flags = width == 2 ? (byte)(_flags | FlagWide) : _flags;

            if (width == 2)
            {
                _cells[i + 1].Ch = 0;
                _cells[i + 1].Fg = _fg;
                _cells[i + 1].Bg = _bg;
                _cells[i + 1].Flags = (byte)(_flags | FlagTrailer);
            }

            _cx += width;
            if (_cx >= _cols)
            {
                _cx = _cols - 1;
                _wrapPending = true;
            }
        }

        /// <summary>
        /// Overwriting one half of a double-width character must clear the other half too,
        /// otherwise the grid keeps a dangling trailer that renders as a stray blank block.
        /// </summary>
        private void BreakPairAt(int index)
        {
            if (index < 0 || index >= _cells.Length) return;

            if ((_cells[index].Flags & FlagWide) != 0)
            {
                int trailer = index + 1;
                if (trailer < _cells.Length && trailer / _cols == index / _cols &&
                    (_cells[trailer].Flags & FlagTrailer) != 0)
                    ClearCell(trailer);
            }
            else if ((_cells[index].Flags & FlagTrailer) != 0)
            {
                int lead = index - 1;
                if (lead >= 0 && lead / _cols == index / _cols &&
                    (_cells[lead].Flags & FlagWide) != 0)
                    ClearCell(lead);
            }
        }

        private void ClearCell(int index)
        {
            if (index < 0 || index >= _cells.Length) return;
            _cells[index] = Blank();
        }

        private void LineFeed()
        {
            _wrapPending = false;
            if (_cy == _scrollBottom) ScrollUp(1);
            else if (_cy < _rows - 1) _cy++;
        }

        private void ReverseIndex()
        {
            _wrapPending = false;
            if (_cy == _scrollTop) ScrollDown(1);
            else if (_cy > 0) _cy--;
        }

        private void ScrollUp(int n)
        {
            n = Math.Max(1, Math.Min(n, _scrollBottom - _scrollTop + 1));
            int height = _scrollBottom - _scrollTop + 1;
            if (n >= height)
            {
                // Whole region leaves the screen — freeze it into scrollback when the
                // scroll is over the top of the full screen (not an inner scroll region).
                if (_scrollTop == 0)
                    for (int y = _scrollTop; y <= _scrollBottom; y++) PushHistory(CopyRow(y));
                ClearRows(_scrollTop, _scrollBottom);
                return;
            }
            if (_scrollTop == 0)
                for (int y = _scrollTop; y < _scrollTop + n; y++) PushHistory(CopyRow(y));
            Array.Copy(_cells, (_scrollTop + n) * _cols, _cells, _scrollTop * _cols, (height - n) * _cols);
            ClearRows(_scrollBottom - n + 1, _scrollBottom);
        }

        private void ScrollDown(int n)
        {
            n = Math.Max(1, Math.Min(n, _scrollBottom - _scrollTop + 1));
            int height = _scrollBottom - _scrollTop + 1;
            if (n >= height) { ClearRows(_scrollTop, _scrollBottom); return; }
            Array.Copy(_cells, _scrollTop * _cols, _cells, (_scrollTop + n) * _cols, (height - n) * _cols);
            ClearRows(_scrollTop, _scrollTop + n - 1);
        }

        private void InsertLines(int n)
        {
            if (_cy < _scrollTop || _cy > _scrollBottom) return;
            int height = _scrollBottom - _cy + 1;
            n = Math.Min(n, height);
            if (n < height)
                Array.Copy(_cells, _cy * _cols, _cells, (_cy + n) * _cols, (height - n) * _cols);
            ClearRows(_cy, _cy + n - 1);
        }

        private void DeleteLines(int n)
        {
            if (_cy < _scrollTop || _cy > _scrollBottom) return;
            int height = _scrollBottom - _cy + 1;
            n = Math.Min(n, height);
            if (n < height)
                Array.Copy(_cells, (_cy + n) * _cols, _cells, _cy * _cols, (height - n) * _cols);
            ClearRows(_scrollBottom - n + 1, _scrollBottom);
        }

        private void DeleteChars(int n)
        {
            n = Math.Min(n, _cols - _cx);
            int row = _cy * _cols;
            BreakPairAt(row + _cx);
            for (int x = _cx; x < _cols - n; x++) _cells[row + x] = _cells[row + x + n];
            for (int x = _cols - n; x < _cols; x++) _cells[row + x] = Blank();
        }

        private void InsertChars(int n)
        {
            n = Math.Min(n, _cols - _cx);
            int row = _cy * _cols;
            BreakPairAt(row + _cx);
            for (int x = _cols - 1; x >= _cx + n; x--) _cells[row + x] = _cells[row + x - n];
            for (int x = _cx; x < _cx + n; x++) _cells[row + x] = Blank();
        }

        private void EraseChars(int n)
        {
            n = Math.Min(n, _cols - _cx);
            ClearSpan(_cy, _cx, _cx + n - 1);
        }

        private void EraseLine(int mode)
        {
            int from = mode == 0 ? _cx : 0;
            int to = mode == 1 ? _cx : _cols - 1;
            ClearSpan(_cy, from, Math.Min(to, _cols - 1));
            _wrapPending = false;
        }

        private void EraseDisplay(int mode)
        {
            if (mode == 2 || mode == 3)
            {
                for (int i = 0; i < _cells.Length; i++) _cells[i] = Blank();
            }
            else if (mode == 0)
            {
                ClearSpan(_cy, _cx, _cols - 1);
                ClearRows(_cy + 1, _rows - 1);
            }
            else if (mode == 1)
            {
                ClearRows(0, _cy - 1);
                ClearSpan(_cy, 0, Math.Min(_cx, _cols - 1));
            }
            _wrapPending = false;
        }

        private void ClearRows(int top, int bottom)
        {
            if (top < 0) top = 0;
            if (bottom > _rows - 1) bottom = _rows - 1;
            for (int y = top; y <= bottom; y++)
                for (int x = 0; x < _cols; x++)
                    _cells[y * _cols + x] = Blank();
        }

        private void SaveCursor()
        {
            _savedX = _cx;
            _savedY = _cy;
        }

        private void RestoreCursor()
        {
            _cx = Clamp(_savedX, 0, _cols - 1);
            _cy = Clamp(_savedY, 0, _rows - 1);
            _wrapPending = false;
        }

        private void HardReset()
        {
            ResetAttrs();
            _scrollTop = 0;
            _scrollBottom = _rows - 1;
            CursorVisible = true;
            _applicationCursor = false;
            _history.Clear();
            _viewOffset = 0;
            ClearAll();
        }

        private TerminalCell Blank()
        {
            return new TerminalCell { Ch = ' ', Fg = -1, Bg = -1, Flags = 0 };
        }

        /// <summary>
        /// Clears a horizontal span, severing any double-width pair that straddles either
        /// end so no orphaned leader/trailer is left behind.
        /// </summary>
        private void ClearSpan(int row, int fromX, int toX)
        {
            if (fromX > toX) return;
            int baseIndex = row * _cols;
            BreakPairAt(baseIndex + fromX);
            BreakPairAt(baseIndex + toX);
            for (int x = fromX; x <= toX && x < _cols; x++)
                _cells[baseIndex + x] = Blank();
        }

        /// <summary>
        /// Moves the view up (negative) or down (positive) through scrollback. Wheel-up
        /// passes a negative <paramref name="deltaRows"/>. Clamped to [0, HistoryCount].
        /// </summary>
        public void Scroll(int deltaRows)
        {
            if (_altCells != null) return; // no scrollback while a full-screen app owns the view
            int next = Clamp(_viewOffset + deltaRows, 0, _history.Count);
            if (next != _viewOffset)
            {
                _viewOffset = next;
                Version++;
            }
        }

        /// <summary>Returns the view to the live bottom and resumes auto-follow.</summary>
        public void ScrollToBottom()
        {
            if (_viewOffset != 0)
            {
                _viewOffset = 0;
                Version++;
            }
        }

        /// <summary>Sets the scrollback offset directly (rows scrolled up from the bottom),
        /// clamped to [0, HistoryCount]. Used by the terminal's scrollbar drag.</summary>
        public void SetViewOffset(int offset)
        {
            if (_altCells != null) return; // no scrollback while a full-screen app owns the view
            int next = Clamp(offset, 0, _history.Count);
            if (next != _viewOffset)
            {
                _viewOffset = next;
                Version++;
            }
        }

        /// <summary>Reads a single cell by absolute virtual row (history + live concatenated).</summary>
        public TerminalCell VirtualCell(int vRow, int x)
        {
            int h = _history.Count;
            if (vRow < h)
            {
                var row = _history[vRow];
                return row[x < row.Length ? x : row.Length - 1];
            }
            vRow -= h;
            if (vRow < 0) vRow = 0;
            if (vRow >= _rows) vRow = _rows - 1;
            return _cells[vRow * _cols + x];
        }

        private TerminalCell[] CopyRow(int y)
        {
            var row = new TerminalCell[_cols];
            Array.Copy(_cells, y * _cols, row, 0, _cols);
            return row;
        }

        private void PushHistory(TerminalCell[] row)
        {
            _history.Add(row);
            // When scrolled back, keep the window anchored to the same historical lines even
            // as new rows arrive: bump the offset, then counteract any front-trimming below.
            bool anchored = _viewOffset > 0;
            if (anchored) _viewOffset++;
            while (_history.Count > _historyCapacity)
            {
                _history.RemoveAt(0);
                if (_viewOffset > 0) _viewOffset--;
            }
        }

        private static int Clamp(int v, int lo, int hi)
        {
            if (hi < lo) return lo;
            return v < lo ? lo : (v > hi ? hi : v);
        }
    }
}
