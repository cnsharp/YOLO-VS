using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Windows;
using System.Windows.Media;

namespace CnSharp.VSIX.Yolo
{
    /// <summary>
    /// Draws a <see cref="TerminalEmulator"/> screen buffer with pure WPF drawing
    /// primitives. Deliberately a <see cref="FrameworkElement"/> (not an HwndHost /
    /// WebView2 / WindowsFormsHost) so it composites like any other WPF control inside
    /// a Visual Studio tool window.
    /// </summary>
    internal sealed class TerminalSurface : FrameworkElement
    {
        private readonly Dictionary<int, Brush> _brushes = new Dictionary<int, Brush>();
        private readonly Typeface _regular;
        private readonly Typeface _bold;
        private readonly Brush _defaultBg;
        private readonly Brush _defaultFg;
        private readonly Brush _cursorBrush;
        // Text selection. The grid is drawn (no real text elements), so a selection is tracked per cell in
        // virtual-row/column coordinates and painted as a semi-transparent highlight; copying reads the row text.
        private readonly Brush _selectionBrush;
        private int _selAnchorRow = -1, _selAnchorCol = -1;
        private int _selEndRow = -1, _selEndCol = -1;

        // Link detection + render cache. The link filter is supplied by the tool window; painted
        // links and clicked links both come from it, so they can never disagree. Results are cached
        // per emulator version so a chatty TUI does not re-run the regexes on every repaint.
        private readonly Brush _linkBrush;
        private readonly Pen _linkPen;
        private YoloLinkFilters? _linkFilter;
        private int _linkCacheVersion = -1;
        private readonly Dictionary<int, string?> _rowTextCache = new Dictionary<int, string?>();
        private readonly Dictionary<int, List<LinkMatch>?> _rowLinkCache = new Dictionary<int, List<LinkMatch>?>();

        private readonly StringBuilder _run = new StringBuilder();
        private readonly Dictionary<char, double> _advanceCache = new Dictionary<char, double>();

        private double _cellWidth;
        private double _cellHeight;
        private double _fontSize = 13.0;
        private int _cols;
        private int _rows;

        public TerminalEmulator Emulator { get; } = new TerminalEmulator();

        /// <summary>Raised (on the UI thread) when the size in character cells changes.</summary>
        public event Action? CellSizeChanged;

        public int Columns => _cols;
        public int Rows => _rows;
        public bool ShowCursor { get; set; }

        // ── Text selection (the grid is drawn, not real text, so selection is tracked per cell) ──

        /// <summary>True when a non-empty selection exists.</summary>
        public bool HasSelection =>
            _selAnchorRow >= 0 && (_selAnchorRow != _selEndRow || _selAnchorCol != _selEndCol);

        /// <summary>Begins (or restarts) a selection at the given virtual cell.</summary>
        public void BeginSelection(int vRow, int col)
        {
            _selAnchorRow = vRow; _selAnchorCol = col;
            _selEndRow = vRow; _selEndCol = col;
            InvalidateVisual();
        }

        /// <summary>Extends the active selection to the given virtual cell.</summary>
        public void UpdateSelection(int vRow, int col)
        {
            if (_selAnchorRow < 0) return;
            _selEndRow = vRow; _selEndCol = col;
            InvalidateVisual();
        }

        /// <summary>Clears any active selection.</summary>
        public void ClearSelection()
        {
            if (_selAnchorRow < 0 && _selEndRow < 0) return;
            _selAnchorRow = _selEndRow = -1;
            _selAnchorCol = _selEndCol = -1;
            InvalidateVisual();
        }

        /// <summary>
        /// The selected text as a multi-line string (stream selection: top row from its anchor column to the
        /// end, middle rows fully, bottom row from the start to its column). Trailing spaces per line are
        /// trimmed, matching common terminal copy behaviour. Returns null when there is no selection.
        /// </summary>
        public string? GetSelectionText()
        {
            if (!HasSelection) return null;
            int top = Math.Min(_selAnchorRow, _selEndRow);
            int bottom = Math.Max(_selAnchorRow, _selEndRow);
            int leftCol = _selAnchorRow <= _selEndRow ? _selAnchorCol : _selEndCol;
            int rightCol = _selAnchorRow <= _selEndRow ? _selEndCol : _selAnchorCol;
            var sb = new StringBuilder();
            for (int r = top; r <= bottom; r++)
            {
                if (r > top) sb.Append('\n');
                string row = GetRowText(r);
                int s, e;
                if (top == bottom) { s = Math.Min(leftCol, rightCol); e = Math.Max(leftCol, rightCol); }
                else if (r == top) { s = leftCol; e = row.Length; }
                else if (r == bottom) { s = 0; e = rightCol; }
                else { s = 0; e = row.Length; }
                s = Math.Max(0, Math.Min(s, row.Length));
                e = Math.Max(s, Math.Min(e, row.Length));
                string line = row.Substring(s, e - s);
                int t = line.Length;
                while (t > 0 && line[t - 1] == ' ') t--;
                sb.Append(line.Substring(0, t));
            }
            return sb.ToString();
        }

