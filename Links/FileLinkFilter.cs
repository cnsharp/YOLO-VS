using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace CnSharp.VSIX.Yolo
{
    /// <summary>
    /// Makes file references printed by agents clickable: <c>path</c>, <c>path:line</c>,
    /// <c>path:line:column</c>, <c>path:line-line</c> (range) and quoted paths with embedded spaces.
    /// Faithful port of IntelliJ <c>FileLinkFilter</c>. Resolution to a real file is deferred to click time
    /// (<see cref="YoloLinkNavigator"/>) so streaming output is never blocked on filesystem I/O; this filter
    /// only parses the reference and applies the truncated-path / diff-line guards.
    /// </summary>
    internal sealed class FileLinkFilter
    {
        /// <param name="text">The row text to scan.</param>
        /// <param name="wrap">
        /// Wrapped-path state. On entry <see cref="PathWrapState.PendingPrefix"/> holds the previous row's
        /// dangling path fragment (if any); on exit the continuation span is recorded for
        /// <see cref="StackTraceLinkFilter"/>. May be null to disable wrap handling.
        /// </param>
        public List<LinkMatch>? Apply(string text, PathWrapState? wrap = null)
        {
            if (string.IsNullOrWhiteSpace(text) || YoloLinkPatterns.IsDiffLine(text))
            {
                // A blank or diff row breaks any pending wrap sequence.
                if (wrap != null)
                {
                    wrap.PendingPrefix = string.Empty;
                    wrap.ContinuationStart = wrap.ContinuationEnd = -1;
                }
                return null;
            }

            var items = new List<LinkMatch>();

            // --- Hard-wrap path reconstruction ---
            // When a long path wraps at the terminal width, the head row ends with a path fragment that has
            // no extension and no :line (kept in PendingPrefix). Here we prepend that prefix, re-match
            // PathPattern on the joined text, and link only the tail portion that is visible on this row —
            // but the link target is the FULL reconstructed path, so navigation is still correct.
            string prefix = wrap?.PendingPrefix ?? string.Empty;
            if (wrap != null)
            {
                wrap.PendingPrefix = string.Empty;
                wrap.ContinuationStart = wrap.ContinuationEnd = -1;
            }

            if (prefix.Length > 0)
            {
                string trimmed = text.TrimStart();
                int leading = text.Length - trimmed.Length;
                string combined = prefix + trimmed;
                foreach (Match cm in YoloLinkPatterns.PathPattern.Matches(combined))
                {
                    int matchStart = cm.Groups[1].Index;
                    int matchEnd = cm.Index + cm.Length;
                    // Only matches that straddle the prefix/continuation boundary are reconstructions.
                    if (matchStart >= prefix.Length || matchEnd <= prefix.Length) continue;

                    string rawC = cm.Groups[1].Value;
                    bool hasExtC = cm.Groups[2].Success;
                    bool hasLineC = cm.Groups[3].Success;
                    // Still no extension / line: the path wraps again. Chained (3+ row) wraps are out of
                    // scope here — see PathWrapState for why.
                    if (!hasExtC && !hasLineC) continue;
                    if (rawC.Contains("…") || rawC.Contains("...") ||
                        YoloLinkPatterns.IsTruncatedPath(combined, cm.Groups[1].Index + cm.Groups[1].Length))
                        continue;

                    int lineC = ParseInt(cm.Groups[3].Value);
                    int colC = ParseInt(cm.Groups[5].Value);
                    // Map the tail portion back to positions within `text`.
                    int tailStart = leading;
                    int tailEnd = leading + (matchEnd - prefix.Length);
                    if (tailEnd > text.Length) continue;

                    items.Add(Make(tailStart, tailEnd, rawC, lineC, colC));
                    if (wrap != null)
                    {
                        wrap.ContinuationStart = tailStart;
                        wrap.ContinuationEnd = tailEnd;
                    }
                }
            }
            // --- End hard-wrap reconstruction ---

            // Quoted paths first (may contain spaces, e.g. `"/path with space/Bar.kt":5`). Their full spans
            // are recorded so the unquoted pass below can suppress a sub-path that falls inside the quotes —
            // `space/Bar.kt` inside `"/path with space/Bar.kt"` would otherwise be linked twice.
            var quotedSpans = new List<(int Start, int End)>();
            foreach (Match m in YoloLinkPatterns.QuotedPathPattern.Matches(text))
            {
                var pathGroup = m.Groups[2];
                string raw = pathGroup.Value;
                if (raw.Contains("…") || raw.Contains("...") ||
                    YoloLinkPatterns.IsTruncatedPath(text, pathGroup.Index + pathGroup.Length))
                    continue;

                int line = ParseInt(m.Groups[3].Value);
                int column = ParseInt(m.Groups[4].Value);
                // Span the path only — the quotes must never be painted as a link. When a `:line[:col]`
                // follows the closing quote, add it as a SECOND span so the whole reference is clickable
                // without colouring the quote (a single span cannot skip the quote in the middle).
                items.Add(Make(pathGroup.Index, pathGroup.Index + pathGroup.Length, raw, line, column));
                if (m.Groups[3].Success)
                    items.Add(Make(m.Groups[3].Index - 1, m.Index + m.Length, raw, line, column));
                quotedSpans.Add((m.Index, m.Index + m.Length));
            }

            // Standard (unquoted) path references.
            foreach (Match m in YoloLinkPatterns.PathPattern.Matches(text))
            {
                int start = m.Groups[1].Index;
                int end = m.Index + m.Length;
                // Skip a match that lies inside a quoted path (see quotedSpans above).
                if (quotedSpans.Exists(s => start < s.End && end > s.Start)) continue;
                // Skip the tail of a truncated path: a `…`/`...` immediately before the path start, e.g.
                // `Read(src/main/kotlin/com/cnshar…/real/Bar.kt)`. The agent abbreviated the middle, so the
                // fragment after the marker is not a real file.
                if (YoloLinkPatterns.IsTruncatedPathHead(text, start)) continue;

                string raw = m.Groups[1].Value;
                bool hasExt = m.Groups[2].Success;
                bool hasLine = m.Groups[3].Success;
                if (!hasExt && !hasLine)
                {
                    // Extension-less, line-less: either a directory reference (not openable) or the head
                    // fragment of a hard-wrapped path. When it reaches the end of the row content, remember
                    // it so the NEXT row can reconstruct the full path.
                    // Guard: only when the last segment has no dot. A dot there means the wrap split inside
                    // the extension (e.g. `build.gradl` for `.gradle`) and the continuation row would carry
                    // only the remaining extension chars, producing a 1-2 character phantom link.
                    continue;
                }
                if (raw.Contains("…") || raw.Contains("...") ||
                    YoloLinkPatterns.IsTruncatedPath(text, start + m.Groups[1].Length))
                    continue;

                int line = ParseInt(m.Groups[3].Value);
                int column = ParseInt(m.Groups[5].Value);
                // Group 1 already carries the full path INCLUDING its extension; never re-append the
                // extension group or the target becomes a doubled `Foo.kt.kt` that resolves to nothing.
                // Span the whole reference (path + optional :line:column) so a click anywhere navigates.
                items.Add(Make(start, end, raw, line, column));
            }

            return items.Count == 0 ? null : items;
        }

        /// <summary>
        /// The dangling path fragment at the end of <paramref name="previousLine"/>, i.e. the value IntelliJ
        /// would have left in <see cref="PathWrapState.PendingPrefix"/> after filtering that line. Returns an
        /// empty string when the line does not end in a wrapped path head. Computing this from the previous
        /// row (instead of carrying mutable state) is what makes link detection order-independent.
        /// </summary>
        public static string ComputePendingPrefix(string previousLine)
        {
            if (string.IsNullOrWhiteSpace(previousLine) || YoloLinkPatterns.IsDiffLine(previousLine))
                return string.Empty;

            foreach (Match m in YoloLinkPatterns.PathPattern.Matches(previousLine))
            {
                if (m.Groups[2].Success || m.Groups[3].Success) continue;  // complete reference, not a head
                int end = m.Index + m.Length;
                if (end < previousLine.Length && previousLine.Substring(end).Trim().Length > 0) continue;

                string raw = m.Groups[1].Value;
                int lastSep = raw.LastIndexOfAny(new[] { '/', '\\' });
                string lastSegment = lastSep >= 0 ? raw.Substring(lastSep + 1) : raw;
                if (lastSegment.IndexOf('.') >= 0) continue;  // wrap split inside the extension
                return raw;
            }
            return string.Empty;
        }

        private static LinkMatch Make(int start, int end, string raw, int line, int column) => new LinkMatch
        {
            Start = start,
            End = end,
            Target = new LinkTarget
            {
                Kind = LinkKind.File,
                Raw = raw,
                FilePath = raw,
                Line = line,
                Column = column
            }
        };

        private static int ParseInt(string? s) => int.TryParse(s, out int v) ? v : 0;
    }
}
