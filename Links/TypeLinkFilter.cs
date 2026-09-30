using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace CnSharp.VSIX.Yolo
{
    /// <summary>
    /// Makes type references printed by agents clickable: qualified names (<c>MyApp.Services.UserService</c>,
    /// <c>com.foo.Bar</c>, <c>foo::Bar</c>, <c>\App\Models\User</c>) and simple PascalCase names (<c>Bar</c>).
    /// This filter handles the following cases, including:
    /// <list type="bullet">
    /// <item>The <c>TypeName:line[:col]</c> "caller citation" branch (e.g. <c>UserService:182</c>), which opens
    /// the resolved type's source file at the cited line.</item>
    /// <item>Hard-wrap reconstruction that stitches a qualified name the terminal split across consecutive rows
    /// (e.g. <c>MyApp.Services.Use</c> + <c>rService</c> → <c>MyApp.Services.UserService</c>), threading
    /// <see cref="PathWrapState"/>.</item>
    /// </list>
    /// <para>
    /// A name is only linked when it is gated by <see cref="YoloProjectTypes"/> — a qualified reference by its
    /// trailing segment, a simple reference by the name itself. This gate is not optional: without it the
    /// pattern matches every capitalized word (<c>Result</c>, <c>Error</c>, the <c>Ctrl</c> in <c>Ctrl/C</c>)
    /// and paints it as a link.
    /// </para>
    /// <para>
    /// The original filter additionally has a "file-name fallback" branch for a simple name that is a source-file base
    /// name but not a class (e.g. a Kotlin file facade). That branch is unreachable here by construction: this
    /// port's type gate <i>is</i> the source-file base-name set, so any name reaching the fallback has already
    /// matched the type branch, and <see cref="YoloLinkNavigator"/> resolves both the same way (open the file
    /// whose base name matches). Emitting one link kind instead of duplicating the branch keeps the two paths
    /// from drifting.
    /// </para>
    /// </summary>
    internal sealed class TypeLinkFilter
    {
        public List<LinkMatch>? Apply(string text, YoloProjectTypes.Snapshot types, PathWrapState? wrap = null, int virtualRow = -1)
        {
            if (string.IsNullOrWhiteSpace(text) || YoloLinkPatterns.IsDiffLine(text)) return null;

            var items = new List<LinkMatch>();
            int guard = 0;

            // --- Hard-wrap reconstruction: stitch a qualified name split across physical rows. ---
            // The terminal may break a long qualified name at its width, so a head row ends with a name
            // fragment (no extension, no line). We stored it in PendingType; here we prepend it, re-match
            // TypeNamePattern on the joined text, and link only the tail portion visible on THIS row — but
            // the link target is the FULL reconstructed name, so navigation is still correct.
            string prefix = wrap?.PendingType ?? string.Empty;
            if (wrap != null) wrap.PendingType = string.Empty;
            if (prefix.Length > 0)
            {
                string trimmed = text.TrimStart();
                int leading = text.Length - trimmed.Length;
                string combined = prefix + trimmed;
                foreach (Match cm in YoloLinkPatterns.TypeNamePattern.Matches(combined))
                {
                    if (guard++ >= YoloLinkPatterns.MaxMatchesPerLine) break;
                    int mStart = cm.Index;
                    int mEnd = cm.Index + cm.Length;
                    // Only the match spanning the prefix/continuation boundary is a reconstruction.
                    if (mStart >= prefix.Length || mEnd <= prefix.Length) continue;

                    var qualified = cm.Groups["qualified"];
                    var simple = cm.Groups["simple"];
                    string? name = qualified.Success ? qualified.Value : simple.Success ? simple.Value : null;
                    if (name == null) continue;
                    if (!types.ContainsSimple(YoloLinkPatterns.LastTypeNameSegment(name))) continue;

                    int tailStart = leading;
                    int tailEnd = leading + (mEnd - prefix.Length);
                    if (tailEnd > text.Length) continue;

                    items.Add(Make(tailStart, tailEnd, name));
                    if (wrap != null)
                    {
                        // Record the reconstructed continuation tail so the same text is not also linked by
                        // the normal-pass loop below (and so StackTraceLinkFilter can suppress any overlap).
                        wrap.ContinuationStart = tailStart;
                        wrap.ContinuationEnd = tailEnd;
                    }
                    break; // at most one reconstruction per row
                }
            }
            // --- End hard-wrap reconstruction ---

            // Normal single-line type references, skipping any span the reconstruction above already linked.
            int contStart = wrap?.ContinuationStart ?? -1;
            int contEnd = wrap?.ContinuationEnd ?? -1;
            bool linkedThisLine = false;
            foreach (Match m in YoloLinkPatterns.TypeNamePattern.Matches(text))
            {
                if (guard++ >= YoloLinkPatterns.MaxMatchesPerLine) break;
                // Skip a match overlapping the reconstructed continuation tail to avoid a duplicate span.
                if (contStart >= 0 && m.Index < contEnd - 1 && m.Index + m.Length > contStart) continue;

                var qualified = m.Groups["qualified"];
                var simple = m.Groups["simple"];
                string? name = qualified.Success ? qualified.Value : simple.Success ? simple.Value : null;
                if (name == null) continue;

                // Gate on the trailing segment (see class doc): a qualified ref by its last segment, a simple
                // ref by the name itself.
                if (!types.ContainsSimple(YoloLinkPatterns.LastTypeNameSegment(name))) continue;

                items.Add(Make(m.Index, m.Index + m.Length, name));
                linkedThisLine = true;
            }

            // --- Type:line citations: `TypeName:line[:col]` (e.g. an agent "caller" citation like
            //     `UserService:182`). Link the full span and, on click, open the resolved type's source file
            //     at the given line. Gated to known project types AND known source-file base names (mirroring
            //     the bare-type fallback above) — an ordinary `Word:123` is not linked. ---
            if (!linkedThisLine)
            {
                int tlGuard = 0;
                foreach (Match tl in YoloLinkPatterns.TypeLinePattern.Matches(text))
                {
                    if (tlGuard++ >= YoloLinkPatterns.MaxMatchesPerLine) break;
                    var qualified = tl.Groups["qualified"];
                    var simple = tl.Groups["simple"];
                    bool isType = qualified.Success
                        ? types.ContainsSimple(YoloLinkPatterns.LastTypeNameSegment(qualified.Value))
                        : (simple.Success && types.ContainsSimple(simple.Value));
                    // Also accept a known source-file base name (e.g. a Kotlin file facade) so a
                    // `TypeName:line` whose name isn't an indexed class still links and opens the file.
                    bool isFile = simple.Success && types.ContainsFile(simple.Value);
                    if (!isType && !isFile) continue;
                    string? name = qualified.Success ? qualified.Value : simple.Value;
                    if (name == null) continue;
                    int line = int.TryParse(tl.Groups["line"].Value, out int ln) ? ln : 0;
                    if (line <= 0) continue;
                    int col = int.TryParse(tl.Groups["col"].Value, out int c) ? c : 0;
                    items.Add(new LinkMatch
                    {
                        Start = tl.Index,
                        End = tl.Index + tl.Length,
                        Target = new LinkTarget
                        {
                            Kind = LinkKind.Type,
                            Raw = name,
                            TypeName = name,
                            Line = line,
                            Column = col
                        }
                    });
                    linkedThisLine = true;
                }
            }
            // --- End type:line citations ---

            // Hard-wrap head detection: if a qualified name reaches end-of-line, remember it so the next row
            // can reconstruct. Skip when this row already linked a complete type, and don't hold a known
            // complete type as a pending prefix (it is not a wrap, and holding it would risk false links on the
            // next line) unless it ends in a separator (clearly incomplete).
            if (!linkedThisLine && wrap != null)
            {
                var head = YoloLinkPatterns.TypeHeadPattern.Match(text);
                if (head.Success)
                {
                    var headNameGroup = head.Groups["qualified"];
                    string? headName = headNameGroup.Success ? headNameGroup.Value : null;
                    if (!string.IsNullOrEmpty(headName))
                    {
                        string lastSeg = YoloLinkPatterns.LastTypeNameSegment(headName!);
                        bool endsWithSep = headName!.EndsWith('.') || headName.EndsWith(':');
                        if (endsWithSep || !types.ContainsSimple(lastSeg))
                            wrap.PendingType = headName!;
                    }
                }
            }

            return items.Count == 0 ? null : items;
        }

        private static LinkMatch Make(int start, int end, string name) => new LinkMatch
        {
            Start = start,
            End = end,
            Target = new LinkTarget
            {
                Kind = LinkKind.Type,
                Raw = name,
                TypeName = name
            }
        };
    }
}
