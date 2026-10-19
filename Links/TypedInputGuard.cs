using System;
using System.Collections.Generic;

namespace CnSharp.VSIX.Yolo
{
    /// <summary>
    /// Tracks the text the user has typed into the embedded terminal but not yet submitted.
    ///
    /// A PTY returns one undifferentiated byte stream: the agent's output and the shell's echo of the
    /// user's keystrokes are indistinguishable by the time they reach the terminal buffer, so "is this the
    /// input line?" cannot be answered from the output side. The only reliable signal is the bytes going
    /// *to* the PTY — <see cref="OnUserInput"/> records those, and <see cref="TypedSpansIn"/> reports the
    /// ranges of a row that the pending input occupies so the link filter can exclude them.
    ///
    /// Everything else (the agent's output, the transcript) keeps its links exactly as before.
    /// Ported from the IntelliJ upstream <c>TypedInputGuard</c> / <c>InputAwareLinkFilter</c>.
    /// </summary>
    internal sealed class TypedInputGuard
    {
        private string _typed = string.Empty;
        private readonly object _lock = new object();

        /// <summary>Record input sent *to* the PTY by the user (a keystroke, key sequence, or paste).</summary>
        public void OnUserInput(string input)
        {
            if (string.IsNullOrEmpty(input)) return;
            lock (_lock)
            {
                _typed = Fold(_typed, input);
            }
        }

        /// <summary>Ranges of <paramref name="line"/> occupied by the pending typed text; empty when nothing is pending.</summary>
        public List<(int Start, int End)> TypedSpansIn(string line)
        {
            string pending;
            lock (_lock) pending = _typed;
            if (string.IsNullOrWhiteSpace(pending)) return new List<(int, int)>();

            var spans = new List<(int, int)>();
            // A multi-line input (Shift+Enter) is matched per line: the terminal renders each on its own row,
            // and `line` therefore only ever contains one of them.
            foreach (var segment in pending.Split('\n'))
            {
                if (string.IsNullOrWhiteSpace(segment)) continue;
                int from = 0;
                while (from <= line.Length - segment.Length)
                {
                    int at = line.IndexOf(segment, from, StringComparison.Ordinal);
                    if (at < 0) break;
                    spans.Add((at, at + segment.Length));
                    from = at + segment.Length;
                }
            }
            // A dropped file writes its path to the PTY without a trailing newline, so `typed` stays
            // non-empty until the next Enter. The substring match above already suppresses the real input
            // spans (the dropped path echoed on the input line); we must NOT blanket the whole line when the
            // pending text isn't found, or every agent output line printed before that Enter would lose all
            // of its links. The original blanket was meant to hide stray links during a desync — that rare
            // case may now keep a stray link under the caret, which is the lesser harm versus suppressing all
            // real output links.
            return spans;
        }

        /// <summary>Fold already-sent input into the pending text.</summary>
        private static string Fold(string typed, string input)
        {
            if (input == "\r" || input == "\n" || input == "\r\n")
                return string.Empty;                          // Enter/Return submits the input → box empty
            if (input == "\x1b\r" || input == "\x1b\n")
                return typed + "\n";                          // Esc+Enter is a newline inside the input, not a submit
            if (input.StartsWith("\x1b[200~", StringComparison.Ordinal))
                return typed + StripBracketedPaste(input);    // keep pasted body, drop \e[200~/201~ markers
            if (input.StartsWith("\x1b", StringComparison.Ordinal))
                return typed;                                 // escape sequence (arrows/fkeys) moves cursor, no text change
            if (input == "\b" || input == "\x7f")
                return typed.Length > 0 ? typed.Substring(0, typed.Length - 1) : typed; // backspace/delete
            if (input == "\x03" || input == "\x15" || input == "\x0b")
                return string.Empty;                          // Ctrl+C / Ctrl+U / Ctrl+K clear the input line
            // Ordinary typing (incl. CJK and tab) appended; any other control byte leaves pending alone.
            foreach (char c in input)
            {
                if (c != '\t' && !(c >= ' ' && c != '\x7f')) return typed;
            }
            return typed + input;
        }

        private static string StripBracketedPaste(string input)
        {
            const string start = "\x1b[200~";
            const string end = "\x1b[201~";
            int e = input.IndexOf(end, StringComparison.Ordinal);
            if (e < 0) return input.Substring(start.Length);
            return input.Substring(start.Length, e - start.Length);
        }
    }
}
