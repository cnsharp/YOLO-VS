using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;

#pragma warning disable VSTHRD001 // Dispatcher marshaling from the pty reader thread is intentional
#pragma warning disable VSTHRD110 // Fire-and-forget dispatcher marshal is intentional

namespace CnSharp.VSIX.Yolo
{
    /// <summary>
    /// The <see cref="ITerminalView"/> implementation used by the tool window: a
    /// WPF-rendered terminal. It owns the <see cref="TerminalSurface"/> (screen) and
    /// translates keystrokes into the byte sequences a pty expects.
    /// </summary>
    internal partial class WpfTerminalView : UserControl, ITerminalView
    {
        private readonly List<byte[]> _pending = new List<byte[]>();
        private int _flushScheduled;
        private bool _loggedFirstPaint;

        // Text-selection drag state.
        private Point _dragStart;
        private bool _mouseDown;
        private bool _dragging;
        private const double DragThreshold = 3.0;

        // Scrollbar overlay: reflects scrollback position and fades out when idle.
        private readonly DispatcherTimer _scrollFade = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1500) };
        private bool _scrollSyncing;

        public event Action<byte[]>? InputReceived;
        public event Action? Resized;

        public WpfTerminalView()
        {
            InitializeComponent();
            Screen.CellSizeChanged += OnCellSizeChanged;
            PreviewMouseWheel += OnPreviewMouseWheel;
            _scrollFade.Tick += OnScrollFadeTick;
            Loaded += (_, __) =>
            {
                Log.Write($"WpfTerminalView loaded: {Screen.Columns}x{Screen.Rows}");
                InputCapture.Focus();
            };
        }

        public int Columns => Screen.Columns;
        public int Rows => Screen.Rows;

        private void OnCellSizeChanged()
        {
            Resized?.Invoke();
            UpdateScrollBarValues();
        }

        /// <summary>
        /// Queues pty bytes and coalesces them into a single UI-thread flush per frame,
        /// so a chatty TUI cannot flood the dispatcher.
        /// </summary>
        public void WriteOutput(byte[] data)
        {
            if (data == null || data.Length == 0) return;
            lock (_pending) _pending.Add(data);

            if (Interlocked.Exchange(ref _flushScheduled, 1) == 0)
                Dispatcher.BeginInvoke(new Action(Flush), DispatcherPriority.Render);
        }

        private void Flush()
        {
            List<byte[]> batch;
            lock (_pending)
            {
                batch = new List<byte[]>(_pending);
                _pending.Clear();
            }
            Interlocked.Exchange(ref _flushScheduled, 0);

            int total = 0;
            foreach (var chunk in batch)
            {
                Screen.Emulator.Write(chunk);
                total += chunk.Length;
            }
            Screen.InvalidateVisual();
            UpdateScrollBarValues();

            if (!_loggedFirstPaint && total > 0)
            {
                _loggedFirstPaint = true;
                Log.Write($"WpfTerminalView: first paint, {total} bytes into {Screen.Columns}x{Screen.Rows}");
            }
        }

        private bool IsOnScrollBar(MouseEventArgs e)
        {
            var src = e.OriginalSource as DependencyObject;
            return src != null && ScrollBar.IsAncestorOf(src);
        }

        private void OnMouseDown(object sender, MouseButtonEventArgs e)
        {
            if (IsOnScrollBar(e)) return;
            if (e.ChangedButton == MouseButton.Left)
            {
                // Begin a (possibly zero-length) selection; we only know click-vs-drag on mouse-up, so defer
                // focus/activation until then. Capturing on Screen keeps move/up flowing even over the input box.
                // Grab keyboard focus immediately so the terminal keeps capturing keystrokes even before the
                // mouse is released (a click must not leave focus stranded).
                _dragStart = e.GetPosition(Screen);
                _mouseDown = true;
                _dragging = false;
                var hit = Screen.HitTest(_dragStart);
                if (hit.HasValue) Screen.BeginSelection(hit.Value.virtualRow, hit.Value.col);
                else Screen.ClearSelection();
                InputCapture.Focus();
                Screen.CaptureMouse();
                e.Handled = true;
                return;
            }
            InputCapture.Focus();
        }

        private void OnMouseMove(object sender, MouseEventArgs e)
        {
            if (IsOnScrollBar(e)) return;
            if (!_mouseDown || e.LeftButton != MouseButtonState.Pressed) return;
            if (!_dragging)
            {
                var pos = e.GetPosition(Screen);
                if (Math.Abs(pos.X - _dragStart.X) > DragThreshold ||
                    Math.Abs(pos.Y - _dragStart.Y) > DragThreshold)
                    _dragging = true;
            }
            if (_dragging)
            {
                var hit = Screen.HitTest(e.GetPosition(Screen));
                if (hit.HasValue) Screen.UpdateSelection(hit.Value.virtualRow, hit.Value.col);
                e.Handled = true;
            }
        }

        private void OnMouseUp(object sender, MouseButtonEventArgs e)
        {
            if (IsOnScrollBar(e)) return;
            if (e.ChangedButton != MouseButton.Left || !_mouseDown) return;
            _mouseDown = false;
            Screen.ReleaseMouseCapture();
            if (_dragging)
            {
                // A drag = selection; keep it and focus the terminal. (Copy via Ctrl+C or the context menu.)
                _dragging = false;
                InputCapture.Focus();
                e.Handled = true;
                return;
            }
            // A plain click clears any selection and focuses the terminal.
            Screen.ClearSelection();
            InputCapture.Focus();
            e.Handled = true;
        }

        private void CopySelection()
        {
            string? text = Screen.GetSelectionText();
            if (string.IsNullOrEmpty(text)) return;
            try { Clipboard.SetText(text); }
            catch (Exception ex) { Log.Write("WpfTerminalView copy failed: " + ex.Message); }
        }

        private void OnContextMenuOpening(object sender, ContextMenuEventArgs e)
        {
            CopyMenuItem.IsEnabled = Screen.HasSelection;
            PasteMenuItem.IsEnabled = Clipboard.ContainsText();
        }

        private void OnCopyMenu(object sender, RoutedEventArgs e) => CopySelection();

        private void OnPasteMenu(object sender, RoutedEventArgs e)
        {
            if (Clipboard.ContainsText())
            {
                try { Send(Clipboard.GetText()); }
                catch (Exception ex) { Log.Write("WpfTerminalView paste failed: " + ex.Message); }
            }
            e.Handled = true;
        }

        private void OnFocusChanged(object sender, KeyboardFocusChangedEventArgs e)
        {
            Screen.ShowCursor = InputCapture.IsKeyboardFocused;
            Screen.InvalidateVisual();
        }

        private void Send(string s)
        {
            if (string.IsNullOrEmpty(s)) return;
            // Any keystroke snaps the view back to the live prompt instead of typing into
            // the history that the user happened to be reviewing.
            if (!Screen.Emulator.AtBottom)
            {
                Screen.Emulator.ScrollToBottom();
                Screen.InvalidateVisual();
            }
            InputReceived?.Invoke(Encoding.UTF8.GetBytes(s));
        }

        private void OnPreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            // Wheel over the terminal reviews scrollback (unless a full-screen app owns it).
            if (Screen.Emulator.InAltScreen) return;
            int lines = e.Delta > 0 ? 3 : -3;
            Screen.Emulator.Scroll(lines);
            Screen.InvalidateVisual();
            ShowScrollBar();
            e.Handled = true;
        }

        /// <summary>
        /// Syncs the overlay scrollbar to the emulator's scrollback state. WPF's thumb
        /// size/position are derived from Minimum/Maximum/ViewportSize/Value:
        ///   Maximum = scrollable rows (HistoryCount), ViewportSize = visible rows,
        ///   Value = HistoryCount - viewOffset  (0 = top, Maximum = bottom/live).
        /// </summary>
        private void UpdateScrollBarValues()
        {
            int history = Screen.Emulator.HistoryCount;
            if (history <= 0)
            {
                ScrollBar.Visibility = Visibility.Collapsed;
                return;
            }
            _scrollSyncing = true;
            try
            {
                ScrollBar.Minimum = 0;
                ScrollBar.Maximum = history;
                ScrollBar.ViewportSize = Screen.Rows;
                ScrollBar.Value = history - Screen.Emulator.ViewOffset;
            }
            finally
            {
                _scrollSyncing = false;
            }
        }

        /// <summary>
        /// Shows the scrollbar (flashing it to full opacity) and (re)starts the idle fade.
        /// Called whenever the user scrolls or drags, so the thumb "appears on scroll".
        /// </summary>
        private void ShowScrollBar()
        {
            if (Screen.Emulator.HistoryCount <= 0) return;
            ScrollBar.Visibility = Visibility.Visible;
            ScrollBar.Opacity = 1.0;
            UpdateScrollBarValues();
            _scrollFade.Stop();
            _scrollFade.Start();
        }

        private void OnScrollFadeTick(object sender, EventArgs e)
        {
            _scrollFade.Stop();
            // Keep the thumb visible (dimmed) while scrolled back so its position shows;
            // only fully hide once the view returns to the live bottom.
            if (Screen.Emulator.AtBottom)
                ScrollBar.Visibility = Visibility.Collapsed;
            else
                ScrollBar.Opacity = 0.5;
        }

        private void OnScrollBarValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_scrollSyncing) return;
            int history = Screen.Emulator.HistoryCount;
            if (history <= 0) return;
            int offset = history - (int)Math.Round(ScrollBar.Value);
            offset = Math.Max(0, Math.Min(history, offset));
            Screen.Emulator.SetViewOffset(offset);
            Screen.InvalidateVisual();
            ShowScrollBar();
        }

        private void OnPreviewTextInput(object sender, TextCompositionEventArgs e)
        {
            // Keep the capture box empty; it exists only to receive input, never to display.
            InputCapture.Clear();
            if (string.IsNullOrEmpty(e.Text)) return;
            Send(e.Text);
            e.Handled = true;
        }

        private void OnPreviewKeyDown(object sender, KeyEventArgs e)
        {
            var mods = Keyboard.Modifiers;
            bool ctrl = (mods & ModifierKeys.Control) != 0;
            bool alt = (mods & ModifierKeys.Alt) != 0;

            // Ctrl+C copies the current selection (standard terminal behaviour) instead of sending SIGINT.
            if (ctrl && !alt && e.Key == Key.C && Screen.HasSelection)
            {
                CopySelection();
                e.Handled = true;
                return;
            }

            // Plain Space is sent directly. Relying on PreviewTextInput alone drops spaces: a
            // focused TextBox frequently raises PreviewTextInput for Space with an empty/whitespace
            // Text, so OnPreviewTextInput's IsNullOrEmpty guard silently discards it. Ctrl+Space
            // still falls through to Translate (it maps to NUL for some TUIs).
            if (e.Key == Key.Space && !ctrl && !alt)
            {
                Send(" ");
                e.Handled = true;
                return;
            }

            string? seq = Translate(e.Key, ctrl, alt, (mods & ModifierKeys.Shift) != 0, Screen.Emulator.ApplicationCursor);
            if (seq == null) return;

            Send(seq);
            e.Handled = true;
        }

        /// <summary>
        /// Maps a WPF key to the bytes a VT terminal would send. Returns null for keys
        /// that should fall through to <see cref="OnTextInput"/> (ordinary characters).
        /// </summary>
        private static string? Translate(Key key, bool ctrl, bool alt, bool shift, bool appCursor)
        {
            if (ctrl && !alt)
            {
                if (key >= Key.A && key <= Key.Z)
                    return ((char)(key - Key.A + 1)).ToString();
                switch (key)
                {
                    case Key.OemOpenBrackets: return "\x1b";
                    case Key.OemCloseBrackets: return "\x1d";
                    case Key.Oem5: return "\x1c";      // backslash
                    case Key.Space: return "\0";
                }
            }

            switch (key)
            {
                case Key.Enter: return "\r";
                case Key.Back: return "\x7f";
                case Key.Tab: return shift ? "\x1b[Z" : "\t";
                case Key.Escape: return "\x1b";
                case Key.Up: return appCursor ? "\x1bOA" : "\x1b[A";
                case Key.Down: return appCursor ? "\x1bOB" : "\x1b[B";
                case Key.Right: return appCursor ? "\x1bOC" : "\x1b[C";
                case Key.Left: return appCursor ? "\x1bOD" : "\x1b[D";
                case Key.Home: return appCursor ? "\x1bOH" : "\x1b[H";
                case Key.End: return appCursor ? "\x1bOF" : "\x1b[F";
                case Key.Insert: return "\x1b[2~";
                case Key.Delete: return "\x1b[3~";
                case Key.PageUp: return "\x1b[5~";
                case Key.PageDown: return "\x1b[6~";
                case Key.F1: return "\x1bOP";
                case Key.F2: return "\x1bOQ";
                case Key.F3: return "\x1bOR";
                case Key.F4: return "\x1bOS";
                case Key.F5: return "\x1b[15~";
                case Key.F6: return "\x1b[17~";
                case Key.F7: return "\x1b[18~";
                case Key.F8: return "\x1b[19~";
                case Key.F9: return "\x1b[20~";
                case Key.F10: return "\x1b[21~";
                case Key.F11: return "\x1b[23~";
                case Key.F12: return "\x1b[24~";
            }

            return null;
        }

        /// <summary>Moves keyboard focus into the terminal (used by the tool window).</summary>
        public void FocusTerminal()
        {
            // Keyboard.Focus moves the focus within the WPF focus scope; the TextBox.Focus()
            // call also requests Win32 focus so the VS shell hands keystrokes to the box.
            Keyboard.Focus(InputCapture);
            InputCapture.Focus();
            Log.Write($"WpfTerminalView.FocusTerminal: focused={InputCapture.IsKeyboardFocused}");
        }
    }
}
