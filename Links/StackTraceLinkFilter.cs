using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace CnSharp.VSIX.Yolo
{
    /// <summary>
    /// Makes stack-trace / traceback file references clickable — the cases <see cref="FileLinkFilter"/> does
    /// not cover because the file name has no directory component. Faithful port of IntelliJ
    /// <c>StackTraceLinkFilter</c>. Handles:
    /// <list type="bullet">
    /// <item>Java/Kotlin frames: <c>at com.foo.Bar.method(Bar.java:123)</c> → links <c>Bar.java:123</c>.</item>
    /// <item>Same-directory references: <c>Bar.kt:12</c>.</item>
    /// <item>Python/JS tracebacks: <c>File "app/main.py", line 42</c> and the single-quoted form.</item>
    /// <item>Bare file names with no line number (<c>plugin.xml</c>, <c>build.gradle.kts</c>), so a log line
    /// like "Updated plugin.xml" is clickable.</item>
    /// </list>
    /// A bare name is never linked when it is the tail of a truncated path (preceded by <c>…</c>/<c>...</c>).
    /// </summary>
    internal sealed class StackTraceLinkFilter
    {
        private sealed class Spec
        {
            public Regex Pattern = null!;
            public int FileGroup;
            public int LineGroup;
            public int ColGroup;
        }

        private static readonly Spec[] Specs =
        {
            new Spec { Pattern = YoloLinkPatterns.StackBarePattern,     FileGroup = 1, LineGroup = 2, ColGroup = 3 },
            new Spec { Pattern = YoloLinkPatterns.StackPyDqPattern,     FileGroup = 1, LineGroup = 2, ColGroup = -1 },
            new Spec { Pattern = YoloLinkPatterns.StackPySqPattern,     FileGroup = 1, LineGroup = 2, ColGroup = -1 },
            new Spec { Pattern = YoloLinkPatterns.StackBareNamePattern, FileGroup = 1, LineGroup = -1, ColGroup = -1 },
        };

        /// <param name="text">The row text to scan.</param>
        /// <param name="wrap">
        /// Wrap state written by <see cref="FileLinkFilter"/> earlier in the same pass; when it reconstructed a
        /// wrapped path here, matches overlapping that span are suppressed to avoid a duplicate (and wrong)
        /// bare-name link for the same text.
        /// </param>
        public List<LinkMatch>? Apply(string text, PathWrapState? wrap = null)
        {
            if (string.IsNullOrWhiteSpace(text) || YoloLinkPatterns.IsDiffLine(text)) return null;

            var items = new List<LinkMatch>();
            bool hasContinuation = wrap != null && wrap.HasContinuation;
            foreach (var spec in Specs)
            {
                int guard = 0;
                foreach (Match m in spec.Pattern.Matches(text))
                {
                    if (guard++ >= YoloLinkPatterns.MaxMatchesPerLine) break;

                    var fileGroup = m.Groups[spec.FileGroup];
                    string raw = fileGroup.Value;
                    // A `…`/`...` marker immediately before the file name means this match is the tail of a
                    // truncated path (e.g. `Read(src/main/kotlin/com/cnshar…Configurable.kt)`), not a real
                    // bare-file reference.
                    if (YoloLinkPatterns.IsTruncatedPathHead(text, fileGroup.Index)) continue;
                    // Already covered by FileLinkFilter's reconstructed wrapped-path link.
                    if (hasContinuation &&
                        m.Index < wrap!.ContinuationEnd - 1 &&
                        m.Index + m.Length > wrap.ContinuationStart) continue;

                    int line = spec.LineGroup >= 0 ? ParseInt(m.Groups[spec.LineGroup].Value) : 0;
                    int column = spec.ColGroup >= 0 ? ParseInt(m.Groups[spec.ColGroup].Value) : 0;

                    // A name with a directory component is resolved as a path; a bare name needs the
                    // solution-wide file-name search instead (see YoloLinkNavigator).
                    bool hasDir = raw.IndexOf('/') >= 0 || raw.IndexOf('\\') >= 0;
                    var target = new LinkTarget
                    {
                        Kind = LinkKind.File,
                        Raw = raw,
                        Line = line,
                        Column = column
                    };
                    if (hasDir) target.FilePath = raw; else target.BareFileName = raw;

                    items.Add(new LinkMatch
                    {
                        Start = m.Index,
                        End = m.Index + m.Length,
                        Target = target
                    });
                }
            }

            return items.Count == 0 ? null : items;
        }

        private static int ParseInt(string? s) => int.TryParse(s, out int v) ? v : 0;
    }
}