        public TerminalSurface()
        {
            var family = PickFontFamily();
            _regular = new Typeface(family, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
            _bold = new Typeface(family, FontStyles.Normal, FontWeights.Bold, FontStretches.Normal);

            _defaultBg = Freeze(TerminalPalette.DefaultBackground);
            _defaultFg = Freeze(TerminalPalette.DefaultForeground);
            _cursorBrush = Freeze(0xAEAFAD);

            // Semi-transparent highlight for the terminal text selection (fixed colour — the terminal surface
            // is the documented exception to the VS-theme rule, AGENTS.md §5.6).
            _selectionBrush = new SolidColorBrush(Color.FromArgb(0x40, 0x56, 0x9C, 0xD6));
            _selectionBrush.Freeze();

            // Fixed console-style link colour. The terminal surface is the documented exception to the
            // VS-theme rule (AGENTS.md §5.6), so links are intentionally NOT themed — they must stay
            // legible regardless of the surrounding chrome theme.
            _linkBrush = Freeze(0x4FC1FF);
            _linkPen = new Pen(_linkBrush, 1);
            _linkPen.Freeze();

            MeasureCell();
            ClipToBounds = true;
            SnapsToDevicePixels = true;
        }

        public double FontSize
        {
            get => _fontSize;
            set
            {
                if (Math.Abs(value - _fontSize) < 0.1) return;
                _fontSize = Math.Max(6, value);
                MeasureCell();
                RecomputeGrid(RenderSize);
                InvalidateVisual();
            }
        }

        /// <summary>
        /// Derives the cell box from the font's own advance width / line height so the
        /// grid stays perfectly monospaced.
        /// </summary>
        private void MeasureCell()
        {
            if (_regular.TryGetGlyphTypeface(out GlyphTypeface gtf))
            {
                ushort glyph = gtf.CharacterToGlyphMap.ContainsKey('M') ? gtf.CharacterToGlyphMap['M'] : (ushort)0;
                _cellWidth = gtf.AdvanceWidths[glyph] * _fontSize;
                _cellHeight = Math.Ceiling(gtf.Height * _fontSize) + 1;
            }
            else
            {
                var probe = MakeText("M", _regular, _defaultFg);
                _cellWidth = probe.WidthIncludingTrailingWhitespace;
                _cellHeight = Math.Ceiling(probe.Height) + 1;
            }
            if (_cellWidth <= 0) _cellWidth = _fontSize * 0.6;
            if (_cellHeight <= 0) _cellHeight = _fontSize * 1.3;
        }

        protected override void OnRenderSizeChanged(SizeChangedInfo info)
        {
            base.OnRenderSizeChanged(info);
            RecomputeGrid(info.NewSize);
        }

        private void RecomputeGrid(Size size)
        {
            int cols = Math.Max(1, (int)Math.Floor(size.Width / _cellWidth));
            int rows = Math.Max(1, (int)Math.Floor(size.Height / _cellHeight));
            if (cols == _cols && rows == _rows) return;
            _cols = cols;
            _rows = rows;
            Emulator.Resize(cols, rows);
            Log.Write($"TerminalSurface: grid {cols}x{rows} (cell {_cellWidth:F2}x{_cellHeight:F2}, px {size.Width:F0}x{size.Height:F0})");
            CellSizeChanged?.Invoke();
            InvalidateVisual();
        }

        protected override void OnRender(DrawingContext dc)
        {
            var em = Emulator;
            dc.DrawRectangle(_defaultBg, null, new Rect(0, 0, Math.Max(1, RenderSize.Width), Math.Max(1, RenderSize.Height)));

            int cols = em.Columns;
            int rows = em.Rows;
            int topRow = em.TopVirtualRow;   // virtual row at the top of the visible window

            for (int y = 0; y < rows; y++)
            {
                double top = y * _cellHeight;
                int x = 0;
                while (x < cols)
                {
                    var cell = em.VirtualCell(topRow + y, x);

                    // Trailers are covered by the wide glyph to their left; only their
                    // background matters and the leader already painted it.
                    if ((cell.Flags & TerminalEmulator.FlagTrailer) != 0) { x++; continue; }

                    int fg = cell.Fg;
                    int bg = cell.Bg;
                    byte flags = cell.Flags;
                    bool wide = (flags & TerminalEmulator.FlagWide) != 0;

                    int len;
                    _run.Clear();
                    _run.Append(cell.Text);

                    if (wide)
                    {
                        // A double-width glyph spans two cells and cannot be run-batched
                        // with its neighbours, since the advance no longer matches the grid.
                        len = 2;
                    }
                    else if (!FitsCell(cell.Text))
                    {
                        // A glyph whose font advance differs from the cell width (⏺, ✔, … — narrow
                        // by CharWidth but rendered from a fallback face) must not join a run: inside
                        // a run WPF advances by font metrics, so everything after it would drift off
                        // the grid and the link overlay (painted at exact cell positions) would show
                        // a ghost copy. Draw it alone at its cell origin; the next cell starts a run.
                        len = 1;
                    }
                    else
                    {
                        len = 1;
                        while (x + len < cols)
                        {
                            var nextCell = em.VirtualCell(topRow + y, x + len);
                            if (nextCell.Fg != fg || nextCell.Bg != bg || nextCell.Flags != flags) break;
                            if ((nextCell.Flags & (TerminalEmulator.FlagWide | TerminalEmulator.FlagTrailer)) != 0) break;
                            if (!FitsCell(nextCell.Text)) break;
                            _run.Append(nextCell.Text);
                            len++;
                        }
                    }

                    bool inverse = (flags & TerminalEmulator.FlagInverse) != 0;
                    int drawBg = inverse ? (fg < 0 ? TerminalPalette.DefaultForeground : fg) : bg;
                    int drawFg = inverse ? (bg < 0 ? TerminalPalette.DefaultBackground : bg) : fg;

                    if (drawBg >= 0)
                    {
                        dc.DrawRectangle(Freeze(drawBg), null,
                            new Rect(x * _cellWidth, top, len * _cellWidth + 0.5, _cellHeight));
                    }

                    string text = _run.ToString();
                    if (!IsBlank(text))
                    {
                        var brush = drawFg < 0 ? _defaultFg : Freeze(drawFg);
                        bool bold = (flags & TerminalEmulator.FlagBold) != 0;
                        var ft = MakeText(text, bold ? _bold : _regular, brush);
                        dc.DrawText(ft, new Point(x * _cellWidth, top));

                        if ((flags & TerminalEmulator.FlagUnderline) != 0)
                        {
                            double uy = top + _cellHeight - 1.5;
                            dc.DrawLine(new Pen(brush, 1),
                                new Point(x * _cellWidth, uy),
                                new Point((x + len) * _cellWidth, uy));
                        }
                    }

                    x += len;
                }
            }

            // Text-selection highlight (stream selection, painted semi-transparent over the glyphs so they
            // stay readable). Only visible rows that fall inside the selection range are drawn.
            if (HasSelection)
            {
                int top = Math.Min(_selAnchorRow, _selEndRow);
                int bottom = Math.Max(_selAnchorRow, _selEndRow);
                int leftCol = _selAnchorRow <= _selEndRow ? _selAnchorCol : _selEndCol;
                int rightCol = _selAnchorRow <= _selEndRow ? _selEndCol : _selAnchorCol;
                for (int y = 0; y < rows; y++)
                {
                    int vRow = topRow + y;
                    if (vRow < top || vRow > bottom) continue;
                    double topY = y * _cellHeight;
                    int s, e;
                    if (top == bottom) { s = Math.Min(leftCol, rightCol); e = Math.Max(leftCol, rightCol); }
                    else if (vRow == top) { s = leftCol; e = cols; }
                    else if (vRow == bottom) { s = 0; e = rightCol; }
                    else { s = 0; e = cols; }
                    if (e <= s) continue;
                    dc.DrawRectangle(_selectionBrush, null,
                        new Rect(s * _cellWidth, topY, (e - s) * _cellWidth + 0.5, _cellHeight));
                }
            }

            // Hyperlink overlay: re-paint matched spans in the link colour + underline. The underlying
            // (terminal-coloured) glyphs were already drawn in the loop above; drawing the same glyphs on
            // top swaps only the colour and adds the rule. Detection here is exactly what the click handler
            // uses, and the per-version cache keeps a fast TUI cheap.
            if (_linkFilter != null)
            {
                // Evaluate every visible row top-to-bottom, threading ONE PathWrapState so a path hard-wrapped
                // across 3+ rows reconstructs correctly (the accumulated prefix is carried from the row above,
                // exactly like IDEA's JediTerm). Results are cached per row for the click handler.
                var wrap = new PathWrapState();
                for (int y = 0; y < rows; y++)
                {
                    int vRow = topRow + y;
                    string rowText = GetRowText(vRow);
                    var links = _linkFilter.FindLinks(rowText, wrap, vRow);
                    _rowLinkCache[vRow] = links;
                    // When a hard-wrapped path just completed, upgrade every fragment link recorded on its
                    // earlier rows (head + continuations) so the WHOLE multi-row reference navigates to the
                    // full file, not just the visible tail row.
                    if (wrap.CompletedTarget != null && wrap.HeadRows.Count > 0)
                    {
                        foreach (int hr in wrap.HeadRows)
                        {
                            if (_rowLinkCache.TryGetValue(hr, out var hl) && hl != null)
                                foreach (var hm in hl)
                                    hm.Target = wrap.CompletedTarget;
                        }
                        wrap.HeadRows.Clear();
                        wrap.CompletedTarget = null;
                    }
                    if (links == null) continue;
                    double top = y * _cellHeight;
                    foreach (var m in links)
                    {
                        int s = m.Start < 0 ? 0 : m.Start;
                        int e = m.End > cols ? cols : m.End;
                        if (e <= s) continue;
                        var ft = MakeText(rowText.Substring(s, e - s), _regular, _linkBrush);
                        dc.DrawText(ft, new Point(s * _cellWidth, top));
                        double uy = top + _cellHeight - 1.5;
                        dc.DrawLine(_linkPen, new Point(s * _cellWidth, uy), new Point(e * _cellWidth, uy));
                    }
                }
            }

            if (ShowCursor && em.AtBottom && em.CursorVisible && em.CursorX < cols && em.CursorY < rows)
            {
                var cell = em.VirtualCell(em.TopVirtualRow + em.CursorY, em.CursorX);
                double cursorWidth = (cell.Flags & TerminalEmulator.FlagWide) != 0
                    ? _cellWidth * 2
                    : _cellWidth;

                dc.DrawRectangle(_cursorBrush, null,
                    new Rect(em.CursorX * _cellWidth, em.CursorY * _cellHeight, Math.Max(1, cursorWidth), _cellHeight));

                string text = cell.Text;
                if (!IsBlank(text))
                {
                    var ft = MakeText(text, _regular, _defaultBg);
                    dc.DrawText(ft, new Point(em.CursorX * _cellWidth, em.CursorY * _cellHeight));
                }
            }
        }

        private static bool IsBlank(string s)
        {
            if (s.Length == 0) return true;
            for (int i = 0; i < s.Length; i++)
                if (s[i] != ' ' && s[i] != '\0') return false;
            return true;
        }

        // ── Terminal hyperlinks ─────────────────────────────────────────────────────
        // Clickable spans (file paths, stack frames, type/member names, URLs) painted over the
        // terminal text. Detection is shared with the click handler via YoloLinkFilters.FindLinks,
        // which also takes the row above for hard-wrapped-path reconstruction.

        /// <summary>The link filter used to paint + hit-test clickable spans. Set by the tool window.</summary>
        public YoloLinkFilters? LinkFilter
        {
            get => _linkFilter;
            set
            {
                if (ReferenceEquals(_linkFilter, value)) return;
                _linkFilter = value;
                InvalidateLinkCache();
                InvalidateVisual();
            }
        }

        /// <summary>Column-indexed text of a virtual row (1 char per column; wide-char trailers become a space).</summary>
        public string GetRowText(int vRow)
        {
            InvalidateLinkCacheIfNeeded();
            if (_rowTextCache.TryGetValue(vRow, out var cached)) return cached ?? string.Empty;
            string text = BuildRowText(vRow);
            _rowTextCache[vRow] = text;
            return text;
        }

        /// <summary>Clickable spans on a virtual row, or null. Cached per emulator version.</summary>
        public List<LinkMatch>? GetRowLinks(int vRow)
        {
            InvalidateLinkCacheIfNeeded();
            if (_rowLinkCache.TryGetValue(vRow, out var cached)) return cached;
            List<LinkMatch>? links = null;
            if (_linkFilter != null)
            {
                string text = GetRowText(vRow);
                string? prev = vRow > 0 ? GetRowText(vRow - 1) : null;
                links = _linkFilter.FindLinks(text, prev);
            }
            _rowLinkCache[vRow] = links;
            return links;
        }

        /// <summary>Maps a client point to (virtual row, column), or null when outside the grid.</summary>
        public (int virtualRow, int col)? HitTest(Point pt)
        {
            int cols = Emulator.Columns;
            int rows = Emulator.Rows;
            if (cols <= 0 || rows <= 0) return null;
            int y = (int)(pt.Y / _cellHeight);
            int x = (int)(pt.X / _cellWidth);
            if (y < 0 || y >= rows || x < 0 || x >= cols) return null;
            return (Emulator.TopVirtualRow + y, x);
        }

        private void InvalidateLinkCache() => _linkCacheVersion = -1;

        private void InvalidateLinkCacheIfNeeded()
        {
            if (Emulator.Version != _linkCacheVersion)
            {
                _rowTextCache.Clear();
                _rowLinkCache.Clear();
                _linkCacheVersion = Emulator.Version;
            }
        }

        private string BuildRowText(int vRow)
        {
            int cols = Emulator.Columns;
            if (cols <= 0) return string.Empty;
            var sb = new StringBuilder(cols);
            for (int x = 0; x < cols; x++)
            {
                var cell = Emulator.VirtualCell(vRow, x);
                // A wide-char trailer occupies a column but carries no glyph; map it to a blank so the
                // character index stays 1:1 with the screen column (links are ASCII, so this never matters
                // for a match, but keeps hit-testing accurate in the rare CJK-adjacent case).
                sb.Append((cell.Flags & TerminalEmulator.FlagTrailer) != 0 ? ' ' : cell.Text);
            }
            return sb.ToString();
        }

        private FormattedText MakeText(string text, Typeface typeface, Brush brush)
        {
#pragma warning disable CS0618 // the pixelsPerDip overload is not available on net472 in all SDKs
            var ft = new FormattedText(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                typeface, _fontSize, brush);
#pragma warning restore CS0618
            ft.SetFontSize(_fontSize);
            return ft;
        }

        /// <summary>
        /// True when every character in <paramref name="s"/> renders with (almost exactly) the cell
        /// advance, so it can share a <see cref="FormattedText"/> run with its neighbours without the
        /// painted glyphs drifting off the grid. ASCII is free — the primary console face is
        /// monospaced and <c>_cellWidth</c> is derived from it. Non-ASCII glyphs (⏺, ✔, CJK from a
        /// fallback face…) are measured once and cached; anything whose advance deviates from the
        /// cell width is drawn cell-by-cell so the run origin stays exact.
        /// </summary>
        private bool FitsCell(string s)
        {
            foreach (char c in s)
            {
                if (c < 0x80) continue;
                if (_advanceCache.TryGetValue(c, out double adv))
                {
                    if (Math.Abs(adv - _cellWidth) > 0.1) return false;
                }
                else
                {
                    double a = MakeText(c.ToString(), _regular, _defaultFg).WidthIncludingTrailingWhitespace;
                    _advanceCache[c] = a;
                    if (Math.Abs(a - _cellWidth) > 0.1) return false;
                }
            }
            return true;
        }

        private Brush Freeze(int rgb)
        {
            if (_brushes.TryGetValue(rgb, out var cached)) return cached;
            var brush = new SolidColorBrush(Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb));
            brush.Freeze();
            _brushes[rgb] = brush;
            return brush;
        }

        /// <summary>
        /// Picks a monospaced primary font and appends CJK fallbacks. WPF treats a
        /// comma-separated FontFamily as a fallback chain, which matters because none of
        /// the Latin console fonts carry Han/Kana glyphs — without this, CJK output in an
        /// agent's TUI renders as tofu boxes.
        /// </summary>
        private static FontFamily PickFontFamily()
        {
            const string cjkFallback = "Microsoft YaHei Mono, Microsoft YaHei, MS Gothic, SimSun, Segoe UI Emoji";

            foreach (var name in new[] { "Cascadia Mono", "Consolas", "Lucida Console", "Courier New" })
            {
                try
                {
                    var probe = new Typeface(new FontFamily(name), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
                    if (probe.TryGetGlyphTypeface(out _))
                        return new FontFamily(name + ", " + cjkFallback);
                }
                catch { /* try the next candidate */ }
            }
            return new FontFamily("Consolas, " + cjkFallback);
        }
    }
}
