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
        private readonly StringBuilder _run = new StringBuilder();

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

        public TerminalSurface()
        {
            var family = PickFontFamily();
            _regular = new Typeface(family, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
            _bold = new Typeface(family, FontStyles.Normal, FontWeights.Bold, FontStretches.Normal);

            _defaultBg = Freeze(TerminalPalette.DefaultBackground);
            _defaultFg = Freeze(TerminalPalette.DefaultForeground);
            _cursorBrush = Freeze(0xAEAFAD);

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
                    else
                    {
                        len = 1;
                        while (x + len < cols)
                        {
                            var nextCell = em.VirtualCell(topRow + y, x + len);
                            if (nextCell.Fg != fg || nextCell.Bg != bg || nextCell.Flags != flags) break;
                            if ((nextCell.Flags & (TerminalEmulator.FlagWide | TerminalEmulator.FlagTrailer)) != 0) break;
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

        private FormattedText MakeText(string text, Typeface typeface, Brush brush)
        {
#pragma warning disable CS0618 // the pixelsPerDip overload is not available on net472 in all SDKs
            var ft = new FormattedText(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                typeface, _fontSize, brush);
#pragma warning restore CS0618
            ft.SetFontSize(_fontSize);
            return ft;
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
